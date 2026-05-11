namespace DenMcp.Core.Models;

public sealed class AgentRunRecord
{
    public required string RunId { get; set; }
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? ReviewRoundId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? Role { get; set; }
    public string? Backend { get; set; }
    public string? Model { get; set; }
    public string? SenderInstanceId { get; set; }
    public required string State { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationMs { get; set; }
    public int? Pid { get; set; }
    public int? ExitCode { get; set; }
    public string? Signal { get; set; }
    public string? TimeoutKind { get; set; }
    public string? OutputStatus { get; set; }
    public string? InfrastructureFailureReason { get; set; }
    public string? InfrastructureWarningReason { get; set; }
    public string? ArtifactDir { get; set; }
    public string? StdoutJsonlPath { get; set; }
    public string? StderrLogPath { get; set; }
    public string? StatusJsonPath { get; set; }
    public string? EventsJsonlPath { get; set; }
    public string? RerunOfRunId { get; set; }
    public string? FallbackModel { get; set; }
    public string? FallbackFromModel { get; set; }
    public int? FallbackFromExitCode { get; set; }
    public int? LatestStreamEntryId { get; set; }
    public int? StartedStreamEntryId { get; set; }
    public int HeartbeatCount { get; set; }
    public int AssistantOutputCount { get; set; }
    public int EventCount { get; set; }
    public int RawWorkEventCount { get; set; }
    public string? OperatorEventsJson { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? LastAssistantOutputAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
