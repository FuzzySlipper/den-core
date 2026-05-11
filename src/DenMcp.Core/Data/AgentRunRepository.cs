using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IAgentRunRepository
{
    Task<bool> UpsertFromStreamEntryAsync(AgentStreamEntry entry);
    Task<AgentRunRecord?> RebuildFromStreamAsync(string runId);
    Task<AgentRunRecord?> GetAsync(string runId, SubagentRunListOptions options);
    Task<List<AgentRunRecord>> ListAsync(SubagentRunListOptions options);
}

public sealed class AgentRunRepository : IAgentRunRepository
{
    private readonly DbConnectionFactory _db;

    public AgentRunRepository(DbConnectionFactory db) => _db = db;

    public async Task<bool> UpsertFromStreamEntryAsync(AgentStreamEntry entry)
    {
        if (entry.StreamKind != AgentStreamKind.Ops ||
            !entry.EventType.StartsWith("subagent_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(TextMetadata(entry.Metadata, "run_id")))
        {
            return false;
        }

        await using var conn = await _db.CreateConnectionAsync();
        var rebuilt = await RebuildFromStreamAsync(conn, TextMetadata(entry.Metadata, "run_id")!);
        return rebuilt is not null;
    }

    public async Task<AgentRunRecord?> RebuildFromStreamAsync(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        await using var conn = await _db.CreateConnectionAsync();
        return await RebuildFromStreamAsync(conn, runId.Trim());
    }

    public async Task<AgentRunRecord?> GetAsync(string runId, SubagentRunListOptions options)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        await using var conn = await _db.CreateConnectionAsync();
        var record = await GetByRunIdAsync(conn, runId.Trim());
        if (record is null || !MatchesOptions(record, options))
            return null;

        return record;
    }

    public async Task<List<AgentRunRecord>> ListAsync(SubagentRunListOptions options)
    {
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

        AddStateFilter(where, cmd, options.State);

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM agent_runs
            {whereClause}
            ORDER BY
                COALESCE(ended_at, last_heartbeat_at, last_assistant_output_at, started_at, updated_at) DESC,
                COALESCE(latest_stream_entry_id, 0) DESC,
                run_id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 50));

