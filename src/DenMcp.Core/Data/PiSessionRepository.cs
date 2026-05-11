using System.Globalization;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IPiSessionRepository
{
    Task<PiSessionRecord> CreateAsync(PiSessionRecord record);
    Task<PiSessionRecord?> GetAsync(string projectId, string sessionId);
    Task<List<PiSessionRecord>> ListAsync(PiSessionListOptions options);
    Task<PiSessionRecord> UpdateStateAsync(
        string projectId,
        string sessionId,
        string state,
        string? stateReason = null,
        DateTime? startedAt = null,
        DateTime? lastActivityAt = null,
        DateTime? endedAt = null,
        string? containerId = null,
        string? containerName = null);
    Task<PiSessionRecord> UpdateRuntimeAsync(
        string projectId,
        string sessionId,
        string state,
        string? stateReason,
        DateTime? lastActivityAt,
        string? containerId,
        string? containerName,
        bool outputCaptured,
        string? outputTail,
        DateTime? outputTailCapturedAt,
        bool outputTailTruncated,
        string? outputTailSha256,
        string? attentionState,
        string? attentionReason,
        bool needsUserInput,
        DateTime? attentionObservedAt);
    Task<PiSessionRecord> MarkTerminationRequestedAsync(string projectId, string sessionId, string requestedBy, string? reason);
    Task<PiSessionRecord> MarkCleanupRequestedAsync(string projectId, string sessionId, string requestedBy, string? reason);
    Task<PiSessionRecord> MarkCleanupCompletedAsync(string projectId, string sessionId, string? stateReason = null);
    Task<PiSessionRecord> MarkCompletionObservedAsync(string projectId, string sessionId, string stateReason, DateTime? lastActivityAt = null);
    Task<PiSessionEvent> AppendEventAsync(PiSessionEvent evt);
}

public sealed class PiSessionRepository : IPiSessionRepository
{
    private const string Columns = """
        session_id, project_id, task_id, workspace_id, run_id, title, tool_profile,
        model, provider, host_id, tmux_session_name, container_id, container_name,
        state, state_reason, launch_profile_kind, launch_profile_id, launch_profile_json,
        launch_command_json, launch_command_display, created_at, started_at, last_activity_at,
        output_tail, output_tail_captured_at, output_tail_truncated, output_tail_sha256,
        attention_state, attention_reason, attention_since_at, attention_updated_at,
        needs_user_input, ended_at, updated_at, termination_requested_at, termination_requested_by,
        termination_reason, cleanup_requested_at, cleanup_requested_by, cleanup_reason,
        cleanup_completed_at
        """;

    private const string EventColumns = """
        id, project_id, task_id, workspace_id, session_id, event_type, payload,
        requested_by, reason, created_at
        """;

    private readonly DbConnectionFactory _db;
    private readonly Func<DateTime> _utcNow;

