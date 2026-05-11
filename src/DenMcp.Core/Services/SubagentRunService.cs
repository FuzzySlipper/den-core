using System.Text.Json;
using System.Text;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface ISubagentRunService
{
    Task<List<SubagentRunSummary>> ListAsync(SubagentRunListOptions options);
    Task<SubagentRunDetail?> GetAsync(string runId, SubagentRunListOptions options);
}

public sealed class SubagentRunService : ISubagentRunService
{
    private const int MaxArtifactTailBytes = 64 * 1024;
    private readonly IAgentStreamRepository _stream;
    private readonly IAgentRunRepository _runs;

    public SubagentRunService(IAgentStreamRepository stream, IAgentRunRepository runs)
    {
        _stream = stream;
        _runs = runs;
    }

    /// <summary>
    /// List sub-agent run summaries for the given project/task.
    /// </summary>
    /// <remarks>
    /// Scale assumptions (as of task #956):
    /// <list type="bullet">
    ///   <item>Up to 50 runs per list request (controlled by options.Limit, clamped to [1,50]).</item>
    ///   <item>Stream summary discovery loads up to options.SourceLimit entries (default 200, max 200).</item>
    ///   <item>List path uses pre-computed EventCounts and OperatorEvents from the materialized AgentRunRecord
    ///         (populated during RebuildFromStreamAsync) — zero per-run event loads for list summaries.</item>
    ///   <item>Latest/started stream entries are batch-loaded in a single GetByIdsAsync call.</item>
    ///   <item>Detail (GetAsync) still loads full events for raw event/artifact views.</item>
    ///   <item>Pre-migration records (without raw_work_event_count/operator_events_json columns) will have
    ///         RawWorkEventCount=0 and OperatorEventsJson=null, yielding conservative counts.
    ///         A RebuildFromStreamAsync call will backfill these columns.</item>
    /// </list>
    /// </remarks>
    public async Task<List<SubagentRunSummary>> ListAsync(SubagentRunListOptions options)
    {
        var records = await _runs.ListAsync(options);

        // Batch-load all latest/started stream entries in a single query
        // instead of N individual GetByIdAsync calls.
        var entryIds = new List<int>(records.Count * 2);
        foreach (var record in records)
        {
            if (record.LatestStreamEntryId.HasValue)
                entryIds.Add(record.LatestStreamEntryId.Value);
            if (record.StartedStreamEntryId.HasValue)
                entryIds.Add(record.StartedStreamEntryId.Value);
        }
        var entriesById = await _stream.GetByIdsAsync(entryIds);

        var summariesByRunId = new Dictionary<string, SubagentRunSummary>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            var latest = record.LatestStreamEntryId.HasValue
                ? entriesById.GetValueOrDefault(record.LatestStreamEntryId.Value)
                : null;
            if (latest is null)
                continue;

            var started = record.StartedStreamEntryId.HasValue
                ? entriesById.GetValueOrDefault(record.StartedStreamEntryId.Value)
                : null;
            var summary = BuildSummaryFromRecord(record, latest, started);
            if (summary is not null)
                summariesByRunId[summary.RunId] = summary;
        }

        foreach (var summary in await ListStreamSummariesAsync(options))
        {
            if (!summariesByRunId.TryGetValue(summary.RunId, out var existing) || summary.Latest.Id > existing.Latest.Id)
            {
                _ = await _runs.RebuildFromStreamAsync(summary.RunId);
                summariesByRunId[summary.RunId] = summary;
            }
        }

