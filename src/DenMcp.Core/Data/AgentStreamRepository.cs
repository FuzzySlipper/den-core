using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IAgentStreamRepository
{
    Task<AgentStreamEntry> AppendAsync(AgentStreamEntry entry);
    Task<AgentStreamEntry?> GetByIdAsync(int id);
    Task<Dictionary<int, AgentStreamEntry>> GetByIdsAsync(IReadOnlyList<int> ids);
    Task<List<AgentStreamEntry>> ListAsync(AgentStreamListOptions? options = null);
}

public sealed class AgentStreamRepository : IAgentStreamRepository
{
    private readonly DbConnectionFactory _db;

    public AgentStreamRepository(DbConnectionFactory db) => _db = db;

    public async Task<AgentStreamEntry> AppendAsync(AgentStreamEntry entry)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_stream_entries (
                stream_kind,
                event_type,
                project_id,
                task_id,
                thread_id,
                dispatch_id,
                sender,
                sender_instance_id,
                recipient_agent,
                recipient_role,
                recipient_instance_id,
                delivery_mode,
                body,
                metadata,
                dedup_key
            )
            VALUES (
                @streamKind,
                @eventType,
                @projectId,
                @taskId,
                @threadId,
                @dispatchId,
                @sender,
                @senderInstanceId,
                @recipientAgent,
                @recipientRole,
                @recipientInstanceId,
                @deliveryMode,
                @body,
                @metadata,
                @dedupKey
            )
            RETURNING
                id,
                stream_kind,
                event_type,
                project_id,
                task_id,
                thread_id,
                dispatch_id,
                sender,
                sender_instance_id,
                recipient_agent,
                recipient_role,
                recipient_instance_id,
                delivery_mode,
                body,
                metadata,
                dedup_key,
                created_at
            """;
        AddParameters(cmd, entry);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            return ReadEntry(reader);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && entry.DedupKey is not null)
        {
            var existing = await GetByDedupKeyAsync(conn, entry.DedupKey);
            if (existing is not null)
                return existing;

            throw;
        }
    }

    public async Task<AgentStreamEntry?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id,
                stream_kind,
                event_type,
                project_id,
                task_id,
                thread_id,
                dispatch_id,
                sender,
                sender_instance_id,
                recipient_agent,
                recipient_role,
                recipient_instance_id,
                delivery_mode,
                body,
                metadata,
                dedup_key,
                created_at
            FROM agent_stream_entries
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEntry(reader) : null;
    }

    public async Task<Dictionary<int, AgentStreamEntry>> GetByIdsAsync(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
            return new Dictionary<int, AgentStreamEntry>();

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var paramNames = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            paramNames[i] = $"@id{i}";
            cmd.Parameters.AddWithValue(paramNames[i], ids[i]);
        }

        cmd.CommandText = $"""
            SELECT
                id,
                stream_kind,
                event_type,
                project_id,
                task_id,
                thread_id,
                dispatch_id,
                sender,
                sender_instance_id,
                recipient_agent,
                recipient_role,
                recipient_instance_id,
                delivery_mode,
                body,
                metadata,
                dedup_key,
                created_at
            FROM agent_stream_entries
            WHERE id IN ({string.Join(", ", paramNames)})
            """;

        var result = new Dictionary<int, AgentStreamEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var entry = ReadEntry(reader);
            result[entry.Id] = entry;
        }

        return result;
    }

    public async Task<List<AgentStreamEntry>> ListAsync(AgentStreamListOptions? options = null)
    {
        options ??= new AgentStreamListOptions();
        var limit = Math.Clamp(options.Limit, 1, 200);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }

        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId.Value);
        }

        if (options.DispatchId is not null)
        {
            where.Add("dispatch_id = @dispatchId");
            cmd.Parameters.AddWithValue("@dispatchId", options.DispatchId.Value);
        }

        if (options.StreamKind is not null)
        {
            where.Add("stream_kind = @streamKind");
            cmd.Parameters.AddWithValue("@streamKind", options.StreamKind.Value.ToDbValue());
        }

        if (!string.IsNullOrWhiteSpace(options.EventType))
        {
            where.Add("event_type = @eventType");
            cmd.Parameters.AddWithValue("@eventType", options.EventType);
        }

        if (!string.IsNullOrWhiteSpace(options.Sender))
        {
            where.Add("sender = @sender");
            cmd.Parameters.AddWithValue("@sender", options.Sender);
        }

        if (!string.IsNullOrWhiteSpace(options.SenderInstanceId))
        {
            where.Add("sender_instance_id = @senderInstanceId");
            cmd.Parameters.AddWithValue("@senderInstanceId", options.SenderInstanceId);
        }

        if (!string.IsNullOrWhiteSpace(options.RecipientAgent))
        {
            where.Add("recipient_agent = @recipientAgent");
            cmd.Parameters.AddWithValue("@recipientAgent", options.RecipientAgent);
        }

        if (!string.IsNullOrWhiteSpace(options.RecipientRole))
        {
            where.Add("recipient_role = @recipientRole");
            cmd.Parameters.AddWithValue("@recipientRole", options.RecipientRole);
        }

        if (!string.IsNullOrWhiteSpace(options.RecipientInstanceId))
        {
            where.Add("recipient_instance_id = @recipientInstanceId");
            cmd.Parameters.AddWithValue("@recipientInstanceId", options.RecipientInstanceId);
        }

        if (!string.IsNullOrWhiteSpace(options.MetadataRunId))
        {
            where.Add("json_extract(metadata, '$.run_id') = @metadataRunId");
            cmd.Parameters.AddWithValue("@metadataRunId", options.MetadataRunId);
        }

        if (!options.IncludeDebug)
        {
            where.Add("(COALESCE(json_extract(metadata, '$.event_visibility'), '') <> 'debug' AND event_type NOT LIKE 'subagent_work_%')");
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT
                id,
                stream_kind,
                event_type,
                project_id,
                task_id,
                thread_id,
                dispatch_id,
                sender,
                sender_instance_id,
                recipient_agent,
                recipient_role,
                recipient_instance_id,
                delivery_mode,
                body,
                metadata,
                dedup_key,
                created_at
            FROM agent_stream_entries
            {whereClause}
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var entries = new List<AgentStreamEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(ReadEntry(reader));

        return entries;
    }

    private static void AddParameters(SqliteCommand cmd, AgentStreamEntry entry)
    {
        cmd.Parameters.AddWithValue("@streamKind", entry.StreamKind.ToDbValue());
        cmd.Parameters.AddWithValue("@eventType", entry.EventType);
        cmd.Parameters.AddWithValue("@projectId", (object?)entry.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@taskId", (object?)entry.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@threadId", (object?)entry.ThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dispatchId", (object?)entry.DispatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sender", entry.Sender);
        cmd.Parameters.AddWithValue("@senderInstanceId", (object?)entry.SenderInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recipientAgent", (object?)entry.RecipientAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recipientRole", (object?)entry.RecipientRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recipientInstanceId", (object?)entry.RecipientInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@deliveryMode", entry.DeliveryMode.ToDbValue());
        cmd.Parameters.AddWithValue("@body", (object?)entry.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata",
            entry.Metadata.HasValue ? entry.Metadata.Value.GetRawText() : DBNull.Value);
        cmd.Parameters.AddWithValue("@dedupKey", (object?)entry.DedupKey ?? DBNull.Value);
    }

    private static async Task<AgentStreamEntry?> GetByDedupKeyAsync(SqliteConnection conn, string dedupKey)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id,
                stream_kind,
                event_type,
                project_id,
                task_id,
                thread_id,
                dispatch_id,
                sender,
                sender_instance_id,
                recipient_agent,
                recipient_role,
                recipient_instance_id,
                delivery_mode,
                body,
                metadata,
                dedup_key,
                created_at
            FROM agent_stream_entries
            WHERE dedup_key = @dedupKey
            ORDER BY id ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@dedupKey", dedupKey);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEntry(reader) : null;
    }

    private static AgentStreamEntry ReadEntry(SqliteDataReader reader)
    {
        var metadataJson = reader.IsDBNull(14) ? null : reader.GetString(14);
        return new AgentStreamEntry
        {
            Id = reader.GetInt32(0),
            StreamKind = EnumExtensions.ParseAgentStreamKind(reader.GetString(1)),
            EventType = reader.GetString(2),
            ProjectId = reader.IsDBNull(3) ? null : reader.GetString(3),
            TaskId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            ThreadId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            DispatchId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Sender = reader.GetString(7),
            SenderInstanceId = reader.IsDBNull(8) ? null : reader.GetString(8),
            RecipientAgent = reader.IsDBNull(9) ? null : reader.GetString(9),
            RecipientRole = reader.IsDBNull(10) ? null : reader.GetString(10),
            RecipientInstanceId = reader.IsDBNull(11) ? null : reader.GetString(11),
            DeliveryMode = EnumExtensions.ParseAgentStreamDeliveryMode(reader.GetString(12)),
            Body = reader.IsDBNull(13) ? null : reader.GetString(13),
            Metadata = metadataJson is not null ? JsonSerializer.Deserialize<JsonElement>(metadataJson) : null,
            DedupKey = reader.IsDBNull(15) ? null : reader.GetString(15),
            CreatedAt = DateTime.Parse(reader.GetString(16))
        };
    }
}
