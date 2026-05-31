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
    public string ProfileIdentity { get; set; } = string.Empty;

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
    /// Gateway adapter instance id for direct-message routing to this worker's
    /// concrete Hermes session. Populated by Gateway on binding establishment.
    /// Distinct from <see cref="AgentInstanceId"/> (the agent binding id);
    /// adapter_instance_id identifies the child session's Gateway adapter instance.
    /// </summary>
    public string? AdapterInstanceId { get; set; }

    /// <summary>
    /// Path or URI pointer to the worker's log stream for drill-down evidence.
    /// </summary>
    public string? LogPointer { get; set; }

    /// <summary>
    /// Seconds after last_heartbeat before the member is considered stale.
    /// When exceeded, capacity-aware queries compute the worker as unavailable
    /// and stale-release operations can reclaim their non-terminal assignments.
    /// Null means no staleness timeout (legacy default).
    /// </summary>
    public int? StaleAfterSeconds { get; set; }

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

    /// <summary>
    /// Unique lease identity for this assignment's capacity slot. Race-safe:
    /// populated from a per-worker sequential counter at insert time and
    /// guarded by a UNIQUE constraint in the assignments table.
    /// Format: "{worker_identity}:{run_id}:{seq}".
    /// </summary>
    public string? LeaseId { get; set; }

    /// <summary>
    /// Shared profile identity (e.g. "spawned-coder") denormalized from the
    /// pool member at lease time for capacity query efficiency.
    /// </summary>
    public string ProfileIdentity { get; set; } = string.Empty;

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

    /// <summary>
    /// JSON array of <see cref="ProfileCapacitySummary"/> — per-profile capacity
    /// breakdown for capacity-aware pool management. Null for backward compatibility.
    /// </summary>
    public string? PerProfileBreakdown { get; set; }
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

    // Pool lane statuses
    public const string LaneActive = "active";
    public const string LaneQuarantined = "quarantined";
    public const string LaneDisabled = "disabled";

    public static readonly string[] ValidLaneStatuses = [LaneActive, LaneQuarantined, LaneDisabled];

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

    // No-capacity reason codes
    public const string NoCapacityNoMatchingWorker = "no_matching_worker";
    public const string NoCapacityAllBusy = "all_busy";
    public const string NoCapacityAllQuarantinedOrOffline = "all_quarantined_or_offline";
    public const string NoCapacityAmbiguous = "ambiguous";
    public const string NoCapacityPreferredNotFoundOrBusy = "preferred_not_found_or_busy";

    public static readonly string[] ValidNoCapacityReasonCodes = [
        NoCapacityNoMatchingWorker,
        NoCapacityAllBusy,
        NoCapacityAllQuarantinedOrOffline,
        NoCapacityAmbiguous,
        NoCapacityPreferredNotFoundOrBusy,
    ];

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
    public string ProfileIdentity { get; set; } = string.Empty;
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
    public string ProfileIdentity { get; set; } = string.Empty;
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

/// <summary>
/// Typed result from a lease attempt. Either a successful <see cref="WorkerAssignment"/>
/// or a typed no-capacity diagnostic. Core-owned; downstream consumers inspect
/// <see cref="IsSuccess"/> to determine the outcome.
/// </summary>
public sealed class LeaseWorkerResult
{
    /// <summary>
    /// True when a worker was successfully leased.
    /// </summary>
    public required bool IsSuccess { get; set; }

    /// <summary>
    /// The assignment record when <see cref="IsSuccess"/> is true.
    /// </summary>
    public WorkerAssignment? Assignment { get; set; }

    /// <summary>
    /// The no-capacity diagnostic when <see cref="IsSuccess"/> is false.
    /// </summary>
    public WorkerNoCapacityRequest? NoCapacity { get; set; }

    /// <summary>
    /// Optional per-profile capacity summary for the lane associated with this
    /// lease attempt. Provides context like "spawned-coder: 2/4 busy" after the
    /// lease succeeds or when it fails due to capacity exhaustion.
    /// </summary>
    public ProfileCapacitySummary? Capacity { get; set; }
}

/// <summary>
/// Typed diagnostic for a failed lease attempt — the core's record of why a
/// worker-pool assignment request could not be fulfilled. Persisted to the
/// <c>worker_no_capacity_requests</c> table for readback.
///
/// Distinguishes at least:
/// - <see cref="WorkerPoolStates.NoCapacityNoMatchingWorker"/>: no worker matches role/profile/capability.
/// - <see cref="WorkerPoolStates.NoCapacityAllBusy"/>: matches exist but all are busy.
/// - <see cref="WorkerPoolStates.NoCapacityAllQuarantinedOrOffline"/>: matches exist but all are quarantined/offline.
/// - <see cref="WorkerPoolStates.NoCapacityAmbiguous"/>: ambiguous or misconfigured candidates.
/// - <see cref="WorkerPoolStates.NoCapacityPreferredNotFoundOrBusy"/>: preferred worker not found or not available.
/// </summary>
public sealed class WorkerNoCapacityRequest
{
    public int Id { get; set; }

