using System.Text.Json;
using System.Data.Common;
using DenCore.Models;

namespace DenCore.Data;

public interface IAgentWorkspaceRepository
{
    Task<AgentWorkspace> UpsertAsync(AgentWorkspace workspace);
    Task<AgentWorkspace?> GetAsync(string id, string? projectId = null);
    Task<List<AgentWorkspace>> ListAsync(AgentWorkspaceListOptions options);
}

public sealed class AgentWorkspaceRepository : IAgentWorkspaceRepository
{
    private const string Columns = """
        id, project_id, task_id, branch, worktree_path, base_branch,
        base_commit, head_commit, state, created_by_run_id, dev_server_url,
        preview_url, cleanup_policy, changed_file_summary, created_at, updated_at
        """;

    private readonly DbConnectionFactory _db;

    public AgentWorkspaceRepository(DbConnectionFactory db) => _db = db;

    public async Task<AgentWorkspace> UpsertAsync(AgentWorkspace workspace)
    {
        Validate(workspace);

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await EnsureTaskBelongsToProjectAsync(conn, workspace.TaskId, workspace.ProjectId);
        var existingId = await FindExistingIdAsync(conn, workspace);
        AgentWorkspace result;
        if (existingId is null)
        {
            result = await InsertAsync(conn, workspace);
        }
        else
        {
            workspace.Id = existingId;
            result = await UpdateAsync(conn, workspace);
        }

        await tx.CommitAsync();
        return result;
    }

    public async Task<AgentWorkspace?> GetAsync(string id, string? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM agent_workspaces
            WHERE id = @id
              AND (@projectId IS NULL OR project_id = @projectId)
            """;
        cmd.AddParameterWithValue("@id", id.Trim());
        cmd.AddParameterWithValue("@projectId", (object?)projectId ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadWorkspace(reader) : null;
    }

    public async Task<List<AgentWorkspace>> ListAsync(AgentWorkspaceListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.AddParameterWithValue("@projectId", options.ProjectId);
        }

        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.AddParameterWithValue("@taskId", options.TaskId.Value);
        }

        if (options.State is not null)
        {
            where.Add("state = @state");
            cmd.AddParameterWithValue("@state", options.State.Value.ToDbValue());
        }

        var whereClause = where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM agent_workspaces
            {whereClause}
            ORDER BY updated_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.AddParameterWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var result = new List<AgentWorkspace>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(ReadWorkspace(reader));
        return result;
    }

    private static async Task EnsureTaskBelongsToProjectAsync(DbConnection conn, int taskId, string projectId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM tasks WHERE id = @taskId";
        cmd.AddParameterWithValue("@taskId", taskId);
        var result = await cmd.ExecuteScalarAsync();
        if (result is not string taskProjectId)
            throw new InvalidOperationException($"Task {taskId} was not found.");
        if (!string.Equals(taskProjectId, projectId.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Task {taskId} belongs to project '{taskProjectId}', not '{projectId.Trim()}'.");
    }

    private static async Task<string?> FindExistingIdAsync(DbConnection conn, AgentWorkspace workspace)
    {
        await using var byId = conn.CreateCommand();
        byId.CommandText = "SELECT id, project_id FROM agent_workspaces WHERE id = @id";
        byId.AddParameterWithValue("@id", workspace.Id.Trim());
        await using (var idReader = await byId.ExecuteReaderAsync())
        {
            if (await idReader.ReadAsync())
            {
                var existingProjectId = idReader.GetString(1);
                if (!string.Equals(existingProjectId, workspace.ProjectId.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Agent workspace id '{workspace.Id}' already belongs to project '{existingProjectId}'.");
                return idReader.GetString(0);
            }
        }

        await using var byTuple = conn.CreateCommand();
        byTuple.CommandText = """
            SELECT id FROM agent_workspaces
            WHERE project_id = @projectId AND task_id = @taskId AND branch = @branch
            """;
        byTuple.AddParameterWithValue("@projectId", workspace.ProjectId.Trim());
        byTuple.AddParameterWithValue("@taskId", workspace.TaskId);
        byTuple.AddParameterWithValue("@branch", workspace.Branch.Trim());
        var tupleResult = await byTuple.ExecuteScalarAsync();
        return tupleResult as string;
    }

    private static async Task<AgentWorkspace> InsertAsync(DbConnection conn, AgentWorkspace workspace)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO agent_workspaces (
                id, project_id, task_id, branch, worktree_path, base_branch,
                base_commit, head_commit, state, created_by_run_id, dev_server_url,
                preview_url, cleanup_policy, changed_file_summary
            ) VALUES (
                @id, @projectId, @taskId, @branch, @worktreePath, @baseBranch,
                @baseCommit, @headCommit, @state, @createdByRunId, @devServerUrl,
                @previewUrl, @cleanupPolicy, @changedFileSummary
            )
            RETURNING {Columns}
            """;
        AddParameters(cmd, workspace);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadWorkspace(reader);
    }