    public PiSessionRepository(DbConnectionFactory db, Func<DateTime>? utcNow = null)
    {
        _db = db;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<PiSessionRecord> CreateAsync(PiSessionRecord record)
    {
        Validate(record);
        var now = _utcNow();
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO pi_sessions (
                session_id, project_id, task_id, workspace_id, run_id, title, tool_profile,
                model, provider, host_id, tmux_session_name, container_id, container_name,
                state, state_reason, launch_profile_kind, launch_profile_id, launch_profile_json,
                launch_command_json, launch_command_display, created_at, started_at, last_activity_at,
                output_tail, output_tail_captured_at, output_tail_truncated, output_tail_sha256,
                attention_state, attention_reason, attention_since_at, attention_updated_at,
                needs_user_input, ended_at, updated_at, termination_requested_at, termination_requested_by,
                termination_reason, cleanup_requested_at, cleanup_requested_by, cleanup_reason,
                cleanup_completed_at
            ) VALUES (
                @sessionId, @projectId, @taskId, @workspaceId, @runId, @title, @toolProfile,
                @model, @provider, @hostId, @tmuxSessionName, @containerId, @containerName,
                @state, @stateReason, @launchProfileKind, @launchProfileId, @launchProfileJson,
                @launchCommandJson, @launchCommandDisplay, @createdAt, @startedAt, @lastActivityAt,
                @outputTail, @outputTailCapturedAt, @outputTailTruncated, @outputTailSha256,
                @attentionState, @attentionReason, @attentionSinceAt, @attentionUpdatedAt,
                @needsUserInput, @endedAt, @updatedAt, @terminationRequestedAt, @terminationRequestedBy,
                @terminationReason, @cleanupRequestedAt, @cleanupRequestedBy, @cleanupReason,
                @cleanupCompletedAt
            )
            RETURNING {Columns}
            """;
        AddRecordParameters(cmd, record, now, now);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadRecord(reader);
    }

    public async Task<PiSessionRecord?> GetAsync(string projectId, string sessionId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM pi_sessions WHERE project_id = @projectId AND session_id = @sessionId";
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRecord(reader) : null;
    }

    public async Task<List<PiSessionRecord>> ListAsync(PiSessionListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "project_id = @projectId" };
        cmd.Parameters.AddWithValue("@projectId", options.ProjectId.Trim());

        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId.Value);
        }

        if (!string.IsNullOrWhiteSpace(options.State))
        {
            var states = options.State.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (states.Length > 0)
            {
                var parameters = new List<string>();
                for (var i = 0; i < states.Length; i++)
                {
                    var name = $"@state{i}";
                    parameters.Add(name);
                    cmd.Parameters.AddWithValue(name, states[i]);
                }
                where.Add($"state IN ({string.Join(", ", parameters)})");
            }
        }

        cmd.CommandText = $"""
            SELECT {Columns}
            FROM pi_sessions
            WHERE {string.Join(" AND ", where)}
            ORDER BY COALESCE(last_activity_at, started_at, created_at) DESC, session_id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var records = new List<PiSessionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            records.Add(ReadRecord(reader));
        return records;
    }

    public async Task<PiSessionRecord> UpdateStateAsync(
        string projectId,
        string sessionId,
        string state,
        string? stateReason = null,
        DateTime? startedAt = null,
        DateTime? lastActivityAt = null,
        DateTime? endedAt = null,
        string? containerId = null,
        string? containerName = null)
    {
        var now = _utcNow();
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE pi_sessions
            SET state = @state,
                state_reason = @stateReason,
                started_at = COALESCE(@startedAt, started_at),
                last_activity_at = COALESCE(@lastActivityAt, last_activity_at),
                ended_at = COALESCE(@endedAt, ended_at),
                container_id = COALESCE(@containerId, container_id),
                container_name = COALESCE(@containerName, container_name),
                attention_state = CASE WHEN @state IN ('launching', 'running', 'terminating') THEN attention_state ELSE NULL END,
                attention_reason = CASE WHEN @state IN ('launching', 'running', 'terminating') THEN attention_reason ELSE NULL END,
                attention_since_at = CASE WHEN @state IN ('launching', 'running', 'terminating') THEN attention_since_at ELSE NULL END,
                attention_updated_at = CASE WHEN @state IN ('launching', 'running', 'terminating') THEN attention_updated_at ELSE NULL END,
                needs_user_input = CASE WHEN @state IN ('launching', 'running', 'terminating') THEN needs_user_input ELSE 0 END,
                updated_at = @updatedAt
            WHERE project_id = @projectId AND session_id = @sessionId
            RETURNING {Columns}
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
        cmd.Parameters.AddWithValue("@state", state.Trim());
        cmd.Parameters.AddWithValue("@stateReason", NullIfWhiteSpace(stateReason));
        cmd.Parameters.AddWithValue("@startedAt", ToDbTimeOrNull(startedAt));
        cmd.Parameters.AddWithValue("@lastActivityAt", ToDbTimeOrNull(lastActivityAt));
        cmd.Parameters.AddWithValue("@endedAt", ToDbTimeOrNull(endedAt));
        cmd.Parameters.AddWithValue("@containerId", NullIfWhiteSpace(containerId));
        cmd.Parameters.AddWithValue("@containerName", NullIfWhiteSpace(containerName));
        cmd.Parameters.AddWithValue("@updatedAt", ToDbTime(now));
        return await ExecuteReturningRecordAsync(cmd, sessionId);
    }

    public async Task<PiSessionRecord> UpdateRuntimeAsync(
        string projectId,
        string sessionId,
        string state,
        string? stateReason,
        DateTime? lastActivityAt,
        string? containerId,
        string? containerName,
        bool outputCaptured,
        string? outputTail,
        DateTime? outputTailCapturedAt,
        bool outputTailTruncated,
        string? outputTailSha256,
        string? attentionState,
        string? attentionReason,
        bool needsUserInput,
        DateTime? attentionObservedAt)
    {
        var now = _utcNow();
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE pi_sessions
            SET state = @state,
                state_reason = @stateReason,
                last_activity_at = COALESCE(@lastActivityAt, last_activity_at),
                container_id = COALESCE(@containerId, container_id),
                container_name = COALESCE(@containerName, container_name),
                output_tail = CASE WHEN @outputCaptured = 1 THEN @outputTail ELSE output_tail END,
                output_tail_captured_at = CASE WHEN @outputCaptured = 1 THEN @outputTailCapturedAt ELSE output_tail_captured_at END,
                output_tail_truncated = CASE WHEN @outputCaptured = 1 THEN @outputTailTruncated ELSE output_tail_truncated END,
                output_tail_sha256 = CASE WHEN @outputCaptured = 1 THEN @outputTailSha256 ELSE output_tail_sha256 END,
                attention_since_at = CASE
                    WHEN @attentionState IS NULL THEN NULL
                    WHEN attention_state = @attentionState THEN COALESCE(attention_since_at, @attentionObservedAt)
                    ELSE @attentionObservedAt
                END,
                attention_state = @attentionState,
                attention_reason = @attentionReason,
                attention_updated_at = CASE WHEN @attentionState IS NULL THEN NULL ELSE @attentionObservedAt END,
                needs_user_input = @needsUserInput,
                updated_at = @updatedAt
            WHERE project_id = @projectId AND session_id = @sessionId
            RETURNING {Columns}
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
        cmd.Parameters.AddWithValue("@state", state.Trim());
        cmd.Parameters.AddWithValue("@stateReason", NullIfWhiteSpace(stateReason));
        cmd.Parameters.AddWithValue("@lastActivityAt", ToDbTimeOrNull(lastActivityAt));
        cmd.Parameters.AddWithValue("@containerId", NullIfWhiteSpace(containerId));
        cmd.Parameters.AddWithValue("@containerName", NullIfWhiteSpace(containerName));
        cmd.Parameters.AddWithValue("@outputCaptured", outputCaptured ? 1 : 0);
        cmd.Parameters.AddWithValue("@outputTail", NullIfWhiteSpace(outputTail));
        cmd.Parameters.AddWithValue("@outputTailCapturedAt", ToDbTimeOrNull(outputTailCapturedAt));
        cmd.Parameters.AddWithValue("@outputTailTruncated", outputTailTruncated ? 1 : 0);
        cmd.Parameters.AddWithValue("@outputTailSha256", NullIfWhiteSpace(outputTailSha256));
        cmd.Parameters.AddWithValue("@attentionState", NullIfWhiteSpace(attentionState));
        cmd.Parameters.AddWithValue("@attentionReason", NullIfWhiteSpace(attentionReason));
        cmd.Parameters.AddWithValue("@needsUserInput", needsUserInput ? 1 : 0);
        cmd.Parameters.AddWithValue("@attentionObservedAt", ToDbTimeOrNull(attentionObservedAt));
        cmd.Parameters.AddWithValue("@updatedAt", ToDbTime(now));
        return await ExecuteReturningRecordAsync(cmd, sessionId);
    }

    public Task<PiSessionRecord> MarkTerminationRequestedAsync(string projectId, string sessionId, string requestedBy, string? reason) =>
        MarkControlAsync(projectId, sessionId, PiSessionStates.Terminating, requestedBy, reason, "termination");

    public Task<PiSessionRecord> MarkCleanupRequestedAsync(string projectId, string sessionId, string requestedBy, string? reason) =>
        MarkControlAsync(projectId, sessionId, null, requestedBy, reason, "cleanup");

    public async Task<PiSessionRecord> MarkCleanupCompletedAsync(string projectId, string sessionId, string? stateReason = null)
    {
        var now = _utcNow();
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE pi_sessions
            SET cleanup_completed_at = @completedAt,
                state_reason = COALESCE(@stateReason, state_reason),
                updated_at = @updatedAt
            WHERE project_id = @projectId AND session_id = @sessionId
            RETURNING {Columns}
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
        cmd.Parameters.AddWithValue("@completedAt", ToDbTime(now));
        cmd.Parameters.AddWithValue("@stateReason", NullIfWhiteSpace(stateReason));
        cmd.Parameters.AddWithValue("@updatedAt", ToDbTime(now));
        return await ExecuteReturningRecordAsync(cmd, sessionId);
    }

    public async Task<PiSessionRecord> MarkCompletionObservedAsync(string projectId, string sessionId, string stateReason, DateTime? lastActivityAt = null)
    {
        var now = _utcNow();
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE pi_sessions
            SET state_reason = @stateReason,
                last_activity_at = COALESCE(@lastActivityAt, last_activity_at),
                updated_at = @updatedAt
            WHERE project_id = @projectId AND session_id = @sessionId
            RETURNING {Columns}
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
        cmd.Parameters.AddWithValue("@stateReason", stateReason.Trim());
        cmd.Parameters.AddWithValue("@lastActivityAt", ToDbTimeOrNull(lastActivityAt));
        cmd.Parameters.AddWithValue("@updatedAt", ToDbTime(now));
        return await ExecuteReturningRecordAsync(cmd, sessionId);
    }

    public async Task<PiSessionEvent> AppendEventAsync(PiSessionEvent evt)
    {
        var now = _utcNow();
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO pi_session_events (
                project_id, task_id, workspace_id, session_id, event_type, payload,
                requested_by, reason, created_at
            ) VALUES (
                @projectId, @taskId, @workspaceId, @sessionId, @eventType, @payload,
                @requestedBy, @reason, @createdAt
            )
            RETURNING {EventColumns}
            """;
        cmd.Parameters.AddWithValue("@projectId", evt.ProjectId.Trim());
        cmd.Parameters.AddWithValue("@taskId", (object?)evt.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@workspaceId", NullIfWhiteSpace(evt.WorkspaceId));
        cmd.Parameters.AddWithValue("@sessionId", evt.SessionId.Trim());
        cmd.Parameters.AddWithValue("@eventType", evt.EventType.Trim());
        cmd.Parameters.AddWithValue("@payload", NullIfWhiteSpace(evt.Payload));
        cmd.Parameters.AddWithValue("@requestedBy", NullIfWhiteSpace(evt.RequestedBy));
        cmd.Parameters.AddWithValue("@reason", NullIfWhiteSpace(evt.Reason));
        cmd.Parameters.AddWithValue("@createdAt", ToDbTime(now));
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadEvent(reader);
    }

    private async Task<PiSessionRecord> MarkControlAsync(string projectId, string sessionId, string? state, string requestedBy, string? reason, string kind)
    {
        var now = _utcNow();
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var stateSet = state is null ? string.Empty : "state = @state,";
        cmd.CommandText = $"""
            UPDATE pi_sessions
            SET {stateSet}
                {kind}_requested_at = @requestedAt,
                {kind}_requested_by = @requestedBy,
                {kind}_reason = @reason,
                updated_at = @updatedAt
            WHERE project_id = @projectId AND session_id = @sessionId
            RETURNING {Columns}
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
        if (state is not null)
            cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@requestedAt", ToDbTime(now));
        cmd.Parameters.AddWithValue("@requestedBy", requestedBy.Trim());
        cmd.Parameters.AddWithValue("@reason", NullIfWhiteSpace(reason));
        cmd.Parameters.AddWithValue("@updatedAt", ToDbTime(now));
        return await ExecuteReturningRecordAsync(cmd, sessionId);
    }

    private static async Task<PiSessionRecord> ExecuteReturningRecordAsync(SqliteCommand cmd, string sessionId)
    {
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new KeyNotFoundException($"Pi session {sessionId} not found.");
        return ReadRecord(reader);
    }

    private static void AddRecordParameters(SqliteCommand cmd, PiSessionRecord record, DateTime createdAt, DateTime updatedAt)
    {
        cmd.Parameters.AddWithValue("@sessionId", record.SessionId.Trim());
        cmd.Parameters.AddWithValue("@projectId", record.ProjectId.Trim());
        cmd.Parameters.AddWithValue("@taskId", (object?)record.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@workspaceId", NullIfWhiteSpace(record.WorkspaceId));
        cmd.Parameters.AddWithValue("@runId", NullIfWhiteSpace(record.RunId));
        cmd.Parameters.AddWithValue("@title", NullIfWhiteSpace(record.Title));
        cmd.Parameters.AddWithValue("@toolProfile", NullIfWhiteSpace(record.ToolProfile));
        cmd.Parameters.AddWithValue("@model", NullIfWhiteSpace(record.Model));
        cmd.Parameters.AddWithValue("@provider", NullIfWhiteSpace(record.Provider));
        cmd.Parameters.AddWithValue("@hostId", record.HostId.Trim());
        cmd.Parameters.AddWithValue("@tmuxSessionName", record.TmuxSessionName.Trim());
        cmd.Parameters.AddWithValue("@containerId", NullIfWhiteSpace(record.ContainerId));
        cmd.Parameters.AddWithValue("@containerName", NullIfWhiteSpace(record.ContainerName));
        cmd.Parameters.AddWithValue("@state", record.State.Trim());
        cmd.Parameters.AddWithValue("@stateReason", NullIfWhiteSpace(record.StateReason));
        cmd.Parameters.AddWithValue("@launchProfileKind", record.LaunchProfileKind.Trim());
        cmd.Parameters.AddWithValue("@launchProfileId", NullIfWhiteSpace(record.LaunchProfileId));
        cmd.Parameters.AddWithValue("@launchProfileJson", record.LaunchProfileJson);
        cmd.Parameters.AddWithValue("@launchCommandJson", record.LaunchCommandJson);
        cmd.Parameters.AddWithValue("@launchCommandDisplay", record.LaunchCommandDisplay);
        cmd.Parameters.AddWithValue("@createdAt", ToDbTime(record.CreatedAt == default ? createdAt : record.CreatedAt));
        cmd.Parameters.AddWithValue("@startedAt", ToDbTimeOrNull(record.StartedAt));
        cmd.Parameters.AddWithValue("@lastActivityAt", ToDbTimeOrNull(record.LastActivityAt));
        cmd.Parameters.AddWithValue("@outputTail", NullIfWhiteSpace(record.OutputTail));
        cmd.Parameters.AddWithValue("@outputTailCapturedAt", ToDbTimeOrNull(record.OutputTailCapturedAt));
        cmd.Parameters.AddWithValue("@outputTailTruncated", record.OutputTailTruncated ? 1 : 0);
        cmd.Parameters.AddWithValue("@outputTailSha256", NullIfWhiteSpace(record.OutputTailSha256));
        cmd.Parameters.AddWithValue("@attentionState", NullIfWhiteSpace(record.AttentionState));
        cmd.Parameters.AddWithValue("@attentionReason", NullIfWhiteSpace(record.AttentionReason));
        cmd.Parameters.AddWithValue("@attentionSinceAt", ToDbTimeOrNull(record.AttentionSinceAt));
        cmd.Parameters.AddWithValue("@attentionUpdatedAt", ToDbTimeOrNull(record.AttentionUpdatedAt));
        cmd.Parameters.AddWithValue("@needsUserInput", record.NeedsUserInput ? 1 : 0);
        cmd.Parameters.AddWithValue("@endedAt", ToDbTimeOrNull(record.EndedAt));
        cmd.Parameters.AddWithValue("@updatedAt", ToDbTime(record.UpdatedAt == default ? updatedAt : record.UpdatedAt));
        cmd.Parameters.AddWithValue("@terminationRequestedAt", ToDbTimeOrNull(record.TerminationRequestedAt));
        cmd.Parameters.AddWithValue("@terminationRequestedBy", NullIfWhiteSpace(record.TerminationRequestedBy));
        cmd.Parameters.AddWithValue("@terminationReason", NullIfWhiteSpace(record.TerminationReason));
        cmd.Parameters.AddWithValue("@cleanupRequestedAt", ToDbTimeOrNull(record.CleanupRequestedAt));
        cmd.Parameters.AddWithValue("@cleanupRequestedBy", NullIfWhiteSpace(record.CleanupRequestedBy));
        cmd.Parameters.AddWithValue("@cleanupReason", NullIfWhiteSpace(record.CleanupReason));
        cmd.Parameters.AddWithValue("@cleanupCompletedAt", ToDbTimeOrNull(record.CleanupCompletedAt));
    }

    private static PiSessionRecord ReadRecord(SqliteDataReader reader) => new()
    {
        SessionId = reader.GetString(0),
        ProjectId = reader.GetString(1),
        TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
        RunId = reader.IsDBNull(4) ? null : reader.GetString(4),
        Title = reader.IsDBNull(5) ? null : reader.GetString(5),
        ToolProfile = reader.IsDBNull(6) ? null : reader.GetString(6),
        Model = reader.IsDBNull(7) ? null : reader.GetString(7),
        Provider = reader.IsDBNull(8) ? null : reader.GetString(8),
        HostId = reader.GetString(9),
        TmuxSessionName = reader.GetString(10),
        ContainerId = reader.IsDBNull(11) ? null : reader.GetString(11),
        ContainerName = reader.IsDBNull(12) ? null : reader.GetString(12),
        State = reader.GetString(13),
        StateReason = reader.IsDBNull(14) ? null : reader.GetString(14),
        LaunchProfileKind = reader.GetString(15),
        LaunchProfileId = reader.IsDBNull(16) ? null : reader.GetString(16),
        LaunchProfileJson = reader.GetString(17),
        LaunchCommandJson = reader.GetString(18),
        LaunchCommandDisplay = reader.GetString(19),
        CreatedAt = FromDbTime(reader.GetString(20)),
        StartedAt = reader.IsDBNull(21) ? null : FromDbTime(reader.GetString(21)),
        LastActivityAt = reader.IsDBNull(22) ? null : FromDbTime(reader.GetString(22)),
        OutputTail = reader.IsDBNull(23) ? null : reader.GetString(23),
        OutputTailCapturedAt = reader.IsDBNull(24) ? null : FromDbTime(reader.GetString(24)),
        OutputTailTruncated = !reader.IsDBNull(25) && reader.GetInt32(25) != 0,
        OutputTailSha256 = reader.IsDBNull(26) ? null : reader.GetString(26),
        AttentionState = reader.IsDBNull(27) ? null : reader.GetString(27),
        AttentionReason = reader.IsDBNull(28) ? null : reader.GetString(28),
        AttentionSinceAt = reader.IsDBNull(29) ? null : FromDbTime(reader.GetString(29)),
        AttentionUpdatedAt = reader.IsDBNull(30) ? null : FromDbTime(reader.GetString(30)),
        NeedsUserInput = !reader.IsDBNull(31) && reader.GetInt32(31) != 0,
        EndedAt = reader.IsDBNull(32) ? null : FromDbTime(reader.GetString(32)),
        UpdatedAt = FromDbTime(reader.GetString(33)),
        TerminationRequestedAt = reader.IsDBNull(34) ? null : FromDbTime(reader.GetString(34)),
        TerminationRequestedBy = reader.IsDBNull(35) ? null : reader.GetString(35),
        TerminationReason = reader.IsDBNull(36) ? null : reader.GetString(36),
        CleanupRequestedAt = reader.IsDBNull(37) ? null : FromDbTime(reader.GetString(37)),
        CleanupRequestedBy = reader.IsDBNull(38) ? null : reader.GetString(38),
        CleanupReason = reader.IsDBNull(39) ? null : reader.GetString(39),
        CleanupCompletedAt = reader.IsDBNull(40) ? null : FromDbTime(reader.GetString(40)),
    };

    private static PiSessionEvent ReadEvent(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ProjectId = reader.GetString(1),
        TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
        SessionId = reader.GetString(4),
        EventType = reader.GetString(5),
        Payload = reader.IsDBNull(6) ? null : reader.GetString(6),
        RequestedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
        Reason = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedAt = FromDbTime(reader.GetString(9)),
    };

    private static void Validate(PiSessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SessionId)) throw new ArgumentException("Session id is required.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.ProjectId)) throw new ArgumentException("Project id is required.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.HostId)) throw new ArgumentException("Host id is required.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.TmuxSessionName)) throw new ArgumentException("tmux session name is required.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.State)) throw new ArgumentException("State is required.", nameof(record));
    }

    private static object NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static object ToDbTimeOrNull(DateTime? value) => value is null ? DBNull.Value : ToDbTime(value.Value);
    private static string ToDbTime(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromDbTime(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
