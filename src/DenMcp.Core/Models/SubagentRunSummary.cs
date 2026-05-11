using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class SubagentRunSummary
{
    public required string RunId { get; set; }
    public required string State { get; set; }
    public string? Schema { get; set; }
    public int? SchemaVersion { get; set; }
    public required AgentStreamEntry Latest { get; set; }
    public AgentStreamEntry? Started { get; set; }
    public string? Role { get; set; }
    public int? TaskId { get; set; }
    public string? ProjectId { get; set; }
    public string? Backend { get; set; }
    public string? Model { get; set; }
    public int? ReviewRoundId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? Purpose { get; set; }
    public string? WorktreePath { get; set; }
    public string? Branch { get; set; }
    public string? BaseBranch { get; set; }
    public string? BaseCommit { get; set; }
    public string? HeadCommit { get; set; }
    public string? FinalHeadCommit { get; set; }
    public string? FinalHeadStatus { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SubagentRunUsageSummary? UsageSummary { get; set; }
    public SubagentRunEventCounts EventCounts { get; set; } = new();
    public List<SubagentRunOperatorEvent> OperatorEvents { get; set; } = [];
    public string? OutputStatus { get; set; }
    public string? TimeoutKind { get; set; }
    public string? InfrastructureFailureReason { get; set; }
    public string? InfrastructureWarningReason { get; set; }
    public int? ExitCode { get; set; }
    public string? Signal { get; set; }
    public int? Pid { get; set; }
    public string? StderrPreview { get; set; }
    public string? FallbackModel { get; set; }
    public string? FallbackFromModel { get; set; }
    public int? FallbackFromExitCode { get; set; }
    public int HeartbeatCount { get; set; }
    public int AssistantOutputCount { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? LastAssistantOutputAt { get; set; }
    public int? DurationMs { get; set; }
    public string? ArtifactDir { get; set; }
    public int EventCount { get; set; }
}

public sealed class SubagentRunUsageSummary
{
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadTokens { get; set; }
    public int? CacheWriteTokens { get; set; }
    public int? TotalTokens { get; set; }
    public double? TotalCost { get; set; }
    public string? Currency { get; set; }
    public string? Source { get; set; }
    public int? MessageCount { get; set; }
    public DateTime? LatestUsageAt { get; set; }
}

public sealed class SubagentRunEventCounts
{
    public int Total { get; set; }
    public int Lifecycle { get; set; }
    public int RawWork { get; set; }
    public int OperatorSummary { get; set; }
    public int Debug { get; set; }
}

public sealed class SubagentRunOperatorEvent
{
    public required string EventName { get; set; }
    public required string Source { get; set; }
    public string? SourceEventType { get; set; }
    public int? StreamEntryId { get; set; }
    public string? SourceMessageType { get; set; }
    public DateTime? OccurredAt { get; set; }
    public string Visibility { get; set; } = "summary";
}

public static class SubagentRunLifecycleConventions
{
    public const string LifecycleSchema = "den_subagent_lifecycle";
    public const int LifecycleSchemaVersion = 1;

    public static readonly IReadOnlyDictionary<string, string> TaskThreadPacketEvents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["coder_context_packet"] = "coder_context_prepared",
        ["implementation_packet"] = "implementation_packet_posted",
        ["validation_packet"] = "validation_completed",
        ["drift_check_packet"] = "drift_check_completed"
    };

    public static bool IsRawWorkEvent(string eventType) => eventType.StartsWith("subagent_work_", StringComparison.Ordinal);

    public static string VisibilityForStreamEvent(string eventType) => IsRawWorkEvent(eventType) ? "debug" : "summary";

    public static string? OperatorEventForSubagentRun(string eventType, string? role)
    {
        var normalizedRole = role?.Trim().ToLowerInvariant();
        return eventType switch
        {
            "subagent_started" when normalizedRole == "coder" => "coder_started",
            "subagent_started" when normalizedRole == "reviewer" => "reviewer_started",
            "subagent_completed" when normalizedRole == "coder" => "coder_completed",
            "subagent_completed" when normalizedRole == "reviewer" => "reviewer_completed",
            "subagent_failed" or "subagent_timeout" or "subagent_aborted" when normalizedRole == "coder" => "coder_completed",
            "subagent_failed" or "subagent_timeout" or "subagent_aborted" when normalizedRole == "reviewer" => "reviewer_completed",
            _ => null
        };
    }

    public static string? OperatorEventForTaskThreadPacket(string? packetType) =>
        !string.IsNullOrWhiteSpace(packetType) && TaskThreadPacketEvents.TryGetValue(packetType, out var eventName)
            ? eventName
            : null;
}

public sealed class SubagentRunDetail
{
    public required SubagentRunSummary Summary { get; set; }
    public required List<AgentStreamEntry> Events { get; set; }
    public List<JsonElement> WorkEvents { get; set; } = [];
    public SubagentRunArtifactSnapshot? Artifacts { get; set; }
}

public sealed class SubagentRunArtifactSnapshot
{
    public required string Dir { get; set; }
    public bool Readable { get; set; }
    public string? StatusJson { get; set; }
    public string? EventsTail { get; set; }
    public string? StdoutTail { get; set; }
    public string? StderrTail { get; set; }
    public string? SessionFilePath { get; set; }
    public string? SessionTail { get; set; }
    public string? ReadError { get; set; }
}

public sealed class SubagentRunListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? State { get; set; }
    public int Limit { get; set; } = 8;
    public int SourceLimit { get; set; } = 200;
}