    private static async Task<AgentWorkspace> UpdateAsync(DbConnection conn, AgentWorkspace workspace)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE agent_workspaces SET
                project_id = @projectId,
                task_id = @taskId,
                branch = @branch,
                worktree_path = @worktreePath,
                base_branch = @baseBranch,
                base_commit = @baseCommit,
                head_commit = @headCommit,
                state = @state,
                created_by_run_id = @createdByRunId,
                dev_server_url = @devServerUrl,
                preview_url = @previewUrl,
                cleanup_policy = @cleanupPolicy,
                changed_file_summary = @changedFileSummary,
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING {Columns}
            """;
        AddParameters(cmd, workspace);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadWorkspace(reader);
    }

    private static void AddParameters(DbCommand cmd, AgentWorkspace workspace)
    {
        cmd.AddParameterWithValue("@id", workspace.Id.Trim());
        cmd.AddParameterWithValue("@projectId", workspace.ProjectId.Trim());
        cmd.AddParameterWithValue("@taskId", workspace.TaskId);
        cmd.AddParameterWithValue("@branch", workspace.Branch.Trim());
        cmd.AddParameterWithValue("@worktreePath", workspace.WorktreePath.Trim());
        cmd.AddParameterWithValue("@baseBranch", workspace.BaseBranch.Trim());
        cmd.AddParameterWithValue("@baseCommit", NullIfWhiteSpace(workspace.BaseCommit));
        cmd.AddParameterWithValue("@headCommit", NullIfWhiteSpace(workspace.HeadCommit));
        cmd.AddParameterWithValue("@state", workspace.State.ToDbValue());
        cmd.AddParameterWithValue("@createdByRunId", NullIfWhiteSpace(workspace.CreatedByRunId));
        cmd.AddParameterWithValue("@devServerUrl", NullIfWhiteSpace(workspace.DevServerUrl));
        cmd.AddParameterWithValue("@previewUrl", NullIfWhiteSpace(workspace.PreviewUrl));
        cmd.AddParameterWithValue("@cleanupPolicy", workspace.CleanupPolicy.ToDbValue());
        cmd.AddParameterWithValue("@changedFileSummary", workspace.ChangedFileSummary is null
            ? DBNull.Value
            : JsonSerializer.Serialize(workspace.ChangedFileSummary.Value));
    }

    private static object NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static AgentWorkspace ReadWorkspace(DbDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ProjectId = reader.GetString(1),
        TaskId = reader.GetInt32(2),
        Branch = reader.GetString(3),
        WorktreePath = reader.GetString(4),
        BaseBranch = reader.GetString(5),
        BaseCommit = reader.IsDBNull(6) ? null : reader.GetString(6),
        HeadCommit = reader.IsDBNull(7) ? null : reader.GetString(7),
        State = EnumExtensions.ParseAgentWorkspaceState(reader.GetString(8)),
        CreatedByRunId = reader.IsDBNull(9) ? null : reader.GetString(9),
        DevServerUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
        PreviewUrl = reader.IsDBNull(11) ? null : reader.GetString(11),
        CleanupPolicy = EnumExtensions.ParseAgentWorkspaceCleanupPolicy(reader.GetString(12)),
        ChangedFileSummary = reader.IsDBNull(13) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(13)).Clone(),
        CreatedAt = DateTime.Parse(reader.GetString(14)),
        UpdatedAt = DateTime.Parse(reader.GetString(15))
    };

    private static void Validate(AgentWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Id))
            throw new ArgumentException("Workspace id is required.", nameof(workspace));
        if (string.IsNullOrWhiteSpace(workspace.ProjectId))
            throw new ArgumentException("Project id is required.", nameof(workspace));
        if (workspace.TaskId <= 0)
            throw new ArgumentException("Task id must be positive.", nameof(workspace));
        if (string.IsNullOrWhiteSpace(workspace.Branch))
            throw new ArgumentException("Branch is required.", nameof(workspace));
        if (string.IsNullOrWhiteSpace(workspace.WorktreePath))
            throw new ArgumentException("Worktree path is required.", nameof(workspace));
        if (string.IsNullOrWhiteSpace(workspace.BaseBranch))
            throw new ArgumentException("Base branch is required.", nameof(workspace));
    }
}
