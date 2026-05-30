namespace DenMcp.Core.Models;

/// <summary>
/// Core worker pool member record. Tracks an agent that can accept work assignments.
/// Core owns this model; Gateway/Channels/Hermes Bridge consume it via APIs.
///
/// IDENTITY CONTRACT (v2 — shared profile with concrete members):
/// - <see cref="WorkerIdentity"/>: Primary key / concrete member lifecycle identity.
///   This is the canonical unique identifier for this specific pool member instance.
///   All lifecycle mutations (lease, release, quarantine, status change) key on this.
/// - <see cref="PoolMemberId"/>: Alias for WorkerIdentity. Exists for explicit naming
///   in downstream consumers. When not supplied on upsert, defaults to WorkerIdentity.
/// - <see cref="ProfileIdentity"/>: Shared role/profile identity (e.g. "spawned-coder").
///   Multiple pool members can share the same profile identity; each has a distinct
///   <see cref="WorkerIdentity"/>. Core uses profile_identity for pool-wide filtering
///   and routing; lifecycle mutations use the concrete <see cref="WorkerIdentity"/>.
/// - <see cref="WorkerRole"/>: Role category (e.g. "coder", "reviewer", "validator").
/// - <see cref="AgentInstanceId"/>: Concrete Gateway/Core agent instance binding id
///   when this member is bound to a running agent instance.
/// - <see cref="ChannelId"/> / <see cref="SessionId"/>: Optional correlation fields
///   for downstream channel/session routing.
///
/// Downstream consumers (#1769 Channels, #1770 Gateway, #1767 Bridge):
///   Read WorkerIdentity + ProfileIdentity + WorkerRole for disambiguation.
///   Use WorkerIdentity (or PoolMemberId) for lifecycle operations.
///   Never use ProfileIdentity alone for mutation — it is a shared/group identity.
/// </summary>
public sealed class WorkerPoolMember
{
    /// <summary>
    /// The spawned-Hermes worker/agent identity (concrete member id).
    /// Primary key and canonical lifecycle identity. Unique across the pool.
    /// All lifecycle APIs use this as the primary lookup key.
    /// </summary>
    public required string WorkerIdentity { get; set; }

    /// <summary>
    /// Explicit pool member id alias. Defaults to <see cref="WorkerIdentity"/> when
    /// not supplied. Provided for downstream consumers that prefer "pool_member_id"
    /// naming for the concrete lifecycle identity.
    /// </summary>
    public string? PoolMemberId { get; set; }

    /// <summary>
    /// The shared role/profile identity (e.g. "spawned-coder", "spawned-reviewer").
    /// Multiple pool members can share the same profile identity; each has a distinct
    /// <see cref="WorkerIdentity"/>. Core uses profile_identity for pool-wide filtering
    /// and routing; lifecycle mutations use the concrete <see cref="WorkerIdentity"/>.
    /// </summary>
    public string? ProfileIdentity { get; set; }

    /// <summary>
    /// Worker role category: e.g. "coder", "reviewer", "validator", "drift_checker", "packet_auditor".
    /// This is the member's role classification, separate from assignment role.
    /// </summary>
    public string? WorkerRole { get; set; }

    /// <summary>
    /// Optional human-readable display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// JSON array of capability strings this worker advertises.
    /// </summary>
    public string? Capabilities { get; set; }

    /// <summary>
    /// Pool status: available, busy, quarantined, offboarded.
    /// </summary>
    public required string Status { get; set; }

    /// <summary>
    /// Last heartbeat or activity timestamp.
    /// </summary>
    public string? LastHeartbeat { get; set; }

    /// <summary>
    /// Concrete Gateway/Core agent instance binding id when this member is
    /// bound to a running agent instance. Populated by Gateway on check-in.
    /// </summary>
    public string? AgentInstanceId { get; set; }

