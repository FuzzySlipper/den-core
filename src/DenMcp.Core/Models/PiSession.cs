using System.Text.Json;

namespace DenMcp.Core.Models;

public static class PiSessionStates
{
    public const string Launching = "launching";
    public const string Running = "running";
    public const string Stale = "stale";
    public const string Terminating = "terminating";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsActive(string? state) => state is Launching or Running or Terminating;
}

public static class PiSessionAttentionStates
{
    public const string UserInputNeeded = "user_input_needed";
    public const string WaitingForDirection = "waiting_for_direction";
    public const string Blocked = "blocked";
    public const string Stalled = "stalled";
}

public sealed class PiSessionLaunchRequest
{
    public string? SessionId { get; init; }
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RunId { get; init; }
    public string? Title { get; init; }
    public string? RequestedBy { get; init; }
    public string? ToolProfile { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public string? DevDir { get; init; }
    public string? PiStateDir { get; init; }
    public string? ComposeFile { get; init; }
    public string? Service { get; init; }
    public string? Image { get; init; }
    public string? PiVersion { get; init; }
    public string? NodeVersion { get; init; }
    public string? GitConfigPath { get; init; }
    public string? SshDir { get; init; }
    public string? GhConfigDir { get; init; }
    public IReadOnlyList<PiDockerCallbackPort> CallbackPorts { get; init; } = [];
    public int? PromptPacketMessageId { get; init; }
    public string? StateFileRef { get; init; }
    public string? StartupPrompt { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class PiSessionRegistrationRequest
{
    public string? SessionId { get; init; }
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RunId { get; init; }
    public string? Title { get; init; }
    public string? RequestedBy { get; init; }
    public string? Role { get; init; }
    public required string Substrate { get; init; }
    public string? Host { get; init; }
    public string? Workdir { get; init; }
    public string? Branch { get; init; }
    public string? BaseBranch { get; init; }
    public string? BaseCommit { get; init; }
    public string? HeadCommit { get; init; }
    public string? Profile { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Toolsets { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? ArtifactPath { get; init; }
    public string? LogPath { get; init; }
    public int? PromptPacketMessageId { get; init; }
    public string? StateFileRef { get; init; }
}

public sealed class PiSessionControlRequest
{
    public string? RequestedBy { get; init; }
    public string? Reason { get; init; }
}

public sealed class PiSessionAttachRequest
{
    public string? RequestedBy { get; init; }
    public string? Mode { get; init; }
}

public sealed class PiSessionListOptions
{
    public required string ProjectId { get; init; }
    public int? TaskId { get; init; }
    public string? State { get; init; }
    public string? AttentionState { get; init; }
    public bool? NeedsUserInput { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed class PiSessionRecord
{
    public required string SessionId { get; init; }
    public required string ProjectId { get; init; }
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RunId { get; init; }
    public string? Title { get; init; }
    public string? ToolProfile { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public required string HostId { get; init; }
    public required string TmuxSessionName { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public required string LaunchProfileKind { get; init; }
    public string? LaunchProfileId { get; init; }
    public required string LaunchProfileJson { get; init; }
    public required string LaunchCommandJson { get; init; }
    public required string LaunchCommandDisplay { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public string? OutputTail { get; init; }
    public DateTime? OutputTailCapturedAt { get; init; }
    public bool OutputTailTruncated { get; init; }
    public string? OutputTailSha256 { get; init; }
    public string? AttentionState { get; init; }
    public string? AttentionReason { get; init; }
    public DateTime? AttentionSinceAt { get; init; }
    public DateTime? AttentionUpdatedAt { get; init; }
    public bool NeedsUserInput { get; init; }
    public DateTime? EndedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? TerminationRequestedAt { get; init; }
    public string? TerminationRequestedBy { get; init; }
    public string? TerminationReason { get; init; }
    public DateTime? CleanupRequestedAt { get; init; }
    public string? CleanupRequestedBy { get; init; }
    public string? CleanupReason { get; init; }
    public DateTime? CleanupCompletedAt { get; init; }
}

public sealed class PiSessionEvent
{
    public long Id { get; init; }
    public required string ProjectId { get; init; }
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string EventType { get; init; }
    public string? Payload { get; init; }
    public string? RequestedBy { get; init; }
    public string? Reason { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class PiSessionSummary
{
    public required string SessionId { get; init; }
    public required string ProjectId { get; init; }
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RunId { get; init; }
    public string? Title { get; init; }
    public string? ToolProfile { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public required string HostId { get; init; }
    public required string TmuxSessionName { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public string? LaunchProfileKind { get; init; }
    public string? LaunchProfileId { get; init; }
    public IReadOnlyList<string> LaunchCommand { get; init; } = [];
    public string? LaunchCommandDisplay { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public string? OutputTail { get; init; }
    public DateTime? OutputTailCapturedAt { get; init; }
    public bool OutputTailTruncated { get; init; }
    public string? AttentionState { get; init; }
    public string? AttentionReason { get; init; }
    public DateTime? AttentionSinceAt { get; init; }
    public DateTime? AttentionUpdatedAt { get; init; }
    public bool NeedsUserInput { get; init; }
    public DateTime? EndedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? TerminationRequestedAt { get; init; }
    public string? TerminationRequestedBy { get; init; }
    public string? TerminationReason { get; init; }
    public DateTime? CleanupRequestedAt { get; init; }
    public string? CleanupRequestedBy { get; init; }
    public string? CleanupReason { get; init; }
    public DateTime? CleanupCompletedAt { get; init; }
}

public sealed class PiSessionDetail
{
    public required PiSessionSummary Session { get; init; }
    public PiDockerLaunchProfile? LaunchProfile { get; init; }
    public PiSessionAttachInfo? Attach { get; init; }
}

public sealed class PiSessionAttachInfo
{
    public required string Mode { get; init; }
    public required string Backend { get; init; }
    public required string TmuxSessionName { get; init; }
    public required string CommandExecutable { get; init; }
    public IReadOnlyList<string> CommandArgs { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static class PiSessionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