        return summariesByRunId.Values
            .Where(summary => MatchesStateFilter(summary.State, options.State))
            .OrderByDescending(summary => summary.Latest.CreatedAt)
            .ThenByDescending(summary => summary.Latest.Id)
            .Take(Math.Clamp(options.Limit, 1, 50))
            .ToList();
    }

    public async Task<SubagentRunDetail?> GetAsync(string runId, SubagentRunListOptions options)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        var events = await LoadStreamEventsAsync(runId, options);
        var record = events.Count > 0
            ? await _runs.RebuildFromStreamAsync(runId)
            : await _runs.GetAsync(runId, options);

        SubagentRunSummary? summary = null;
        if (record is not null && MatchesStateFilter(record.State, options.State))
            summary = await BuildSummaryAsync(record, options, events);

        summary ??= events.Count > 0 ? BuildSummary(runId, events) : null;
        if (summary is null || !MatchesStateFilter(summary.State, options.State))
            return null;

        var artifacts = await ReadArtifactsAsync(runId, summary);
        return new SubagentRunDetail
        {
            Summary = summary,
            Events = events,
            WorkEvents = BuildWorkEvents(events, artifacts, summary),
            Artifacts = artifacts
        };
    }

    private async Task<List<SubagentRunSummary>> ListStreamSummariesAsync(SubagentRunListOptions options)
    {
        var entries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = options.ProjectId,
            TaskId = options.TaskId,
            StreamKind = AgentStreamKind.Ops,
            IncludeDebug = true,
            Limit = Math.Clamp(options.SourceLimit, 1, 200)
        });

        return entries
            .Where(entry => entry.EventType.StartsWith("subagent_", StringComparison.Ordinal))
            .Select(entry => new { Entry = entry, RunId = TextMetadata(entry, "run_id") })
            .Where(item => !string.IsNullOrWhiteSpace(item.RunId))
            .GroupBy(item => item.RunId!)
            .Select(group => BuildSummary(group.Key, group.Select(item => item.Entry)))
            .Where(summary => MatchesStateFilter(summary.State, options.State))
            .ToList();
    }

    private async Task<List<AgentStreamEntry>> LoadStreamEventsAsync(string runId, SubagentRunListOptions options)
    {
        var entries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = options.ProjectId,
            TaskId = options.TaskId,
            StreamKind = AgentStreamKind.Ops,
            MetadataRunId = runId,
            IncludeDebug = true,
            Limit = Math.Clamp(options.SourceLimit, 1, 200)
        });

        return entries
            .Where(entry => entry.EventType.StartsWith("subagent_", StringComparison.Ordinal))
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .ToList();
    }

    private async Task<SubagentRunSummary?> BuildSummaryAsync(
        AgentRunRecord record,
        SubagentRunListOptions options,
        IReadOnlyList<AgentStreamEntry>? knownEvents = null)
    {
        var latest = record.LatestStreamEntryId is null ? null : await _stream.GetByIdAsync(record.LatestStreamEntryId.Value);
        if (latest is null)
            return null;

        var started = record.StartedStreamEntryId is null ? null : await _stream.GetByIdAsync(record.StartedStreamEntryId.Value);
        var events = knownEvents is { Count: > 0 } ? knownEvents : await LoadStreamEventsAsync(record.RunId, options);
        return BuildSummary(record, latest, started, events);
    }

    /// <summary>
    /// Build a list summary from a durable record using pre-computed counters and operator events,
    /// avoiding per-run event loading. Falls back to single-entry projection if the record
    /// lacks materialized event data (pre-migration records).
    /// </summary>
    private static SubagentRunSummary? BuildSummaryFromRecord(
        AgentRunRecord record,
        AgentStreamEntry latest,
        AgentStreamEntry? started)
    {
        // Derive event counts from materialized record fields.
        var lifecycleCount = record.EventCount - record.RawWorkEventCount;
        var eventCounts = new SubagentRunEventCounts
        {
            Total = record.EventCount,
            Lifecycle = lifecycleCount,
            RawWork = record.RawWorkEventCount,
            Debug = record.RawWorkEventCount,
            OperatorSummary = DeserializeOperatorEvents(record.OperatorEventsJson)?.Count ?? 0
        };

        var operatorEvents = DeserializeOperatorEvents(record.OperatorEventsJson) ?? [];

        return new SubagentRunSummary
        {
            RunId = record.RunId,
            State = record.State,
            Schema = TextMetadata(latest, "schema") ?? TextMetadata(started, "schema"),
            SchemaVersion = IntMetadata(latest, "schema_version") ?? IntMetadata(started, "schema_version"),
            Latest = latest,
            Started = started,
            Role = record.Role ?? TextMetadata(latest, "role") ?? TextMetadata(started, "role"),
            TaskId = record.TaskId ?? latest.TaskId ?? started?.TaskId ?? IntMetadata(latest, "task_id") ?? IntMetadata(started, "task_id"),
            ProjectId = record.ProjectId ?? latest.ProjectId ?? started?.ProjectId,
            Backend = record.Backend ?? TextMetadata(latest, "backend") ?? TextMetadata(started, "backend"),
            Model = record.Model ?? TextMetadata(latest, "model") ?? TextMetadata(started, "model"),
            ReviewRoundId = record.ReviewRoundId ?? IntMetadata(latest, "review_round_id") ?? IntMetadata(started, "review_round_id"),
            WorkspaceId = record.WorkspaceId ?? TextMetadata(latest, "workspace_id") ?? TextMetadata(started, "workspace_id"),
            Purpose = TextMetadata(latest, "purpose") ?? TextMetadata(started, "purpose"),
            WorktreePath = TextMetadata(latest, "worktree_path") ?? TextMetadata(started, "worktree_path"),
            Branch = TextMetadata(latest, "branch") ?? TextMetadata(started, "branch"),
            BaseBranch = TextMetadata(latest, "base_branch") ?? TextMetadata(started, "base_branch"),
            BaseCommit = TextMetadata(latest, "base_commit") ?? TextMetadata(started, "base_commit"),
            HeadCommit = TextMetadata(latest, "head_commit") ?? TextMetadata(started, "head_commit"),
            FinalHeadCommit = TextMetadata(latest, "final_head_commit"),
            FinalHeadStatus = TextMetadata(latest, "final_head_status"),
            StartedAt = record.StartedAt ?? DateMetadata(latest, "started_at") ?? DateMetadata(started, "started_at") ?? started?.CreatedAt,
            EndedAt = record.EndedAt ?? DateMetadata(latest, "ended_at"),
            UsageSummary = UsageSummary(latest) ?? UsageSummary(started),
            EventCounts = eventCounts,
            OperatorEvents = operatorEvents,
            OutputStatus = record.OutputStatus ?? TextMetadata(latest, "output_status"),
            TimeoutKind = record.TimeoutKind ?? TextMetadata(latest, "timeout_kind"),
            InfrastructureFailureReason = record.InfrastructureFailureReason ?? TextMetadata(latest, "infrastructure_failure_reason"),
            InfrastructureWarningReason = record.InfrastructureWarningReason ?? TextMetadata(latest, "infrastructure_warning_reason"),
            ExitCode = record.ExitCode ?? IntMetadata(latest, "exit_code"),
            Signal = record.Signal ?? TextMetadata(latest, "signal"),
            Pid = record.Pid ?? IntMetadata(latest, "pid"),
            StderrPreview = TextMetadata(latest, "stderr_preview"),
            FallbackModel = record.FallbackModel ?? TextMetadata(latest, "fallback_model"),
            FallbackFromModel = record.FallbackFromModel ?? TextMetadata(latest, "fallback_from_model"),
            FallbackFromExitCode = record.FallbackFromExitCode ?? IntMetadata(latest, "fallback_from_exit_code"),
            HeartbeatCount = record.HeartbeatCount,
            AssistantOutputCount = record.AssistantOutputCount,
            LastHeartbeatAt = record.LastHeartbeatAt,
            LastAssistantOutputAt = record.LastAssistantOutputAt,
            DurationMs = record.DurationMs ?? IntMetadata(latest, "duration_ms"),
            ArtifactDir = record.ArtifactDir ?? ArtifactDir(latest) ?? ArtifactDir(started),
            EventCount = record.EventCount
        };
    }

    private static List<SubagentRunOperatorEvent>? DeserializeOperatorEvents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<SubagentRunOperatorEvent>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SubagentRunSummary BuildSummary(
        AgentRunRecord record,
        AgentStreamEntry latest,
        AgentStreamEntry? started,
        IReadOnlyList<AgentStreamEntry>? events = null)
    {
        var eventProjection = BuildEventProjection(events ?? [latest], record.Role ?? TextMetadata(latest, "role") ?? TextMetadata(started, "role"));
        return new SubagentRunSummary
        {
            RunId = record.RunId,
            State = record.State,
            Schema = TextMetadata(latest, "schema") ?? TextMetadata(started, "schema"),
            SchemaVersion = IntMetadata(latest, "schema_version") ?? IntMetadata(started, "schema_version"),
            Latest = latest,
            Started = started,
            Role = record.Role ?? TextMetadata(latest, "role") ?? TextMetadata(started, "role"),
            TaskId = record.TaskId ?? latest.TaskId ?? started?.TaskId ?? IntMetadata(latest, "task_id") ?? IntMetadata(started, "task_id"),
            ProjectId = record.ProjectId ?? latest.ProjectId ?? started?.ProjectId,
            Backend = record.Backend ?? TextMetadata(latest, "backend") ?? TextMetadata(started, "backend"),
            Model = record.Model ?? TextMetadata(latest, "model") ?? TextMetadata(started, "model"),
            ReviewRoundId = record.ReviewRoundId ?? IntMetadata(latest, "review_round_id") ?? IntMetadata(started, "review_round_id"),
            WorkspaceId = record.WorkspaceId ?? TextMetadata(latest, "workspace_id") ?? TextMetadata(started, "workspace_id"),
            Purpose = TextMetadata(latest, "purpose") ?? TextMetadata(started, "purpose"),
            WorktreePath = TextMetadata(latest, "worktree_path") ?? TextMetadata(started, "worktree_path"),
            Branch = TextMetadata(latest, "branch") ?? TextMetadata(started, "branch"),
            BaseBranch = TextMetadata(latest, "base_branch") ?? TextMetadata(started, "base_branch"),
            BaseCommit = TextMetadata(latest, "base_commit") ?? TextMetadata(started, "base_commit"),
            HeadCommit = TextMetadata(latest, "head_commit") ?? TextMetadata(started, "head_commit"),
            FinalHeadCommit = TextMetadata(latest, "final_head_commit"),
            FinalHeadStatus = TextMetadata(latest, "final_head_status"),
            StartedAt = record.StartedAt ?? DateMetadata(latest, "started_at") ?? DateMetadata(started, "started_at") ?? started?.CreatedAt,
            EndedAt = record.EndedAt ?? DateMetadata(latest, "ended_at"),
            UsageSummary = UsageSummary(latest) ?? UsageSummary(started),
            EventCounts = eventProjection.Counts,
            OperatorEvents = eventProjection.OperatorEvents,
            OutputStatus = record.OutputStatus ?? TextMetadata(latest, "output_status"),
            TimeoutKind = record.TimeoutKind ?? TextMetadata(latest, "timeout_kind"),
            InfrastructureFailureReason = record.InfrastructureFailureReason ?? TextMetadata(latest, "infrastructure_failure_reason"),
            InfrastructureWarningReason = record.InfrastructureWarningReason ?? TextMetadata(latest, "infrastructure_warning_reason"),
            ExitCode = record.ExitCode ?? IntMetadata(latest, "exit_code"),
            Signal = record.Signal ?? TextMetadata(latest, "signal"),
            Pid = record.Pid ?? IntMetadata(latest, "pid"),
            StderrPreview = TextMetadata(latest, "stderr_preview"),
            FallbackModel = record.FallbackModel ?? TextMetadata(latest, "fallback_model"),
            FallbackFromModel = record.FallbackFromModel ?? TextMetadata(latest, "fallback_from_model"),
            FallbackFromExitCode = record.FallbackFromExitCode ?? IntMetadata(latest, "fallback_from_exit_code"),
            HeartbeatCount = record.HeartbeatCount,
            AssistantOutputCount = record.AssistantOutputCount,
            LastHeartbeatAt = record.LastHeartbeatAt,
            LastAssistantOutputAt = record.LastAssistantOutputAt,
            DurationMs = record.DurationMs ?? IntMetadata(latest, "duration_ms"),
            ArtifactDir = record.ArtifactDir ?? ArtifactDir(latest) ?? ArtifactDir(started),
            EventCount = record.EventCount
        };
    }

    private static SubagentRunSummary BuildSummary(string runId, IEnumerable<AgentStreamEntry> entries)
    {
        var sorted = entries
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .ToList();
        var latest = sorted[^1];
        var started = sorted.FirstOrDefault(entry => entry.EventType == "subagent_started");
        var processStarted = sorted.LastOrDefault(entry => entry.EventType == "subagent_process_started");
        var fallbackStarted = sorted.LastOrDefault(entry => entry.EventType == "subagent_fallback_started");
        var heartbeats = sorted.Where(entry => entry.EventType == "subagent_heartbeat").ToList();
        var assistantOutputs = sorted.Where(entry => entry.EventType == "subagent_assistant_output").ToList();
        var lastHeartbeat = heartbeats.LastOrDefault();
        var lastAssistantOutput = assistantOutputs.LastOrDefault();
        var role = TextMetadata(latest, "role") ?? TextMetadata(started, "role");
        var eventProjection = BuildEventProjection(sorted, role);

        return new SubagentRunSummary
        {
            RunId = runId,
            State = StateFromEvent(latest.EventType),
            Schema = TextMetadata(latest, "schema") ?? TextMetadata(started, "schema"),
            SchemaVersion = IntMetadata(latest, "schema_version") ?? IntMetadata(started, "schema_version"),
            Latest = latest,
            Started = started,
            Role = role,
            TaskId = latest.TaskId ?? started?.TaskId ?? IntMetadata(latest, "task_id") ?? IntMetadata(started, "task_id"),
            ProjectId = latest.ProjectId ?? started?.ProjectId,
            Backend = TextMetadata(latest, "backend") ?? TextMetadata(started, "backend"),
            Model = TextMetadata(latest, "model") ?? TextMetadata(started, "model"),
            ReviewRoundId = IntMetadata(latest, "review_round_id") ?? IntMetadata(started, "review_round_id"),
            WorkspaceId = TextMetadata(latest, "workspace_id") ?? TextMetadata(started, "workspace_id"),
            Purpose = TextMetadata(latest, "purpose") ?? TextMetadata(started, "purpose"),
            WorktreePath = TextMetadata(latest, "worktree_path") ?? TextMetadata(started, "worktree_path"),
            Branch = TextMetadata(latest, "branch") ?? TextMetadata(started, "branch"),
            BaseBranch = TextMetadata(latest, "base_branch") ?? TextMetadata(started, "base_branch"),
            BaseCommit = TextMetadata(latest, "base_commit") ?? TextMetadata(started, "base_commit"),
            HeadCommit = TextMetadata(latest, "head_commit") ?? TextMetadata(started, "head_commit"),
            FinalHeadCommit = TextMetadata(latest, "final_head_commit"),
            FinalHeadStatus = TextMetadata(latest, "final_head_status"),
            StartedAt = DateMetadata(latest, "started_at") ?? DateMetadata(started, "started_at") ?? started?.CreatedAt,
            EndedAt = DateMetadata(latest, "ended_at"),
            UsageSummary = UsageSummary(latest) ?? UsageSummary(started),
            EventCounts = eventProjection.Counts,
            OperatorEvents = eventProjection.OperatorEvents,
            OutputStatus = TextMetadata(latest, "output_status"),
            TimeoutKind = TextMetadata(latest, "timeout_kind"),
            InfrastructureFailureReason = TextMetadata(latest, "infrastructure_failure_reason"),
            InfrastructureWarningReason = TextMetadata(latest, "infrastructure_warning_reason"),
            ExitCode = IntMetadata(latest, "exit_code"),
            Signal = TextMetadata(latest, "signal"),
            Pid = IntMetadata(latest, "pid") ?? EventIntMetadata(processStarted, "pid"),
            StderrPreview = TextMetadata(latest, "stderr_preview"),
            FallbackModel = TextMetadata(latest, "fallback_model") ?? TextMetadata(fallbackStarted, "fallback_model"),
            FallbackFromModel = TextMetadata(latest, "fallback_from_model") ?? TextMetadata(fallbackStarted, "failed_model"),
            FallbackFromExitCode = IntMetadata(latest, "fallback_from_exit_code") ?? IntMetadata(fallbackStarted, "failed_exit_code"),
            HeartbeatCount = heartbeats.Count,
            AssistantOutputCount = assistantOutputs.Count,
            LastHeartbeatAt = lastHeartbeat?.CreatedAt,
            LastAssistantOutputAt = lastAssistantOutput?.CreatedAt,
            DurationMs = IntMetadata(latest, "duration_ms"),
            ArtifactDir = ArtifactDir(latest) ?? ArtifactDir(started),
            EventCount = sorted.Count
        };
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

    private static string? TextMetadata(AgentStreamEntry? entry, string propertyName)
    {
        if (!TryGetMetadataProperty(entry, propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;
    }

    private static int? IntMetadata(AgentStreamEntry? entry, string propertyName)
    {
        if (!TryGetMetadataProperty(entry, propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static int? EventIntMetadata(AgentStreamEntry? entry, string propertyName)
    {
        if (!TryGetEventMetadataProperty(entry, propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static DateTime? DateMetadata(AgentStreamEntry? entry, string propertyName)
    {
        var text = TextMetadata(entry, propertyName);
        return text is not null && DateTime.TryParse(text, out var value) ? value : null;
    }

    private static SubagentRunUsageSummary? UsageSummary(AgentStreamEntry? entry)
    {
        if (!TryGetMetadataProperty(entry, "usage_summary", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var summary = new SubagentRunUsageSummary
        {
            InputTokens = IntProperty(usage, "input_tokens"),
            OutputTokens = IntProperty(usage, "output_tokens"),
            CacheReadTokens = IntProperty(usage, "cache_read_tokens"),
            CacheWriteTokens = IntProperty(usage, "cache_write_tokens"),
            TotalTokens = IntProperty(usage, "total_tokens"),
            TotalCost = DoubleProperty(usage, "total_cost"),
            Currency = TextProperty(usage, "currency"),
            Source = TextProperty(usage, "source"),
            MessageCount = IntProperty(usage, "message_count"),
            LatestUsageAt = DateProperty(usage, "latest_usage_at")
        };

        return summary.InputTokens is null &&
            summary.OutputTokens is null &&
            summary.CacheReadTokens is null &&
            summary.CacheWriteTokens is null &&
            summary.TotalTokens is null &&
            summary.TotalCost is null
                ? null
                : summary;
    }

    private static (SubagentRunEventCounts Counts, List<SubagentRunOperatorEvent> OperatorEvents) BuildEventProjection(
        IReadOnlyList<AgentStreamEntry> events,
        string? role)
    {
        var counts = new SubagentRunEventCounts { Total = events.Count };
        var operatorEvents = new List<SubagentRunOperatorEvent>();

        foreach (var entry in events)
        {
            if (SubagentRunLifecycleConventions.IsRawWorkEvent(entry.EventType))
            {
                counts.RawWork++;
                counts.Debug++;
                continue;
            }

            counts.Lifecycle++;
            var entryRole = TextMetadata(entry, "role") ?? role;
            var eventName = TextMetadata(entry, "operator_event") ??
                SubagentRunLifecycleConventions.OperatorEventForSubagentRun(entry.EventType, entryRole);
            if (eventName is null)
                continue;

            operatorEvents.Add(new SubagentRunOperatorEvent
            {
                EventName = eventName,
                Source = "agent_stream",
                SourceEventType = entry.EventType,
                StreamEntryId = entry.Id,
                OccurredAt = entry.CreatedAt,
                Visibility = TextMetadata(entry, "event_visibility") ?? SubagentRunLifecycleConventions.VisibilityForStreamEvent(entry.EventType)
            });
        }

        counts.OperatorSummary = operatorEvents.Count;
        return (counts, operatorEvents);
    }

    private static string? ArtifactDir(AgentStreamEntry? entry)
    {
        if (!TryGetMetadataProperty(entry, "artifacts", out var artifacts) ||
            artifacts.ValueKind != JsonValueKind.Object ||
            !artifacts.TryGetProperty("dir", out var dir) ||
            dir.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(dir.GetString()) ? null : dir.GetString();
    }

    private static List<JsonElement> BuildWorkEvents(
        IReadOnlyList<AgentStreamEntry> streamEvents,
        SubagentRunArtifactSnapshot? artifacts,
        SubagentRunSummary summary)
    {
        var reasoningPolicy = ReasoningCapturePolicy.FromStatusJson(artifacts?.StatusJson);
        var sessionEvents = ParseSessionWorkEvents(artifacts?.SessionTail, reasoningPolicy);
        if (sessionEvents.Count > 0)
            return EnrichWorkEvents(sessionEvents, summary);

        var artifactEvents = ParseWorkEvents(artifacts?.EventsTail);
        if (artifactEvents.Count > 0)
            return EnrichWorkEvents(artifactEvents, summary);

        var streamWorkEvents = streamEvents
            .Select(StreamWorkEvent)
            .Where(workEvent => workEvent.HasValue)
            .Select(workEvent => workEvent!.Value)
            .TakeLast(80)
            .ToList();
        return EnrichWorkEvents(streamWorkEvents, summary);
    }

    private static List<JsonElement> EnrichWorkEvents(IEnumerable<JsonElement> events, SubagentRunSummary summary)
    {
        return events.Select(workEvent => EnrichWorkEvent(workEvent, summary)).ToList();
    }

    private static JsonElement EnrichWorkEvent(JsonElement workEvent, SubagentRunSummary summary)
    {
        if (workEvent.ValueKind != JsonValueKind.Object)
            return workEvent.Clone();

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workEvent.GetRawText()) ?? [];
        AddMissing(payload, "run_id", summary.RunId);
        AddMissing(payload, "task_id", summary.TaskId);
        AddMissing(payload, "subagent_role", summary.Role);
        AddMissing(payload, "backend", summary.Backend);
        AddMissing(payload, "requested_model", summary.Model);
        return JsonSerializer.SerializeToElement(payload);
    }

    private static void AddMissing(Dictionary<string, JsonElement> payload, string key, object? value)
    {
        if (payload.ContainsKey(key) || value is null)
            return;
        payload[key] = JsonSerializer.SerializeToElement(value);
    }

    private static List<JsonElement> ParseSessionWorkEvents(string? sessionJsonl, ReasoningCapturePolicy reasoningPolicy)
    {
        if (string.IsNullOrWhiteSpace(sessionJsonl))
            return [];

        var events = new List<JsonElement>();
        foreach (var line in sessionJsonl.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "...")
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                events.AddRange(NormalizeSessionEntry(doc.RootElement, reasoningPolicy));
            }
            catch (JsonException)
            {
                // The artifact snapshot may begin mid-line when tailing a large file.
            }
        }

        return events.TakeLast(80).ToList();
    }

    private static IReadOnlyList<JsonElement> NormalizeSessionEntry(JsonElement entry, ReasoningCapturePolicy reasoningPolicy)
    {
        if (entry.ValueKind != JsonValueKind.Object || !TryGetString(entry, "type", out var type))
            return [];

        return type switch
        {
            "session" => [WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_session",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_session",
                ["session_id"] = StringOrNull(entry, "id"),
                ["cwd"] = StringOrNull(entry, "cwd"),
                ["version"] = NumberOrNull(entry, "version")
            })],
            "message" => NormalizeSessionMessageEntry(entry, reasoningPolicy),
            "compaction" => [WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_compaction",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_compaction",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["text_preview"] = Preview(StringOrNull(entry, "summary")),
                ["tokens_before"] = NumberOrNull(entry, "tokensBefore"),
                ["first_kept_entry_id"] = StringOrNull(entry, "firstKeptEntryId")
            })],
            "branch_summary" => [WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_branch_summary",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_branch_summary",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["from_id"] = StringOrNull(entry, "fromId"),
                ["text_preview"] = Preview(StringOrNull(entry, "summary"))
            })],
            "custom" => [WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_custom",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_custom",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["custom_type"] = StringOrNull(entry, "customType"),
                ["result_preview"] = Preview(JsonPreview(entry, "data"))
            })],
            "custom_message" => [WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_custom_message",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_custom_message",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["custom_type"] = StringOrNull(entry, "customType"),
                ["text_preview"] = Preview(ContentText(entry.TryGetProperty("content", out var content) ? content : default)),
                ["display"] = BoolOrNull(entry, "display")
            })],
            _ => []
        };
    }

    private static IReadOnlyList<JsonElement> NormalizeSessionMessageEntry(JsonElement entry, ReasoningCapturePolicy reasoningPolicy)
    {
        if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return [];
        if (!TryGetString(message, "role", out var role))
            return [];
        if (role == "user")
            return []; // Avoid surfacing raw prompts in the Den work feed.

        var common = new Dictionary<string, object?>
        {
            ["ts"] = TimestampMs(entry),
            ["entry_id"] = StringOrNull(entry, "id"),
            ["parent_id"] = StringOrNull(entry, "parentId"),
            ["role"] = role
        };

        switch (role)
        {
            case "assistant":
                return NormalizeSessionAssistantMessage(common, message, reasoningPolicy);
            case "toolResult":
                common["type"] = "subagent.work_tool_end";
                common["source_type"] = "session_tree_tool_result";
                common["tool_call_id"] = StringOrNull(message, "toolCallId");
                common["tool_name"] = StringOrNull(message, "toolName");
                common["result_preview"] = Preview(ContentText(message.TryGetProperty("content", out var toolContent) ? toolContent : default) ?? JsonPreview(message, "content"));
                common["is_error"] = BoolOrNull(message, "isError") ?? false;
                return [WorkEvent(common)];
            case "bashExecution":
                common["type"] = "subagent.work_bash_execution";
                common["source_type"] = "session_tree_bash_execution";
                common["tool_name"] = "bash";
                common["args_preview"] = Preview(StringOrNull(message, "command"));
                common["result_preview"] = Preview(StringOrNull(message, "output"));
                common["exit_code"] = NumberOrNull(message, "exitCode");
                common["cancelled"] = BoolOrNull(message, "cancelled");
                common["truncated"] = BoolOrNull(message, "truncated");
                common["is_error"] = NumberOrNull(message, "exitCode") is not null and not 0;
                return [WorkEvent(common)];
            case "custom":
                common["type"] = "subagent.work_custom_message";
                common["source_type"] = "session_tree_custom_message";
                common["custom_type"] = StringOrNull(message, "customType");
                common["text_preview"] = Preview(ContentText(message.TryGetProperty("content", out var customContent) ? customContent : default));
                common["display"] = BoolOrNull(message, "display");
                return [WorkEvent(common)];
            case "branchSummary":
                common["type"] = "subagent.work_branch_summary";
                common["source_type"] = "session_tree_branch_summary_message";
                common["from_id"] = StringOrNull(message, "fromId");
                common["text_preview"] = Preview(StringOrNull(message, "summary"));
                return [WorkEvent(common)];
            case "compactionSummary":
                common["type"] = "subagent.work_compaction";
                common["source_type"] = "session_tree_compaction_message";
                common["text_preview"] = Preview(StringOrNull(message, "summary"));
                common["tokens_before"] = NumberOrNull(message, "tokensBefore");
                return [WorkEvent(common)];
            default:
                return [];
        }
    }

    private static IReadOnlyList<JsonElement> NormalizeSessionAssistantMessage(Dictionary<string, object?> common, JsonElement message, ReasoningCapturePolicy reasoningPolicy)
    {
        var content = message.TryGetProperty("content", out var value) ? value : default;
        var text = ContentText(content, includeThinking: false);
        var thinkingText = ContentText(content, includeThinking: true, thinkingOnly: true);
        var providerVisibleSummaryText = ReasoningSummaryText(content);
        var summaryText = reasoningPolicy.CaptureProviderSummaries ? providerVisibleSummaryText : null;
        var rawThinkingText = !string.IsNullOrWhiteSpace(providerVisibleSummaryText) && thinkingText == providerVisibleSummaryText ? null : thinkingText;
        var thinkingChars = rawThinkingText?.Length ?? providerVisibleSummaryText?.Length;
        var contentTypes = ContentTypes(content);
        var toolCalls = ToolCalls(content);
        var provider = StringOrNull(message, "provider");
        var model = StringOrNull(message, "model");
        var stopReason = StringOrNull(message, "stopReason");
        var events = new List<JsonElement>();

        if (thinkingChars is > 0 || ContentTypesIncludeThinking(contentTypes))
        {
            var exposeRaw = reasoningPolicy.CaptureRawLocalPreviews && !ContentHasRedactedThinking(content) && !string.IsNullOrWhiteSpace(rawThinkingText);
            var reasoning = new Dictionary<string, object?>(common)
            {
                ["type"] = "subagent.work_reasoning_end",
                ["source_type"] = "session_tree_message",
                ["provider"] = provider,
                ["model"] = model,
                ["reasoning_kind"] = "thinking",
                ["reasoning_chars"] = thinkingChars,
                ["reasoning_redacted"] = !exposeRaw,
                ["text_preview"] = exposeRaw ? Preview(rawThinkingText, reasoningPolicy.PreviewChars) : null,
                ["reasoning_summary_preview"] = Preview(summaryText, reasoningPolicy.PreviewChars),
                ["reasoning_summary_chars"] = summaryText?.Length,
                ["reasoning_summary_source"] = string.IsNullOrWhiteSpace(summaryText) ? null : "provider_visible",
                ["content_types"] = contentTypes,
                ["stop_reason"] = stopReason
            };
            events.Add(WorkEvent(reasoning));
        }

        if (!string.IsNullOrWhiteSpace(text) || toolCalls is { Count: > 0 })
        {
            var messageEvent = new Dictionary<string, object?>(common)
            {
                ["type"] = "subagent.work_message_end",
                ["source_type"] = "session_tree_message",
                ["provider"] = provider,
                ["model"] = model,
                ["text_preview"] = Preview(text),
                ["text_chars"] = text?.Length,
                ["thinking_chars"] = thinkingChars,
                ["reasoning_chars"] = thinkingChars,
                ["reasoning_redacted"] = thinkingChars is > 0,
                ["reasoning_summary_preview"] = Preview(summaryText, reasoningPolicy.PreviewChars),
                ["reasoning_summary_chars"] = summaryText?.Length,
                ["reasoning_summary_source"] = string.IsNullOrWhiteSpace(summaryText) ? null : "provider_visible",
                ["content_types"] = contentTypes,
                ["tool_calls"] = toolCalls,
                ["stop_reason"] = stopReason
            };
            events.Add(WorkEvent(messageEvent));
        }

        return events;
    }

    private static JsonElement WorkEvent(Dictionary<string, object?> payload)
    {
        var compact = payload
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return JsonSerializer.SerializeToElement(compact);
    }

    private static List<Dictionary<string, object?>>? ToolCalls(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var calls = new List<Dictionary<string, object?>>();
        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type) || type.GetString() != "toolCall")
                continue;
            calls.Add(new Dictionary<string, object?>
            {
                ["id"] = StringOrNull(part, "id"),
                ["name"] = StringOrNull(part, "name"),
                ["args_preview"] = Preview(JsonPreview(part, "arguments"))
            });
        }

        return calls.Count == 0 ? null : calls;
    }

    private static List<string>? ContentTypes(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var types = content.EnumerateArray()
            .Select(part => StringOrNull(part, "type"))
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();
        return types.Count == 0 ? null : types;
    }

    private static bool ContentTypesIncludeThinking(List<string>? contentTypes) =>
        contentTypes?.Any(type => type.Contains("thinking", StringComparison.OrdinalIgnoreCase) || type.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool ContentHasRedactedThinking(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return false;

        return content.EnumerateArray().Any(part =>
            (StringOrNull(part, "type") is "thinking" or "reasoning") &&
            BoolOrNull(part, "redacted") == true);
    }

    private sealed record ReasoningCapturePolicy(bool CaptureProviderSummaries, bool CaptureRawLocalPreviews, int PreviewChars)
    {
        public static ReasoningCapturePolicy FromStatusJson(string? statusJson)
        {
            var captureProviderSummaries = true;
            var captureRawLocalPreviews = RawReasoningCaptureEnvValue() ?? false;
            var previewChars = 240;

            if (!string.IsNullOrWhiteSpace(statusJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(statusJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("reasoning_capture", out var reasoning) &&
                        reasoning.ValueKind == JsonValueKind.Object)
                    {
                        captureProviderSummaries = BoolOrNull(reasoning, "capture_provider_summaries") ?? captureProviderSummaries;
                        captureRawLocalPreviews = BoolOrNull(reasoning, "capture_raw_local_previews") ?? captureRawLocalPreviews;
                        if (NumberOrNull(reasoning, "preview_chars") is { } configuredPreviewChars)
                            previewChars = (int)configuredPreviewChars;
                    }
                }
                catch (JsonException)
                {
                    // Fall back to defaults for older or partially written status artifacts.
                }
            }

            return new ReasoningCapturePolicy(
                captureProviderSummaries,
                captureRawLocalPreviews,
                Math.Clamp(previewChars, 1, 2_000));
        }
    }

    private static bool? RawReasoningCaptureEnvValue()
    {
        var value = Environment.GetEnvironmentVariable("DEN_PI_SUBAGENT_RAW_REASONING")?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    private static string? ReasoningSummaryText(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var part in content.EnumerateArray())
        {
            if (StringOrNull(part, "type") is not ("thinking" or "reasoning"))
                continue;

            var direct = SummaryText(part, "summary") ??
                SummaryText(part, "summaryText") ??
                SummaryText(part, "summary_text") ??
                SummaryText(part, "reasoningSummary") ??
                SummaryText(part, "reasoning_summary") ??
                SummaryText(part, "thinkingSummary") ??
                SummaryText(part, "thinking_summary");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            var signatureSummary = ReasoningSummaryFromSignature(StringOrNull(part, "thinkingSignature") ??
                StringOrNull(part, "reasoningSignature") ??
                StringOrNull(part, "signature"));
            if (!string.IsNullOrWhiteSpace(signatureSummary))
                return signatureSummary;
        }

        return null;
    }

    private static string? ReasoningSummaryFromSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || !signature.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(signature);
            return SummaryText(doc.RootElement, "summary") ??
                SummaryText(doc.RootElement, "reasoning_summary") ??
                SummaryText(doc.RootElement, "thinking_summary");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SummaryText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        return SummaryText(property);
    }

    private static string? SummaryText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString();
        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = element.EnumerateArray()
                .Select(SummaryText)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            return SummaryText(element, "text") ??
                SummaryText(element, "summary") ??
                SummaryText(element, "content");
        }
        return null;
    }

    private static string? ContentText(JsonElement content, bool includeThinking = true, bool thinkingOnly = false)
    {
        if (content.ValueKind == JsonValueKind.String)
            return thinkingOnly ? null : content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type))
                continue;
            var typeName = type.GetString();
            if (typeName == "text" && !thinkingOnly && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                parts.Add(text.GetString()!);
            if ((typeName == "thinking" || typeName == "reasoning") && includeThinking)
            {
                if (part.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String)
                    parts.Add(thinking.GetString()!);
                else if (part.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                    parts.Add(reasoning.GetString()!);
            }
        }

        return parts.Count == 0 ? null : string.Join("", parts);
    }

    private static int? ContentTextLength(JsonElement content, bool includeThinking = true, bool thinkingOnly = false)
    {
        var text = ContentText(content, includeThinking, thinkingOnly);
        return text is null ? null : text.Length;
    }

    private static long? TimestampMs(JsonElement entry)
    {
        if (!TryGetString(entry, "timestamp", out var value) || !DateTimeOffset.TryParse(value, out var timestamp))
            return null;
        return timestamp.ToUnixTimeMilliseconds();
    }

    private static string? Preview(string? value, int maxChars = 500)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxChars ? normalized : string.Concat(normalized.AsSpan(0, maxChars), "…");
    }

    private static string? JsonPreview(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            ? property.GetRawText()
            : null;
    }

    private static string? StringOrNull(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName, out var value) ? value : null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static long? NumberOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
            return null;
        return property.TryGetInt64(out var value) ? value : null;
    }

    private static bool? BoolOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? IntProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static double? DoubleProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var value)
            ? value
            : null;

    private static string? TextProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static DateTime? DateProperty(JsonElement element, string propertyName)
    {
        var text = TextProperty(element, propertyName);
        return text is not null && DateTime.TryParse(text, out var value) ? value : null;
    }

    private static List<JsonElement> ParseWorkEvents(string? eventsJsonl)
    {
        if (string.IsNullOrWhiteSpace(eventsJsonl))
            return [];

        var events = new List<JsonElement>();
        foreach (var line in eventsJsonl.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "...")
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (IsSubagentWorkEvent(doc.RootElement))
                    events.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // The artifact snapshot may begin mid-line when tailing a large file.
            }
        }

        return events.TakeLast(80).ToList();
    }

    private static JsonElement? StreamWorkEvent(AgentStreamEntry entry)
    {
        if (!entry.EventType.StartsWith("subagent_work_", StringComparison.Ordinal) ||
            !TryGetMetadataProperty(entry, "event", out var eventMetadata) ||
            !IsSubagentWorkEvent(eventMetadata))
        {
            return null;
        }

        return eventMetadata.Clone();
    }

    private static bool IsSubagentWorkEvent(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            type.GetString()?.StartsWith("subagent.work_", StringComparison.Ordinal) == true;
    }

    private static bool TryGetMetadataProperty(
        AgentStreamEntry? entry,
        string propertyName,
        out JsonElement property)
    {
        property = default;
        return entry?.Metadata is { } metadata &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty(propertyName, out property);
    }

    private static bool TryGetEventMetadataProperty(
        AgentStreamEntry? entry,
        string propertyName,
        out JsonElement property)
    {
        property = default;
        return TryGetMetadataProperty(entry, "event", out var eventMetadata) &&
            eventMetadata.ValueKind == JsonValueKind.Object &&
            eventMetadata.TryGetProperty(propertyName, out property);
    }

    private static async Task<SubagentRunArtifactSnapshot?> ReadArtifactsAsync(string runId, SubagentRunSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.ArtifactDir))
            return null;

        var artifactDir = summary.ArtifactDir;
        if (!IsSafeArtifactDir(runId, artifactDir))
        {
            return new SubagentRunArtifactSnapshot
            {
                Dir = artifactDir,
                Readable = false,
                ReadError = "Artifact directory did not match the expected den-subagent-runs/<run_id> shape."
            };
        }

        try
        {
            var sessionFilePath = FindSessionFile(artifactDir);
            return new SubagentRunArtifactSnapshot
            {
                Dir = artifactDir,
                Readable = true,
                StatusJson = await ReadArtifactTailAsync(artifactDir, "status.json"),
                EventsTail = await ReadArtifactTailAsync(artifactDir, "events.jsonl"),
                StdoutTail = await ReadArtifactTailAsync(artifactDir, "stdout.jsonl"),
                StderrTail = await ReadArtifactTailAsync(artifactDir, "stderr.log"),
                SessionFilePath = sessionFilePath,
                SessionTail = sessionFilePath is null ? null : await ReadArtifactPathTailAsync(artifactDir, sessionFilePath)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SubagentRunArtifactSnapshot
            {
                Dir = artifactDir,
                Readable = false,
                ReadError = ex.Message
            };
        }
    }

    private static bool IsSafeArtifactDir(string runId, string artifactDir)
    {
        try
        {
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
            var parent = Directory.GetParent(fullDir);
            return Path.GetFileName(fullDir).Equals(runId, StringComparison.Ordinal) &&
                parent?.Name.Equals("den-subagent-runs", StringComparison.Ordinal) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindSessionFile(string artifactDir)
    {
        var sessionDir = Path.Combine(artifactDir, "sessions");
        if (!Directory.Exists(sessionDir))
            return null;

        var fullArtifactDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
        var dirWithSeparator = $"{fullArtifactDir}{Path.DirectorySeparatorChar}";
        return Directory.EnumerateFiles(sessionDir, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Where(path => path.StartsWith(dirWithSeparator, StringComparison.Ordinal))
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault();
    }

    private static Task<string?> ReadArtifactTailAsync(string artifactDir, string fileName)
    {
        var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
        return ReadArtifactPathTailAsync(fullDir, Path.Combine(fullDir, fileName));
    }

    private static async Task<string?> ReadArtifactPathTailAsync(string artifactDir, string filePath)
    {
        var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
        var fullPath = Path.GetFullPath(filePath);
        var dirWithSeparator = $"{fullDir}{Path.DirectorySeparatorChar}";
        if (!fullPath.StartsWith(dirWithSeparator, StringComparison.Ordinal) || !File.Exists(fullPath))
            return null;

        await using var stream = File.OpenRead(fullPath);
        var bytesToRead = (int)Math.Min(stream.Length, MaxArtifactTailBytes);
        if (bytesToRead == 0)
            return string.Empty;

        stream.Seek(-bytesToRead, SeekOrigin.End);
        var buffer = new byte[bytesToRead];
        var read = await stream.ReadAsync(buffer);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        return stream.Length > MaxArtifactTailBytes ? $"...\n{text}" : text;
    }
}