    /// <summary>
    /// Optional Den channel id for correlation with channel membership.
    /// Populated by Channels when this member is associated with a channel.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Optional Hermes/worker session id for correlation with active sessions.
    /// Populated by Gateway or Hermes Bridge on session establishment.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Arbitrary JSON metadata (e.g. provider, model, tools).
    /// </summary>
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An assignment lease — a bounded work contract between a pool member and a task/role.
/// Core owns lease lifecycle; Gateway/Channels query and respond via checkpoint exchange.
///
/// IDENTITY CONTRACT (v2):
/// - <see cref="WorkerIdentity"/>: Concrete pool member identity (maps to
///   <see cref="WorkerPoolMember.WorkerIdentity"/>). Used for lifecycle mutations.
/// - <see cref="PoolMemberId"/>: Display/readback alias for WorkerIdentity.
/// - <see cref="ProfileIdentity"/> / <see cref="WorkerRole"/>: Denormalized from
///   the pool member for readback convenience. Not used for lifecycle keys.
/// - <see cref="AgentInstanceId"/>: Binding id when available.
/// - <see cref="RunId"/>: The worker run id tracking execution (e.g. spawned-Hermes run_id).
/// - <see cref="ChannelId"/>: Optional channel correlation id.
///
/// Downstream consumers: use assignment_id for direct lifecycle operations.
/// Use WorkerIdentity + ProfileIdentity for display/routing disambiguation.
/// </summary>
public sealed class WorkerAssignment
{
    public int Id { get; set; }

    /// <summary>
    /// The pool member performing the work (concrete identity).
    /// Maps to <see cref="WorkerPoolMember.WorkerIdentity"/>.
    /// </summary>
    public required string WorkerIdentity { get; set; }

    /// <summary>
    /// Pool member id alias for display/readback convenience.
    /// Denormalized from the pool member at lease time.
    /// </summary>
    public string? PoolMemberId { get; set; }

    /// <summary>
    /// Shared profile identity (e.g. "spawned-coder") denormalized from the pool member
    /// for readback convenience. Not a lifecycle key.
    /// </summary>
    public string? ProfileIdentity { get; set; }

    /// <summary>
    /// Worker role (e.g. "coder") denormalized from the pool member for readback convenience.
    /// </summary>
    public string? WorkerRole { get; set; }

    /// <summary>
    /// Agent instance binding id denormalized from the pool member for readback convenience.
    /// </summary>
    public string? AgentInstanceId { get; set; }

    /// <summary>
    /// Optional channel correlation id, denormalized from the pool member.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// The worker run id tracking execution (e.g. spawned-Hermes run_id).
    /// </summary>
    public required string RunId { get; set; }

    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public required string Role { get; set; }

    /// <summary>
    /// Entity that assigned/leased this worker.
    /// </summary>
    public required string AssignedBy { get; set; }

    /// <summary>
    /// Current lifecycle state.
    /// Non-terminal: ack, running, checkpoint_waiting, blocked
    /// Terminal: completed, failed, expired
    /// </summary>
    public required string State { get; set; }

    /// <summary>
    /// The latest checkpoint id for this assignment, if any.
    /// </summary>
    public int? LatestCheckpointId { get; set; }

    /// <summary>
    /// Cleanup evidence JSON (e.g. {"log_path":"/tmp/..."}).
    /// Required before release when state is terminal.
    /// </summary>
    public string? CleanupEvidence { get; set; }

    /// <summary>
    /// When cleanup evidence was recorded.
    /// </summary>
    public string? CleanupRecordedAt { get; set; }

    /// <summary>
    /// When the lease was acquired.
    /// </summary>
    public string? AcquiredAt { get; set; }

    /// <summary>
    /// When the lease was released (only after terminal + cleanup).
    /// </summary>
    public string? ReleasedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A checkpoint packet — an append-only progress/completion/failure report from a worker.
/// Checkpoints are the primary communication mechanism between worker and orchestrator.
/// </summary>
public sealed class WorkerCheckpoint
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    public required string RunId { get; set; }

    /// <summary>
    /// Type: checkpoint (periodic), progress, completion, failure, state_snapshot.
    /// </summary>
    public required string CheckpointType { get; set; }

