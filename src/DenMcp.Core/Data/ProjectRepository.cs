using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Core.Data;

public interface IProjectRepository
{
    Task<Project> CreateAsync(Project project);
    Task<Project?> GetByIdAsync(string id);
    Task<List<Project>> GetAllAsync();
    Task<List<Project>> ListAsync(string? kind = null, bool includeHidden = false, bool includeArchived = false);
    Task<ProjectWithStats> GetWithStatsAsync(string id, string? agent = null);
}

public sealed class ProjectRepository : IProjectRepository
{
    private readonly DbConnectionFactory _db;

    public ProjectRepository(DbConnectionFactory db) => _db = db;

    public async Task<Project> CreateAsync(Project project)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, name, kind, visibility, owner, root_path, description, settings_json)
            VALUES (@id, @name, @kind, @visibility, @owner, @rootPath, @description, @settingsJson)
            RETURNING id, name, kind, visibility, owner, root_path, description, settings_json, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", project.Id);
        cmd.Parameters.AddWithValue("@name", project.Name);
        cmd.Parameters.AddWithValue("@kind", project.Kind);
        cmd.Parameters.AddWithValue("@visibility", project.Visibility);
        cmd.Parameters.AddWithValue("@owner", (object?)project.Owner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rootPath", (object?)project.RootPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description", (object?)project.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@settingsJson", (object?)project.SettingsJson ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadProject(reader);
    }

    public async Task<Project?> GetByIdAsync(string id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, kind, visibility, owner, root_path, description, settings_json, created_at, updated_at FROM projects WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadProject(reader) : null;
    }

    public async Task<List<Project>> GetAllAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, kind, visibility, owner, root_path, description, settings_json, created_at, updated_at FROM projects ORDER BY id";

        await using var reader = await cmd.ExecuteReaderAsync();
        var projects = new List<Project>();
        while (await reader.ReadAsync())
            projects.Add(ReadProject(reader));
        return projects;
    }

    public async Task<List<Project>> ListAsync(string? kind = null, bool includeHidden = false, bool includeArchived = false)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (kind is not null)
            conditions.Add("kind = @kind");
        if (!includeHidden)
            conditions.Add("visibility != 'hidden'");
        if (!includeArchived)
            conditions.Add("visibility != 'archived'");

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"SELECT id, name, kind, visibility, owner, root_path, description, settings_json, created_at, updated_at FROM projects {whereClause} ORDER BY id";

        if (kind is not null)
            cmd.Parameters.AddWithValue("@kind", kind);

        await using var reader = await cmd.ExecuteReaderAsync();
        var projects = new List<Project>();
        while (await reader.ReadAsync())
            projects.Add(ReadProject(reader));
        return projects;
    }

    public async Task<ProjectWithStats> GetWithStatsAsync(string id, string? agent = null)
    {
        await using var conn = await _db.CreateConnectionAsync();

        await using var projCmd = conn.CreateCommand();
        projCmd.CommandText = "SELECT id, name, kind, visibility, owner, root_path, description, settings_json, created_at, updated_at FROM projects WHERE id = @id";
        projCmd.Parameters.AddWithValue("@id", id);

        await using var projReader = await projCmd.ExecuteReaderAsync();
        if (!await projReader.ReadAsync())
            throw new KeyNotFoundException($"Project '{id}' not found");

        var project = ReadProject(projReader);
        await projReader.CloseAsync();

        // Task counts by status
        await using var statsCmd = conn.CreateCommand();
        statsCmd.CommandText = "SELECT status, COUNT(*) FROM tasks WHERE project_id = @id GROUP BY status";
        statsCmd.Parameters.AddWithValue("@id", id);

        var counts = new Dictionary<TaskStatus, int>();
        foreach (TaskStatus s in Enum.GetValues<TaskStatus>())
            counts[s] = 0;

        await using var statsReader = await statsCmd.ExecuteReaderAsync();
        while (await statsReader.ReadAsync())
        {
            var status = EnumExtensions.ParseTaskStatus(statsReader.GetString(0));
            counts[status] = statsReader.GetInt32(1);
        }
        await statsReader.CloseAsync();

        // Unread message count for agent
        var unread = 0;
        if (agent is not null)
        {
            await using var unreadCmd = conn.CreateCommand();
            unreadCmd.CommandText = """
                SELECT COUNT(*) FROM messages m
                WHERE m.project_id = @id
                  AND NOT EXISTS (
                    SELECT 1 FROM message_reads mr
                    WHERE mr.message_id = m.id AND mr.agent = @agent
                  )
                  AND m.sender != @agent
                """;
            unreadCmd.Parameters.AddWithValue("@id", id);
            unreadCmd.Parameters.AddWithValue("@agent", agent);
            unread = Convert.ToInt32(await unreadCmd.ExecuteScalarAsync());
        }

        return new ProjectWithStats
        {
            Project = project,
            TaskCountsByStatus = counts,
            UnreadMessageCount = unread
        };
    }

    private static Project ReadProject(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Kind = reader.GetString(2),
        Visibility = reader.GetString(3),
        Owner = reader.IsDBNull(4) ? null : reader.GetString(4),
        RootPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        Description = reader.IsDBNull(6) ? null : reader.GetString(6),
        SettingsJson = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAt = DateTime.Parse(reader.GetString(8)),
        UpdatedAt = DateTime.Parse(reader.GetString(9))
    };
}
