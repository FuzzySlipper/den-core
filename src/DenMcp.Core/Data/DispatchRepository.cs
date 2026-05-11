using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IDispatchRepository
{
    /// <summary>
    /// Create a dispatch entry if no pending entry with the same dedup key exists.
    /// Returns the entry (existing or new), and whether it was newly created.
    /// </summary>
    Task<(DispatchEntry Entry, bool Created)> CreateIfAbsentAsync(DispatchEntry entry);
    Task<DispatchEntry?> GetByIdAsync(int id);
    Task<List<DispatchEntry>> ListAsync(string? projectId = null, string? targetAgent = null,
        DispatchStatus[]? statuses = null);
    Task<DispatchEntry> ApproveAsync(int id, string decidedBy);
    Task<DispatchEntry> RejectAsync(int id, string decidedBy);
    Task<DispatchEntry> CompleteAsync(int id, string? completedBy = null);
    Task<DispatchEntry> ExpireAsync(int id);
    Task<int> ExpireOpenForTaskAsync(string projectId, int taskId, int? excludeId = null);
    Task<int> ExpireSupersededForTaskTargetAsync(string projectId, int taskId, string targetAgent, int keepId);
    Task<int> ExpireStaleAsync(DateTime now);
    Task<int> GetPendingCountAsync(string? projectId = null);
}

public sealed class DispatchRepository : IDispatchRepository
{
    private readonly DbConnectionFactory _db;

    public DispatchRepository(DbConnectionFactory db) => _db = db;

    public async Task<(DispatchEntry Entry, bool Created)> CreateIfAbsentAsync(DispatchEntry entry)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Try insert; the unique partial index on (dedup_key) WHERE status='pending'
        // will reject duplicates atomically.
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO dispatch_entries
                (project_id, target_agent, status, trigger_type, trigger_id, task_id,
                 summary, context_prompt, context_json, dedup_key, expires_at)
            VALUES
                (@projectId, @targetAgent, @status, @triggerType, @triggerId, @taskId,
                 @summary, @contextPrompt, @contextJson, @dedupKey, @expiresAt)
            RETURNING id, project_id, target_agent, status, trigger_type, trigger_id,
                      task_id, summary, context_prompt, context_json, dedup_key,
                      created_at, expires_at, decided_at, completed_at, decided_by, completed_by
            """;
        AddCreateParams(insertCmd, entry);

        try
        {
            await using var reader = await insertCmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            return (ReadEntry(reader), true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            // Only treat as dedup if a pending entry with this key actually exists.
            // Other constraint violations (FK on project_id, task_id, etc.) should propagate.
            var existing = await GetByDedupKeyAsync(conn, entry.DedupKey);
            if (existing is null)
                throw; // Not a dedup hit — rethrow the real constraint error
            return (existing, false);
        }
    }

    public async Task<DispatchEntry?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEntry(reader) : null;
    }

    public async Task<List<DispatchEntry>> ListAsync(string? projectId = null,
        string? targetAgent = null, DispatchStatus[]? statuses = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();

        if (projectId is not null)
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", projectId);
        }

        if (targetAgent is not null)
        {
            where.Add("target_agent = @targetAgent");
            cmd.Parameters.AddWithValue("@targetAgent", targetAgent);
        }

        if (statuses is { Length: > 0 })
        {
            var placeholders = new List<string>();
            for (var i = 0; i < statuses.Length; i++)
            {
                var p = $"@status{i}";
                placeholders.Add(p);
                cmd.Parameters.AddWithValue(p, statuses[i].ToDbValue());
            }
            where.Add($"status IN ({string.Join(", ", placeholders)})");
        }

        var whereClause = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = SelectColumns + whereClause + " ORDER BY created_at DESC";

        var results = new List<DispatchEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadEntry(reader));
        return results;
    }

    public async Task<DispatchEntry> ApproveAsync(int id, string decidedBy)
        => await TransitionAsync(id, DispatchStatus.Pending, DispatchStatus.Approved, decidedBy);

    public async Task<DispatchEntry> RejectAsync(int id, string decidedBy)
        => await TransitionAsync(id, DispatchStatus.Pending, DispatchStatus.Rejected, decidedBy);

    public async Task<DispatchEntry> CompleteAsync(int id, string? completedBy = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = @newStatus, completed_at = datetime('now'), completed_by = @completedBy
            WHERE id = @id AND status = @requiredStatus
            RETURNING id, project_id, target_agent, status, trigger_type, trigger_id,
                      task_id, summary, context_prompt, context_json, dedup_key,
                      created_at, expires_at, decided_at, completed_at, decided_by, completed_by
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@newStatus", DispatchStatus.Completed.ToDbValue());
        cmd.Parameters.AddWithValue("@requiredStatus", DispatchStatus.Approved.ToDbValue());
        cmd.Parameters.AddWithValue("@completedBy", (object?)completedBy ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(
                $"Dispatch {id} cannot transition to completed (must be approved)");
        return ReadEntry(reader);
    }

    public async Task<DispatchEntry> ExpireAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = @newStatus
            WHERE id = @id AND status IN ('pending', 'approved')
            RETURNING id, project_id, target_agent, status, trigger_type, trigger_id,
                      task_id, summary, context_prompt, context_json, dedup_key,
                      created_at, expires_at, decided_at, completed_at, decided_by, completed_by
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@newStatus", DispatchStatus.Expired.ToDbValue());

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(
                $"Dispatch {id} cannot transition to expired (must be pending or approved)");
        return ReadEntry(reader);
    }

    public async Task<int> ExpireOpenForTaskAsync(string projectId, int taskId, int? excludeId = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = 'expired'
            WHERE project_id = @projectId
              AND task_id = @taskId
              AND status IN ('pending', 'approved')
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@taskId", taskId);

        if (excludeId is not null)
        {
            cmd.CommandText += " AND id != @excludeId";
            cmd.Parameters.AddWithValue("@excludeId", excludeId.Value);
        }

        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> ExpireSupersededForTaskTargetAsync(
        string projectId,
        int taskId,
        string targetAgent,
        int keepId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = 'expired'
            WHERE project_id = @projectId
              AND task_id = @taskId
              AND target_agent = @targetAgent
              AND status IN ('pending', 'approved')
              AND id < @keepId
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@targetAgent", targetAgent);
        cmd.Parameters.AddWithValue("@keepId", keepId);

        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> ExpireStaleAsync(DateTime now)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = 'expired'
            WHERE status = 'pending' AND expires_at <= @now
            """;
        cmd.Parameters.AddWithValue("@now", now.ToString("yyyy-MM-dd HH:mm:ss"));
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetPendingCountAsync(string? projectId = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        if (projectId is not null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM dispatch_entries WHERE status = 'pending' AND project_id = @projectId";
            cmd.Parameters.AddWithValue("@projectId", projectId);
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM dispatch_entries WHERE status = 'pending'";
        }

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private async Task<DispatchEntry> TransitionAsync(int id, DispatchStatus from, DispatchStatus to, string decidedBy)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = @newStatus, decided_at = datetime('now'), decided_by = @decidedBy
            WHERE id = @id AND status = @requiredStatus
            RETURNING id, project_id, target_agent, status, trigger_type, trigger_id,
                      task_id, summary, context_prompt, context_json, dedup_key,
                      created_at, expires_at, decided_at, completed_at, decided_by, completed_by
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@newStatus", to.ToDbValue());
        cmd.Parameters.AddWithValue("@requiredStatus", from.ToDbValue());
        cmd.Parameters.AddWithValue("@decidedBy", decidedBy);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(
                $"Dispatch {id} cannot transition from {from} to {to}");
        return ReadEntry(reader);
    }