    /// <summary>
    /// JSON payload with checkpoint data.
    /// </summary>
    public required string Payload { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A response to a checkpoint — orchestrator guidance, redirect, abort.
/// </summary>
public sealed class CheckpointResponse
{
    public int Id { get; set; }
    public int CheckpointId { get; set; }
    public int? AssignmentId { get; set; }
    public required string RunId { get; set; }

    /// <summary>
    /// Response type: ack, guidance, redirect, abort, checkpoint_request.
    /// </summary>
    public required string ResponseType { get; set; }

    /// <summary>
    /// JSON payload with response data.
    /// </summary>
    public required string Payload { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Lightweight summary projection for caretaker/Agents Overview reads.
/// Core-owned; Gateway/Channels may extend.
/// </summary>
public sealed class WorkerPoolSummary
{
    public int TotalMembers { get; set; }
    public int AvailableMembers { get; set; }
    public int BusyMembers { get; set; }
    public int QuarantinedMembers { get; set; }
    public int ActiveAssignments { get; set; }
    public int CompletedAssignments { get; set; }
    public int FailedAssignments { get; set; }
    public int ExpiredAssignments { get; set; }
    public int RecentCheckpoints { get; set; }
}

/// <summary>
/// Constants for worker pool state values.
/// </summary>
public static class WorkerPoolStates
{
    // Member statuses
    public const string MemberAvailable = "available";
    public const string MemberBusy = "busy";
    public const string MemberQuarantined = "quarantined";
    public const string MemberOffboarded = "offboarded";

    // Assignment non-terminal states
    public const string Ack = "ack";
    public const string Running = "running";
    public const string CheckpointWaiting = "checkpoint_waiting";
    public const string Blocked = "blocked";

    // Assignment terminal states
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";

    // Checkpoint types
    public const string CheckpointPeriodic = "checkpoint";
    public const string CheckpointProgress = "progress";
    public const string CheckpointCompletion = "completion";
    public const string CheckpointFailure = "failure";
    public const string CheckpointStateSnapshot = "state_snapshot";

    // Checkpoint response types
    public const string ResponseAck = "ack";
    public const string ResponseGuidance = "guidance";
    public const string ResponseRedirect = "redirect";
    public const string ResponseAbort = "abort";
    public const string ResponseCheckpointRequest = "checkpoint_request";

    public static readonly string[] NonTerminalStates = [Ack, Running, CheckpointWaiting, Blocked];
    public static readonly string[] TerminalStates = [Completed, Failed, Expired];
    public static readonly string[] ValidMemberStatuses = [MemberAvailable, MemberBusy, MemberQuarantined, MemberOffboarded];
    public static readonly string[] ValidAssignmentStates = [Ack, Running, CheckpointWaiting, Blocked, Completed, Failed, Expired];
    public static readonly string[] ValidCheckpointTypes = [CheckpointPeriodic, CheckpointProgress, CheckpointCompletion, CheckpointFailure, CheckpointStateSnapshot];
    public static readonly string[] ValidResponseTypes = [ResponseAck, ResponseGuidance, ResponseRedirect, ResponseAbort, ResponseCheckpointRequest];

    public static bool IsNonTerminal(string state) => Array.IndexOf(NonTerminalStates, state) >= 0;
    public static bool IsTerminal(string state) => Array.IndexOf(TerminalStates, state) >= 0;
}

/// <summary>
/// Options for listing pool members.
/// </summary>
public sealed class WorkerPoolMemberListOptions
{
    public string? Status { get; set; }
    public string? WorkerIdentity { get; set; }
    public string? ProfileIdentity { get; set; }
    public string? WorkerRole { get; set; }
    public int Limit { get; set; } = 50;
}

/// <summary>
/// Options for listing assignments.
/// </summary>
public sealed class WorkerAssignmentListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkerIdentity { get; set; }
    public string? State { get; set; }
    public string? Role { get; set; }
    public int Limit { get; set; } = 50;
}

/// <summary>
/// Options for listing checkpoints.
/// </summary>
public sealed class WorkerCheckpointListOptions
{
    public int? AssignmentId { get; set; }
    public string? RunId { get; set; }
    public string? CheckpointType { get; set; }
    public int Limit { get; set; } = 50;
}

/// <summary>
/// Input for leasing an available worker.
/// </summary>
public sealed record LeaseWorkerInput
{
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public required string Role { get; set; }
    public required string AssignedBy { get; set; }
    public required string RunId { get; set; }
    /// <summary>Optional capability filter — the worker must have ALL specified capabilities.</summary>
    public string[]? RequiredCapabilities { get; set; }
    /// <summary>Optional specific worker identity to lease.</summary>
    public string? PreferredWorkerIdentity { get; set; }
    /// <summary>Optional profile identity filter — only consider workers with matching profile.</summary>
    public string? ProfileIdentity { get; set; }
    /// <summary>Optional worker role filter — only consider workers with matching role.</summary>
    public string? WorkerRole { get; set; }
}

/// <summary>
/// Input for recording cleanup evidence.
/// </summary>
public sealed class RecordCleanupEvidenceInput
{
    public required int AssignmentId { get; set; }
    public required string EvidenceJson { get; set; }
}

/// <summary>
/// Input for quarantining a worker.
/// </summary>
public sealed class QuarantineWorkerInput
{
    public required string WorkerIdentity { get; set; }
    public required string QuarantinedBy { get; set; }
    public string? Reason { get; set; }
}