        var records = new List<AgentRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            records.Add(ReadRecord(reader));
        return records;
    }

    private async Task<AgentRunRecord?> RebuildFromStreamAsync(SqliteConnection conn, string runId)
    {
        var events = await LoadRunEventsAsync(conn, runId);
        if (events.Count == 0)
            return await GetByRunIdAsync(conn, runId);

        var existing = await GetByRunIdAsync(conn, runId);
        var record = BuildRecord(runId, events, existing);
        await SaveAsync(conn, record);
        return record;
    }

    private static AgentRunRecord BuildRecord(
        string runId,
        IReadOnlyList<RunStreamEvent> events,
        AgentRunRecord? existing)
    {
        var now = DateTime.UtcNow;
        var record = existing is null
            ? new AgentRunRecord
            {
                RunId = runId,
                State = "unknown",
                CreatedAt = events[0].CreatedAt,
                UpdatedAt = now
            }
            : Clone(existing);

        record.HeartbeatCount = 0;
        record.AssistantOutputCount = 0;
        record.EventCount = events.Count;
        record.RawWorkEventCount = 0;
        record.OperatorEventsJson = null;
        record.LastHeartbeatAt = null;
        record.LastAssistantOutputAt = null;

        var operatorEvents = new List<SubagentRunOperatorEvent>();
        string? role = record.Role;

        foreach (var item in events)
        {
            record.ProjectId = item.ProjectId ?? TextMetadata(item.Metadata, "project_id") ?? record.ProjectId;
            record.TaskId = item.TaskId ?? IntMetadata(item.Metadata, "task_id") ?? record.TaskId;
            record.ReviewRoundId = IntMetadata(item.Metadata, "review_round_id") ?? record.ReviewRoundId;
            record.WorkspaceId = TextMetadata(item.Metadata, "workspace_id") ?? record.WorkspaceId;
            record.Role = TextMetadata(item.Metadata, "role") ?? record.Role;
            role = record.Role ?? role;
            record.Backend = TextMetadata(item.Metadata, "backend") ?? record.Backend;
            record.Model = TextMetadata(item.Metadata, "model") ?? record.Model;
            record.SenderInstanceId = item.SenderInstanceId ?? TextMetadata(item.Metadata, "sender_instance_id") ?? record.SenderInstanceId;
            record.RerunOfRunId = TextMetadata(item.Metadata, "rerun_of_run_id") ?? record.RerunOfRunId;
            record.FallbackModel = TextMetadata(item.Metadata, "fallback_model") ?? record.FallbackModel;
            record.FallbackFromModel = TextMetadata(item.Metadata, "fallback_from_model")
                ?? TextMetadata(item.Metadata, "failed_model")
                ?? record.FallbackFromModel;
            record.FallbackFromExitCode = IntMetadata(item.Metadata, "fallback_from_exit_code")
                ?? IntMetadata(item.Metadata, "failed_exit_code")
                ?? record.FallbackFromExitCode;

            ApplyArtifacts(record, item.Metadata);

            var startedAt = DateMetadata(item.Metadata, "started_at");
            if (startedAt is not null)
                record.StartedAt = startedAt;
            else if (item.EventType == "subagent_started" && record.StartedAt is null)
                record.StartedAt = item.CreatedAt;

            var endedAt = DateMetadata(item.Metadata, "ended_at");
            if (endedAt is not null)
                record.EndedAt = endedAt;
            else if (IsTerminalStateEvent(item.EventType))
                record.EndedAt = item.CreatedAt;

            record.DurationMs = IntMetadata(item.Metadata, "duration_ms") ?? record.DurationMs;
            record.Pid = IntMetadata(item.Metadata, "pid") ?? EventIntMetadata(item.Metadata, "pid") ?? record.Pid;
            record.ExitCode = IntMetadata(item.Metadata, "exit_code") ?? record.ExitCode;
            record.Signal = TextMetadata(item.Metadata, "signal") ?? record.Signal;
            record.TimeoutKind = TextMetadata(item.Metadata, "timeout_kind") ?? record.TimeoutKind;
            record.OutputStatus = TextMetadata(item.Metadata, "output_status") ?? record.OutputStatus;
            record.InfrastructureFailureReason = TextMetadata(item.Metadata, "infrastructure_failure_reason") ?? record.InfrastructureFailureReason;
            record.InfrastructureWarningReason = TextMetadata(item.Metadata, "infrastructure_warning_reason") ?? record.InfrastructureWarningReason;

            if (item.EventType == "subagent_started" && record.StartedStreamEntryId is null)
                record.StartedStreamEntryId = item.Id;
            if (item.EventType == "subagent_heartbeat")
            {
                record.HeartbeatCount++;
                record.LastHeartbeatAt = item.CreatedAt;
            }
            if (item.EventType == "subagent_assistant_output")
            {
                record.AssistantOutputCount++;
                record.LastAssistantOutputAt = item.CreatedAt;
            }

            if (SubagentRunLifecycleConventions.IsRawWorkEvent(item.EventType))
            {
                record.RawWorkEventCount++;
            }
            else
            {
                var entryRole = TextMetadata(item.Metadata, "role") ?? role;
                var eventName = TextMetadata(item.Metadata, "operator_event") ??
                    SubagentRunLifecycleConventions.OperatorEventForSubagentRun(item.EventType, entryRole);
                if (eventName is not null)
                {
                    operatorEvents.Add(new SubagentRunOperatorEvent
                    {
                        EventName = eventName,
                        Source = "agent_stream",
                        SourceEventType = item.EventType,
                        StreamEntryId = item.Id,
                        OccurredAt = item.CreatedAt,
                        Visibility = TextMetadata(item.Metadata, "event_visibility") ??
                            SubagentRunLifecycleConventions.VisibilityForStreamEvent(item.EventType)
                    });
                }
            }

            record.LatestStreamEntryId = item.Id;
            record.State = StateFromEvent(item.EventType);
        }

        if (operatorEvents.Count > 0)
            record.OperatorEventsJson = JsonSerializer.Serialize(operatorEvents);

        record.UpdatedAt = now;
        return record;
    }

    private static AgentRunRecord Clone(AgentRunRecord source) => new()
    {
        RunId = source.RunId,
        ProjectId = source.ProjectId,
        TaskId = source.TaskId,
        ReviewRoundId = source.ReviewRoundId,
        WorkspaceId = source.WorkspaceId,
        Role = source.Role,
        Backend = source.Backend,
        Model = source.Model,
        SenderInstanceId = source.SenderInstanceId,
        State = source.State,
        StartedAt = source.StartedAt,
        EndedAt = source.EndedAt,
        DurationMs = source.DurationMs,
        Pid = source.Pid,
        ExitCode = source.ExitCode,
        Signal = source.Signal,
        TimeoutKind = source.TimeoutKind,
        OutputStatus = source.OutputStatus,
        InfrastructureFailureReason = source.InfrastructureFailureReason,
        InfrastructureWarningReason = source.InfrastructureWarningReason,
        ArtifactDir = source.ArtifactDir,
        StdoutJsonlPath = source.StdoutJsonlPath,
        StderrLogPath = source.StderrLogPath,
        StatusJsonPath = source.StatusJsonPath,
        EventsJsonlPath = source.EventsJsonlPath,
        RerunOfRunId = source.RerunOfRunId,
        FallbackModel = source.FallbackModel,
        FallbackFromModel = source.FallbackFromModel,
        FallbackFromExitCode = source.FallbackFromExitCode,
        LatestStreamEntryId = source.LatestStreamEntryId,
        StartedStreamEntryId = source.StartedStreamEntryId,
        HeartbeatCount = source.HeartbeatCount,
        AssistantOutputCount = source.AssistantOutputCount,
        RawWorkEventCount = source.RawWorkEventCount,
        OperatorEventsJson = source.OperatorEventsJson,
        EventCount = source.EventCount,
        LastHeartbeatAt = source.LastHeartbeatAt,
        LastAssistantOutputAt = source.LastAssistantOutputAt,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt
    };

    private static async Task<List<RunStreamEvent>> LoadRunEventsAsync(SqliteConnection conn, string runId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                id,
                event_type,
                project_id,
                task_id,
                sender_instance_id,
                metadata,
                created_at
            FROM agent_stream_entries
            WHERE stream_kind = 'ops'
              AND event_type LIKE 'subagent_%'
              AND json_extract(metadata, '$.run_id') = @runId
            ORDER BY id ASC
            """;
        cmd.Parameters.AddWithValue("@runId", runId);

        var events = new List<RunStreamEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var metadataJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            events.Add(new RunStreamEvent(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                metadataJson is not null ? JsonSerializer.Deserialize<JsonElement>(metadataJson) : null,
                DateTime.Parse(reader.GetString(6))));
        }

        return events;
    }

    private static async Task SaveAsync(SqliteConnection conn, AgentRunRecord record)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_runs (
                run_id,
                project_id,
                task_id,
                review_round_id,
                workspace_id,
                role,
                backend,
                model,
                sender_instance_id,
                state,
                started_at,
                ended_at,
                duration_ms,
                pid,
                exit_code,
                signal,
                timeout_kind,
                output_status,
                infrastructure_failure_reason,
                infrastructure_warning_reason,
                artifact_dir,
                stdout_jsonl_path,
                stderr_log_path,
                status_json_path,
                events_jsonl_path,
                rerun_of_run_id,
                fallback_model,
                fallback_from_model,
                fallback_from_exit_code,
                latest_stream_entry_id,
                started_stream_entry_id,
                heartbeat_count,
                assistant_output_count,
                event_count,
                raw_work_event_count,
                operator_events_json,
                last_heartbeat_at,
                last_assistant_output_at,
                created_at,
                updated_at
            )
            VALUES (
                @runId,
                @projectId,
                @taskId,
                @reviewRoundId,
                @workspaceId,
                @role,
                @backend,
                @model,
                @senderInstanceId,
                @state,
                @startedAt,
                @endedAt,
                @durationMs,
                @pid,
                @exitCode,
                @signal,
                @timeoutKind,
                @outputStatus,
                @infrastructureFailureReason,
                @infrastructureWarningReason,
                @artifactDir,
                @stdoutJsonlPath,
                @stderrLogPath,
                @statusJsonPath,
                @eventsJsonlPath,
                @rerunOfRunId,
                @fallbackModel,
                @fallbackFromModel,
                @fallbackFromExitCode,
                @latestStreamEntryId,
                @startedStreamEntryId,
                @heartbeatCount,
                @assistantOutputCount,
                @eventCount,
                @rawWorkEventCount,
                @operatorEventsJson,
                @lastHeartbeatAt,
                @lastAssistantOutputAt,
                @createdAt,
                @updatedAt
            )
            ON CONFLICT(run_id) DO UPDATE SET
                project_id = excluded.project_id,
                task_id = excluded.task_id,
                review_round_id = excluded.review_round_id,
                workspace_id = excluded.workspace_id,
                role = excluded.role,
                backend = excluded.backend,
                model = excluded.model,
                sender_instance_id = excluded.sender_instance_id,
                state = excluded.state,
                started_at = excluded.started_at,
                ended_at = excluded.ended_at,
                duration_ms = excluded.duration_ms,
                pid = excluded.pid,
                exit_code = excluded.exit_code,
                signal = excluded.signal,
                timeout_kind = excluded.timeout_kind,
                output_status = excluded.output_status,
                infrastructure_failure_reason = excluded.infrastructure_failure_reason,
                infrastructure_warning_reason = excluded.infrastructure_warning_reason,
                artifact_dir = excluded.artifact_dir,
                stdout_jsonl_path = excluded.stdout_jsonl_path,
                stderr_log_path = excluded.stderr_log_path,
                status_json_path = excluded.status_json_path,
                events_jsonl_path = excluded.events_jsonl_path,
                rerun_of_run_id = excluded.rerun_of_run_id,
                fallback_model = excluded.fallback_model,
                fallback_from_model = excluded.fallback_from_model,
                fallback_from_exit_code = excluded.fallback_from_exit_code,
                latest_stream_entry_id = excluded.latest_stream_entry_id,
                started_stream_entry_id = excluded.started_stream_entry_id,
                heartbeat_count = excluded.heartbeat_count,
                assistant_output_count = excluded.assistant_output_count,
                event_count = excluded.event_count,
                raw_work_event_count = excluded.raw_work_event_count,
                operator_events_json = excluded.operator_events_json,
                last_heartbeat_at = excluded.last_heartbeat_at,
                last_assistant_output_at = excluded.last_assistant_output_at,
                updated_at = excluded.updated_at
            """;
        AddParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParameters(SqliteCommand cmd, AgentRunRecord record)
    {
        cmd.Parameters.AddWithValue("@runId", record.RunId);
        cmd.Parameters.AddWithValue("@projectId", (object?)record.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@taskId", (object?)record.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reviewRoundId", (object?)record.ReviewRoundId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@workspaceId", (object?)record.WorkspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", (object?)record.Role ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@backend", (object?)record.Backend ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@model", (object?)record.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@senderInstanceId", (object?)record.SenderInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", record.State);
        cmd.Parameters.AddWithValue("@startedAt", DateDb(record.StartedAt));
        cmd.Parameters.AddWithValue("@endedAt", DateDb(record.EndedAt));
        cmd.Parameters.AddWithValue("@durationMs", (object?)record.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pid", (object?)record.Pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@exitCode", (object?)record.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@signal", (object?)record.Signal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timeoutKind", (object?)record.TimeoutKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@outputStatus", (object?)record.OutputStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@infrastructureFailureReason", (object?)record.InfrastructureFailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@infrastructureWarningReason", (object?)record.InfrastructureWarningReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artifactDir", (object?)record.ArtifactDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stdoutJsonlPath", (object?)record.StdoutJsonlPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stderrLogPath", (object?)record.StderrLogPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statusJsonPath", (object?)record.StatusJsonPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@eventsJsonlPath", (object?)record.EventsJsonlPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rerunOfRunId", (object?)record.RerunOfRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fallbackModel", (object?)record.FallbackModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fallbackFromModel", (object?)record.FallbackFromModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fallbackFromExitCode", (object?)record.FallbackFromExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@latestStreamEntryId", (object?)record.LatestStreamEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@startedStreamEntryId", (object?)record.StartedStreamEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@heartbeatCount", record.HeartbeatCount);
        cmd.Parameters.AddWithValue("@assistantOutputCount", record.AssistantOutputCount);
        cmd.Parameters.AddWithValue("@eventCount", record.EventCount);
        cmd.Parameters.AddWithValue("@rawWorkEventCount", record.RawWorkEventCount);
        cmd.Parameters.AddWithValue("@operatorEventsJson", (object?)record.OperatorEventsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastHeartbeatAt", DateDb(record.LastHeartbeatAt));
        cmd.Parameters.AddWithValue("@lastAssistantOutputAt", DateDb(record.LastAssistantOutputAt));
        cmd.Parameters.AddWithValue("@createdAt", DateDb(record.CreatedAt));
        cmd.Parameters.AddWithValue("@updatedAt", DateDb(record.UpdatedAt));
    }

    private static async Task<AgentRunRecord?> GetByRunIdAsync(SqliteConnection conn, string runId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM agent_runs
            WHERE run_id = @runId
            """;
        cmd.Parameters.AddWithValue("@runId", runId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRecord(reader) : null;
    }

    private static AgentRunRecord ReadRecord(SqliteDataReader reader) => new()
    {
        RunId = reader.GetString(0),
        ProjectId = reader.IsDBNull(1) ? null : reader.GetString(1),
        TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        ReviewRoundId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
        WorkspaceId = reader.IsDBNull(4) ? null : reader.GetString(4),
        Role = reader.IsDBNull(5) ? null : reader.GetString(5),
        Backend = reader.IsDBNull(6) ? null : reader.GetString(6),
        Model = reader.IsDBNull(7) ? null : reader.GetString(7),
        SenderInstanceId = reader.IsDBNull(8) ? null : reader.GetString(8),
        State = reader.GetString(9),
        StartedAt = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
        EndedAt = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
        DurationMs = reader.IsDBNull(12) ? null : reader.GetInt32(12),
        Pid = reader.IsDBNull(13) ? null : reader.GetInt32(13),
        ExitCode = reader.IsDBNull(14) ? null : reader.GetInt32(14),
        Signal = reader.IsDBNull(15) ? null : reader.GetString(15),
        TimeoutKind = reader.IsDBNull(16) ? null : reader.GetString(16),
        OutputStatus = reader.IsDBNull(17) ? null : reader.GetString(17),
        InfrastructureFailureReason = reader.IsDBNull(18) ? null : reader.GetString(18),
        InfrastructureWarningReason = reader.IsDBNull(19) ? null : reader.GetString(19),
        ArtifactDir = reader.IsDBNull(20) ? null : reader.GetString(20),
        StdoutJsonlPath = reader.IsDBNull(21) ? null : reader.GetString(21),
        StderrLogPath = reader.IsDBNull(22) ? null : reader.GetString(22),
        StatusJsonPath = reader.IsDBNull(23) ? null : reader.GetString(23),
        EventsJsonlPath = reader.IsDBNull(24) ? null : reader.GetString(24),
        RerunOfRunId = reader.IsDBNull(25) ? null : reader.GetString(25),
        FallbackModel = reader.IsDBNull(26) ? null : reader.GetString(26),
        FallbackFromModel = reader.IsDBNull(27) ? null : reader.GetString(27),
        FallbackFromExitCode = reader.IsDBNull(28) ? null : reader.GetInt32(28),
        LatestStreamEntryId = reader.IsDBNull(29) ? null : reader.GetInt32(29),
        StartedStreamEntryId = reader.IsDBNull(30) ? null : reader.GetInt32(30),
        HeartbeatCount = reader.GetInt32(31),
        AssistantOutputCount = reader.GetInt32(32),
        EventCount = reader.GetInt32(33),
        RawWorkEventCount = reader.IsDBNull(34) ? 0 : reader.GetInt32(34),
        OperatorEventsJson = reader.IsDBNull(35) ? null : reader.GetString(35),
        LastHeartbeatAt = reader.IsDBNull(36) ? null : DateTime.Parse(reader.GetString(36)),
        LastAssistantOutputAt = reader.IsDBNull(37) ? null : DateTime.Parse(reader.GetString(37)),
        CreatedAt = DateTime.Parse(reader.GetString(38)),
        UpdatedAt = DateTime.Parse(reader.GetString(39))
    };

    private static bool MatchesOptions(AgentRunRecord record, SubagentRunListOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ProjectId) && record.ProjectId != options.ProjectId)
            return false;
        if (options.TaskId is not null && record.TaskId != options.TaskId.Value)
            return false;
        return MatchesStateFilter(record.State, options.State);
    }

    private static void AddStateFilter(List<string> where, SqliteCommand cmd, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;

        switch (filter.ToLowerInvariant())
        {
            case "active":
                where.Add("state IN ('running', 'retrying', 'aborting', 'rerun_requested')");
                break;
            case "problem":
                where.Add("state IN ('failed', 'timeout', 'aborted', 'unknown')");
                break;
            case "complete":
                where.Add("state = 'complete'");
                break;
            default:
                where.Add("state = @state");
                cmd.Parameters.AddWithValue("@state", filter.ToLowerInvariant());
                break;
        }
    }

    private static bool MatchesStateFilter(string state, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;

        return filter.ToLowerInvariant() switch
        {
            "active" => state is "running" or "retrying" or "aborting" or "rerun_requested",
            "problem" => state is "failed" or "timeout" or "aborted" or "unknown",
            "complete" => state == "complete",
            _ => state.Equals(filter, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static void ApplyArtifacts(AgentRunRecord record, JsonElement? metadata)
    {
        if (!TryGetMetadataProperty(metadata, "artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Object)
            return;

        record.ArtifactDir = TextProperty(artifacts, "dir") ?? record.ArtifactDir;
        record.StdoutJsonlPath = TextProperty(artifacts, "stdout_jsonl_path") ?? record.StdoutJsonlPath;
        record.StderrLogPath = TextProperty(artifacts, "stderr_log_path") ?? record.StderrLogPath;
        record.StatusJsonPath = TextProperty(artifacts, "status_json_path") ?? record.StatusJsonPath;
        record.EventsJsonlPath = TextProperty(artifacts, "events_jsonl_path") ?? record.EventsJsonlPath;
    }

    private static string StateFromEvent(string eventType) => eventType switch
    {
        "subagent_started" => "running",
        "subagent_process_started" => "running",
        "subagent_heartbeat" => "running",
        "subagent_assistant_output" => "running",
        "subagent_prompt_echo_detected" => "running",
        _ when eventType.StartsWith("subagent_work_", StringComparison.Ordinal) => "running",
        "subagent_fallback_started" => "retrying",
        "subagent_abort_requested" => "aborting",
        "subagent_rerun_requested" => "rerun_requested",
        "subagent_rerun_accepted" => "rerun_accepted",
        "subagent_rerun_unavailable" => "failed",
        "subagent_completed" => "complete",
        "subagent_timeout" => "timeout",
        "subagent_startup_timeout" => "timeout",
        "subagent_terminal_drain_timeout" => "timeout",
        "subagent_aborted" => "aborted",
        "subagent_abort" => "aborted",
        "subagent_failed" => "failed",
        "subagent_spawn_error" => "failed",
        _ => "unknown"
    };

    private static bool IsTerminalStateEvent(string eventType) => StateFromEvent(eventType) is "complete" or "timeout" or "aborted" or "failed";

    private static string? TextMetadata(JsonElement? metadata, string propertyName) =>
        TryGetMetadataProperty(metadata, propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static int? IntMetadata(JsonElement? metadata, string propertyName) =>
        TryGetMetadataProperty(metadata, propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static int? EventIntMetadata(JsonElement? metadata, string propertyName) =>
        TryGetMetadataProperty(metadata, "event", out var eventMetadata) &&
        eventMetadata.ValueKind == JsonValueKind.Object &&
        eventMetadata.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static DateTime? DateMetadata(JsonElement? metadata, string propertyName)
    {
        var text = TextMetadata(metadata, propertyName);
        return text is not null && DateTime.TryParse(text, out var value) ? value : null;
    }

    private static bool TryGetMetadataProperty(JsonElement? metadata, string propertyName, out JsonElement property)
    {
        property = default;
        return metadata is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(propertyName, out property);
    }

    private static string? TextProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()
                : null;
    }

    private static object DateDb(DateTime? value) => value.HasValue ? value.Value.ToString("O") : DBNull.Value;

    private const string Columns = """
        run_id,
        project_id,
        task_id,
        review_round_id,
        workspace_id,
        role,
        backend,
        model,
        sender_instance_id,
        state,
        started_at,
        ended_at,
        duration_ms,
        pid,
        exit_code,
        signal,
        timeout_kind,
        output_status,
        infrastructure_failure_reason,
        infrastructure_warning_reason,
        artifact_dir,
        stdout_jsonl_path,
        stderr_log_path,
        status_json_path,
        events_jsonl_path,
        rerun_of_run_id,
        fallback_model,
        fallback_from_model,
        fallback_from_exit_code,
        latest_stream_entry_id,
        started_stream_entry_id,
        heartbeat_count,
        assistant_output_count,
        event_count,
        raw_work_event_count,
        operator_events_json,
        last_heartbeat_at,
        last_assistant_output_at,
        created_at,
        updated_at
        """;

    private sealed record RunStreamEvent(
        int Id,
        string EventType,
        string? ProjectId,
        int? TaskId,
        string? SenderInstanceId,
        JsonElement? Metadata,
        DateTime CreatedAt);
}