    private static async Task<DispatchEntry?> GetByDedupKeyAsync(SqliteConnection conn, string dedupKey)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE dedup_key = @dedupKey AND status = 'pending'";
        cmd.Parameters.AddWithValue("@dedupKey", dedupKey);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEntry(reader) : null;
    }

    private static void AddCreateParams(SqliteCommand cmd, DispatchEntry entry)
    {
        cmd.Parameters.AddWithValue("@projectId", entry.ProjectId);
        cmd.Parameters.AddWithValue("@targetAgent", entry.TargetAgent);
        cmd.Parameters.AddWithValue("@status", entry.Status.ToDbValue());
        cmd.Parameters.AddWithValue("@triggerType", entry.TriggerType.ToDbValue());
        cmd.Parameters.AddWithValue("@triggerId", entry.TriggerId);
        cmd.Parameters.AddWithValue("@taskId", (object?)entry.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@summary", (object?)entry.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@contextPrompt", (object?)entry.ContextPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@contextJson", (object?)entry.ContextJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dedupKey", entry.DedupKey);
        cmd.Parameters.AddWithValue("@expiresAt", entry.ExpiresAt.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private const string SelectColumns = """
        SELECT id, project_id, target_agent, status, trigger_type, trigger_id,
               task_id, summary, context_prompt, context_json, dedup_key,
               created_at, expires_at, decided_at, completed_at, decided_by, completed_by
        FROM dispatch_entries
        """;

    internal static DispatchEntry ReadEntry(SqliteDataReader reader)
    {
        return new DispatchEntry
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            TargetAgent = reader.GetString(2),
            Status = EnumExtensions.ParseDispatchStatus(reader.GetString(3)),
            TriggerType = EnumExtensions.ParseDispatchTriggerType(reader.GetString(4)),
            TriggerId = reader.GetInt32(5),
            TaskId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
            ContextPrompt = reader.IsDBNull(8) ? null : reader.GetString(8),
            ContextJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            DedupKey = reader.GetString(10),
            CreatedAt = DateTime.Parse(reader.GetString(11)),
            ExpiresAt = DateTime.Parse(reader.GetString(12)),
            DecidedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
            CompletedAt = reader.IsDBNull(14) ? null : DateTime.Parse(reader.GetString(14)),
            DecidedBy = reader.IsDBNull(15) ? null : reader.GetString(15),
            CompletedBy = reader.IsDBNull(16) ? null : reader.GetString(16)
        };
    }
}
