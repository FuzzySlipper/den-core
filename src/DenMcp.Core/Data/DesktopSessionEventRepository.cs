using System.Globalization;
using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IDesktopSessionEventRepository
{
    /// <summary>
    /// Append a session lifecycle/control event. Returns the stored event with server-assigned id and created_at.
    /// </summary>
    Task<DesktopSessionEvent> AppendAsync(DesktopSessionEvent evt);

    /// <summary>
    /// List session events with optional filters. Ordered newest-first.
    /// </summary>
    Task<List<DesktopSessionEvent>> ListAsync(DesktopSessionEventListOptions options);
}

public sealed class DesktopSessionEventRepository : IDesktopSessionEventRepository
{
    private const string Columns = """
        id, project_id, task_id, workspace_id, source_instance_id, session_id,
        event_type, payload, requested_by, reason, observed_at, created_at
        """;

    private const int MaxPayloadBytes = 10240;
    private const int MaxReasonLength = 2000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DbConnectionFactory _db;
    private readonly Func<DateTime> _utcNow;

    public DesktopSessionEventRepository(DbConnectionFactory db, Func<DateTime>? utcNow = null)
    {
        _db = db;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<DesktopSessionEvent> AppendAsync(DesktopSessionEvent evt)
    {
        ValidateEvent(evt);
        var now = _utcNow();

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO desktop_session_events (
                project_id, task_id, workspace_id, source_instance_id, session_id,
                event_type, payload, requested_by, reason, observed_at, created_at
            ) VALUES (
                @projectId, @taskId, @workspaceId, @sourceInstanceId, @sessionId,
                @eventType, @payload, @requestedBy, @reason, @observedAt, @createdAt
            )
            RETURNING {Columns}
            """;
        AddParameters(cmd, evt, now);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadEvent(reader);
    }

    public async Task<List<DesktopSessionEvent>> ListAsync(DesktopSessionEventListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var where = BuildWhere(cmd, options);
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM desktop_session_events
            {where}
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<DesktopSessionEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadEvent(reader));
        return results;
    }

    private string BuildWhere(SqliteCommand cmd, DesktopSessionEventListOptions options)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId.Trim());
        }
        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.WorkspaceId))
        {
            where.Add("workspace_id = @workspaceId");
            cmd.Parameters.AddWithValue("@workspaceId", options.WorkspaceId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.SourceInstanceId))
        {
            where.Add("source_instance_id = @sourceInstanceId");
            cmd.Parameters.AddWithValue("@sourceInstanceId", options.SourceInstanceId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            where.Add("session_id = @sessionId");
            cmd.Parameters.AddWithValue("@sessionId", options.SessionId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.EventTypes))
        {
            var types = options.EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (types.Length > 0)
            {
                var typeParams = new List<string>();
                for (var i = 0; i < types.Length; i++)
                {
                    var p = $"@eventType{i}";
                    typeParams.Add(p);
                    cmd.Parameters.AddWithValue(p, types[i].Trim());
                }
                where.Add($"event_type IN ({string.Join(", ", typeParams)})");
            }
        }
        return where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
    }

    private void AddParameters(SqliteCommand cmd, DesktopSessionEvent evt, DateTime now)
    {
        cmd.Parameters.AddWithValue("@projectId", evt.ProjectId.Trim());
        cmd.Parameters.AddWithValue("@taskId", (object?)evt.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@workspaceId", NullIfWhiteSpace(evt.WorkspaceId));
        cmd.Parameters.AddWithValue("@sourceInstanceId", evt.SourceInstanceId.Trim());
        cmd.Parameters.AddWithValue("@sessionId", evt.SessionId.Trim());
        cmd.Parameters.AddWithValue("@eventType", evt.EventType.Trim());
        cmd.Parameters.AddWithValue("@payload", (object?)evt.Payload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestedBy", NullIfWhiteSpace(evt.RequestedBy));
        cmd.Parameters.AddWithValue("@reason", NullIfWhiteSpace(evt.Reason));
        cmd.Parameters.AddWithValue("@observedAt", ToDbTime(evt.ObservedAt));
        cmd.Parameters.AddWithValue("@createdAt", ToDbTime(now));
    }

    private static DesktopSessionEvent ReadEvent(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ProjectId = reader.GetString(1),
        TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
        SourceInstanceId = reader.GetString(4),
        SessionId = reader.GetString(5),
        EventType = reader.GetString(6),
        Payload = reader.IsDBNull(7) ? null : reader.GetString(7),
        RequestedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
        Reason = reader.IsDBNull(9) ? null : reader.GetString(9),
        ObservedAt = FromDbTime(reader.GetString(10)),
        CreatedAt = FromDbTime(reader.GetString(11))
    };

    private static void ValidateEvent(DesktopSessionEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.ProjectId))
            throw new ArgumentException("Project id is required.", nameof(evt));
        if (string.IsNullOrWhiteSpace(evt.SourceInstanceId))
            throw new ArgumentException("Source instance id is required.", nameof(evt));
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            throw new ArgumentException("Session id is required.", nameof(evt));
        if (string.IsNullOrWhiteSpace(evt.EventType))
            throw new ArgumentException("Event type is required.", nameof(evt));
        if (evt.ObservedAt == default)
            throw new ArgumentException("Observed at is required.", nameof(evt));

        if (evt.Payload?.Length > MaxPayloadBytes)
            throw new ArgumentException(
                $"Payload must not exceed {MaxPayloadBytes} bytes. Got {evt.Payload.Length}.",
                nameof(evt));
        if (evt.Reason?.Length > MaxReasonLength)
            throw new ArgumentException(
                $"Reason must not exceed {MaxReasonLength} characters. Got {evt.Reason.Length}.",
                nameof(evt));
    }

    private static object NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string ToDbTime(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromDbTime(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
