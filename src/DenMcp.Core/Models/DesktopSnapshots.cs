using System.Text.Json;

namespace DenMcp.Core.Models;

public enum DesktopSnapshotState
{
    Ok,
    PathNotVisible,
    NotGitRepository,
    GitError,
    SourceOffline,
    Missing
}

public sealed class DesktopGitSnapshot
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public required string RootPath { get; set; }
    public DesktopSnapshotState State { get; set; } = DesktopSnapshotState.Ok;
    public string? Branch { get; set; }
    public bool IsDetached { get; set; }
    public string? HeadSha { get; set; }
    public string? Upstream { get; set; }
    public int? Ahead { get; set; }
    public int? Behind { get; set; }
    public GitDirtyCounts DirtyCounts { get; set; } = new();
    public List<GitFileStatus> ChangedFiles { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool Truncated { get; set; }
    public required string SourceInstanceId { get; set; }
    public string? SourceDisplayName { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsStale { get; set; }
    public int FreshnessSeconds { get; set; }
    public string FreshnessStatus => IsStale ? "stale" : "fresh";
}

public sealed class DesktopGitSnapshotListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? SourceInstanceId { get; set; }
    public string? RootPath { get; set; }
    public DesktopSnapshotState? State { get; set; }
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);
    public int Limit { get; set; } = 50;
}

public sealed class DesktopGitSnapshotLatestResult
{
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? RootPath { get; set; }
    public string? SourceInstanceId { get; set; }
    public required DesktopSnapshotState State { get; set; }
    public required bool IsStale { get; set; }
    public required string FreshnessStatus { get; set; }
    public DesktopGitSnapshot? Snapshot { get; set; }
}

public sealed class DesktopDiffSnapshotLatestResult
{
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? RootPath { get; set; }
    public string? Path { get; set; }
    public string? SourceInstanceId { get; set; }
    public required DesktopSnapshotState State { get; set; }
    public required bool IsStale { get; set; }
    public required string FreshnessStatus { get; set; }
    public DesktopDiffSnapshot? Snapshot { get; set; }
}

public sealed class DesktopDiffSnapshot
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public required string RootPath { get; set; }
    public string? Path { get; set; }
    public string? BaseRef { get; set; }
    public string? HeadRef { get; set; }
    public int MaxBytes { get; set; }
    public bool Staged { get; set; }
    public string Diff { get; set; } = string.Empty;
    public bool Truncated { get; set; }
    public bool Binary { get; set; }
    public List<string> Warnings { get; set; } = [];
    public required string SourceInstanceId { get; set; }
    public string? SourceDisplayName { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsStale { get; set; }
    public int FreshnessSeconds { get; set; }
}

public sealed class DesktopSessionSnapshot
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public required string SessionId { get; set; }
    public string? ParentSessionId { get; set; }
    public string? AgentIdentity { get; set; }
    public string? Role { get; set; }
    public string? CurrentCommand { get; set; }
    public string? CurrentPhase { get; set; }

    // First-class OperatorSession snapshot fields (task #1009)
    // project_id/task_id/workspace_id are launch/attach context by default.
    // They should be updated only when the backend logical context changes
    // (e.g., a new task branch or workspace is detected), not on every poll.
    public string? Title { get; set; }
    public string? DisplayName { get; set; }
    public string? Cwd { get; set; }
    public string? Kind { get; set; }
    public string? Backend { get; set; }
    /// <summary>Normalized status: starting, running, idle, exited, failed, detached, stale, source_offline, crashed.</summary>
    public string? Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? ExitedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? SourceDisplayName { get; set; }

    // Structured capabilities replacing ad-hoc control_capabilities over time.
    // These are observational display data only; no local-control authority is inferred
    // from Den snapshots.
    //
    // Legacy Pi builder capability key mapping:
    //   can_stream_raw_terminal -> can_stream_terminal
    //   can_stop               -> can_terminate
    //   can_send_input         -> can_send_input
    //   can_launch_managed_session -> can_deliver_compiled_response
    //   can_focus              -> can_focus
    public JsonElement? Capabilities { get; set; }

    // Legacy fields preserved for backward compatibility
    public JsonElement? RecentActivity { get; set; }
    public JsonElement? ChildSessions { get; set; }
    public JsonElement? ControlCapabilities { get; set; }
    public List<string> Warnings { get; set; } = [];
    public required string SourceInstanceId { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsStale { get; set; }
    public int FreshnessSeconds { get; set; }

    // Session ID namespace notes:
    // Legacy Pi artifact session IDs (pi_session_id or pi-run-{run_id}) remain
    // canonical for observer-only Pi artifact sessions.  New managed sessions
    // should use prefixed ids: pty:, tmux:, process:, pi-artifact:, pi-session:
    // external:.  The UNIQUE(project_id, source_instance_id, session_id)
    // constraint prevents duplicates across the namespace.
}

public sealed class DesktopSessionSnapshotListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? SourceInstanceId { get; set; }
    public string? SessionId { get; set; }
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);
    public int Limit { get; set; } = 50;
}

/// <summary>
/// Append-only session lifecycle/control event types.
/// </summary>
public enum SessionEventType
{
    Created,
    Discovered,
    StatusChanged,
    CapabilitiesChanged,
    Attached,
    Detached,
    InputSent,
    ResizeRequested,
    TerminateRequested,
    TerminateCompleted,
    /// <summary>Legacy v1 catch-all reconnect event retained for compatibility.</summary>
    Reconnect,
    ReconnectRequested,
    Reconnected,
    LeaseAcquired,
    LeaseLost,
    LeaseConflict,
    Warning,
    Crashed,
    Exited,
    SnapshotPublished,
    SnapshotPublishFailed
}

/// <summary>
/// An append-only record of a session lifecycle or control event.
/// Does not store raw terminal byte streams or high-frequency heartbeats.
/// Payload is bounded to 10 KB and excludes raw terminal output/input text.
/// </summary>
public sealed class DesktopSessionEvent
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public required string SourceInstanceId { get; set; }
    public required string SessionId { get; set; }
    /// <summary>Event kind, e.g. created, status_changed, reconnect_requested, reconnected, crashed. Legacy reconnect remains accepted.</summary>
    public required string EventType { get; set; }
    /// <summary>Bounded JSON payload. Max 10 KB. Must not contain raw terminal streams or raw input text.</summary>
    public string? Payload { get; set; }
    /// <summary>Agent or entity that requested/provoked this event (for audit).</summary>
    public string? RequestedBy { get; set; }
    /// <summary>Optional free-text reason for the event.</summary>
    public string? Reason { get; set; }
    /// <summary>When the event was observed by the source.</summary>
    public DateTime ObservedAt { get; set; }
    /// <summary>When the event was received/stored by the server (append-only, auto-set).</summary>
    public DateTime CreatedAt { get; set; }
}

public sealed class DesktopSessionEventListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? SourceInstanceId { get; set; }
    public string? SessionId { get; set; }
    /// <summary>Comma-separated event type filter.</summary>
    public string? EventTypes { get; set; }
    public int Limit { get; set; } = 50;
}
