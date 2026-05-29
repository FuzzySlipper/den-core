namespace DenMcp.Core.Models;

/// <summary>
/// Core worker pool member record. Tracks an agent that can accept work assignments.
/// Core owns this model; Gateway/Channels/Hermes Bridge consume it via APIs.
/// </summary>
public sealed class WorkerPoolMember
{
    /// <summary>
    /// The spawned-Hermes worker/agent identity. Unique across the pool.
    /// </summary>
    public required string WorkerIdentity { get; set; }

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
    /// Arbitrary JSON metadata (e.g. provider, model, tools).
    /// </summary>
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An assignment lease — a bounded work contract between a pool member and a task/role.
/// Core owns lease lifecycle; Gateway/Channels query and respond via checkpoint exchange.
/// </summary>
public sealed class WorkerAssignment
{
    public int Id { get; set; }

    /// <summary>
    /// The pool member performing the work.
    /// </summary>
    public required string WorkerIdentity { get; set; }

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