    /// <summary>Project id from the original lease request.</summary>
    public required string ProjectId { get; set; }

    /// <summary>Optional task id from the original lease request.</summary>
    public int? TaskId { get; set; }

    /// <summary>Role requested (e.g. "coder", "reviewer").</summary>
    public required string Role { get; set; }

    /// <summary>Entity that requested the lease.</summary>
    public required string AssignedBy { get; set; }

    /// <summary>Worker run id for this request.</summary>
    public required string RunId { get; set; }

    /// <summary>Optional profile identity filter from the request.</summary>
    public string ProfileIdentity { get; set; } = string.Empty;

    /// <summary>Optional worker role filter from the request.</summary>
    public string? WorkerRole { get; set; }

    /// <summary>Required capabilities filter as JSON array string.</summary>
    public string? RequiredCapabilities { get; set; }

    /// <summary>Preferred worker identity if specified.</summary>
    public string? PreferredWorkerIdentity { get; set; }

    /// <summary>
    /// Typed reason code from <see cref="WorkerPoolStates.ValidNoCapacityReasonCodes"/>.
    /// </summary>
    public required string ReasonCode { get; set; }

    /// <summary>
    /// JSON object with candidate statistics: total, available, busy, quarantined, offboarded counts.
    /// e.g. {"total":3,"available":0,"busy":2,"quarantined":1,"offboarded":0}
    /// </summary>
    public string CandidateDetails { get; set; } = "{}";

    /// <summary>Human-readable diagnostic message.</summary>
    public string? DiagnosticMessage { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Options for listing no-capacity request records.
/// </summary>
public sealed class NoCapacityRequestListOptions
{
    public string? ProjectId { get; set; }
    public string? RunId { get; set; }
    public string? ReasonCode { get; set; }
    public int Limit { get; set; } = 50;
}

/// <summary>
/// Statistics about candidate workers during a no-capacity diagnostic.
/// </summary>
public sealed class WorkerCandidateStats
{
    public int Total { get; set; }
    public int Available { get; set; }
    public int Busy { get; set; }
    public int Quarantined { get; set; }
    public int Offboarded { get; set; }

    public string ToJson() =>
        System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
}

/// <summary>
/// A pool lane definition — binds a profile identity to a worker role with a
/// concurrency capacity. Multiple members share one lane's profile config;
/// the lane governs how many concurrent active assignments those members can
/// collectively hold. Quarantine targets the lane to block new leases without
/// disrupting already-running assignments.
/// </summary>
public sealed class WorkerPoolLane
{
    /// <summary>Shared profile identity (e.g. "spawned-coder").</summary>
    public required string ProfileIdentity { get; set; }

    /// <summary>Worker role for this lane (e.g. "coder", "reviewer").</summary>
    public required string WorkerRole { get; set; }

    /// <summary>
    /// Maximum concurrent active (non-terminal) assignments permitted for this
    /// profile+role combination. Default 4.
    /// </summary>
    public int Capacity { get; set; } = 4;

    /// <summary>
    /// Lane status: active, quarantined, or disabled.
    /// Quarantined lanes block new leases; existing assignments continue to run.
    /// Disabled lanes prevent all operations.
    /// </summary>
    public required string Status { get; set; }

    /// <summary>Arbitrary JSON metadata.</summary>
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Per-profile capacity summary. Answers "spawned-coder: 2/4 busy" queries.
/// Aggregates across one or more lanes sharing the same profile_identity.
/// </summary>
public sealed class ProfileCapacitySummary
{
    /// <summary>The shared profile identity (e.g. "spawned-coder").</summary>
    public required string ProfileIdentity { get; set; }

    /// <summary>Sum of capacity across all active lanes for this profile.</summary>
    public int TotalCapacity { get; set; }

    /// <summary>Number of active non-terminal assignments across all lanes.</summary>
    public int ActiveLeases { get; set; }

    /// <summary>Unused capacity (TotalCapacity - ActiveLeases).</summary>
    public int AvailableSlots { get; set; }

    /// <summary>Per-lane breakdown of capacity and usage.</summary>
    public List<LaneCapacitySummary> Lanes { get; set; } = [];
}

/// <summary>
/// Per-lane capacity breakdown within a profile.
/// </summary>
public sealed class LaneCapacitySummary
{
    /// <summary>Shared profile identity.</summary>
    public required string ProfileIdentity { get; set; }

    /// <summary>Worker role for this lane.</summary>
    public required string WorkerRole { get; set; }

    /// <summary>Maximum concurrent assignments for this lane.</summary>
    public int Capacity { get; set; }

    /// <summary>Currently active non-terminal assignments in this lane.</summary>
    public int BusyCount { get; set; }

    /// <summary>Unused capacity (Capacity - BusyCount).</summary>
    public int AvailableCount { get; set; }

    /// <summary>Count of members in quarantined status for this lane.</summary>
    public int QuarantinedCount { get; set; }
}
