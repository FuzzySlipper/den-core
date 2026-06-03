using System.Text.Json;

namespace DenCore.Models;

/// <summary>
/// Core-owned Direct Delivery contract models and projections.
///
/// These models use generic Den terminology (concrete agent instance, adapter binding,
/// pool member, assignment, lease, worker run, source context, target work, session owner/lane)
/// as defined in den-core/agent-runtime-terminology-glossary.
///
/// No Hermes/Pi/Codex internals appear in this contract. Gateway, Channels, and local
/// runtime adapters consume these projections through Core APIs. The local adapter/Bridge
/// owns harness/local process/session mechanics; Core owns canonical workflow truth and
/// binding records/projections.
///
/// Relationship to #1885 (session-owner slice):
///   The session_owner_id, session_id, channel_id, and lane fields on
///   <see cref="DirectDeliveryBindingSnapshot"/> are the session-owner portion
///   of this broader direct-delivery contract. Task #1885 owns the session-owner
///   lifecycle tracking; #1901 wraps it into the full adapter-binding projection
///   without expanding into Gateway/Hermes internals.
/// </summary>

// ── Readiness ────────────────────────────────────────────────────────────

public sealed class DirectDeliveryReadinessResponse
{
    public required string Status { get; set; }
    public required string Service { get; set; }
    public DateTime CheckedAt { get; set; }
    public required Dictionary<string, DirectDeliveryReadinessCheck> Checks { get; set; }
}

public sealed class DirectDeliveryReadinessCheck
{
    public required string Status { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

// ── Binding Projection (generic Core adapter-binding snapshot) ───────────

/// <summary>
/// Paginated list of direct-delivery adapter binding projections.
/// </summary>
public sealed class DirectDeliveryBindingSnapshotPage
{
    public DateTime GeneratedAt { get; set; }
    public required List<DirectDeliveryBindingSnapshot> Items { get; set; }
}

/// <summary>
/// Core-constructed projection of an adapter binding suitable for direct delivery.
///
/// This snapshot enriches the raw <see cref="AgentInstanceBinding"/> with:
///   - Pool member identity and shared-profile disambiguation
///     (concrete <see cref="PoolMemberId"/> vs shared <see cref="ProfileIdentity"/>)
///   - Active assignment/run linkage (<see cref="AssignmentId"/>, <see cref="WorkerRunId"/>)
///   - Session-owner / lane correlation (<see cref="SessionOwnerId"/>, <see cref="Lane"/>)
///
/// Separation from <see cref="GatewayBindingSnapshot"/> (/api/gateway/bindings):
///   <see cref="GatewayBindingSnapshot"/> projects the raw AgentInstanceBinding row
///   for Gateway contract consumers. This snapshot projects a Core-enriched view
///   for direct-delivery adapters, joining pool member and assignment data where
///   available. Gateway-bound consumers who only need raw binding fields should
///   continue using /api/gateway/bindings, which remains a stable compatibility alias.
/// </summary>
public sealed class DirectDeliveryBindingSnapshot
{
    // ── Adapter binding identity & freshness ─────────────────────────

    /// <summary>The concrete agent instance binding id (primary identity).</summary>
    public required string AgentInstanceId { get; set; }

    /// <summary>Project id this binding is scoped to.</summary>
    public required string ProjectId { get; set; }

    /// <summary>Agent family identifier (e.g. "hermes", "codex", "claude").</summary>
    public required string AgentFamily { get; set; }

    /// <summary>Agent identity / display handle.</summary>
    public required string AgentIdentity { get; set; }

    /// <summary>Role this binding advertises (e.g. "coder", "reviewer", "router").</summary>
    public string? Role { get; set; }

    /// <summary>Transport kind (e.g. "local_adapter", "discord", "manual_mcp").</summary>
    public required string TransportKind { get; set; }

    /// <summary>Binding status: active, degraded, inactive.</summary>
    public required string Status { get; set; }

    /// <summary>When the binding was established / last checked in.</summary>
    public DateTime CheckedInAt { get; set; }

    /// <summary>When the binding last sent a heartbeat.</summary>
    public DateTime LastHeartbeat { get; set; }

    // ── Session owner / lane (task #1885 slice) ───────────────────────

    /// <summary>
    /// Session owner identity — the concrete agent instance or orchestrator
    /// that owns this session. Populated from the pool member's AgentInstanceId
    /// or from a linked assignment's AgentInstanceId. May be the same as
    /// <see cref="AgentInstanceId"/> when this binding is the session owner.
    /// </summary>
    public string? SessionOwnerId { get; set; }

    /// <summary>Hermes/worker session id for correlation with active sessions.</summary>
    public string? SessionId { get; set; }

    /// <summary>Optional Den channel id for correlation with channel membership.</summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Lane identifier derived from profile_identity + worker_role.
    /// e.g. "spawned-coder/coder", "spawned-reviewer/reviewer".
    /// Null when no pool member is linked.
    /// </summary>
    public string? Lane { get; set; }

    // ── Pool member disambiguation (shared-profile identity) ──────────

    /// <summary>
    /// Concrete pool member identity (the canonical lifecycle key).
    /// Distinct from <see cref="ProfileIdentity"/> — two pool members can
    /// share the same profile_identity but have distinct pool_member_id values.
    /// </summary>
    public string? PoolMemberId { get; set; }

    /// <summary>
    /// Shared profile identity (e.g. "spawned-coder", "spawned-reviewer").
    /// Multiple pool members can share the same profile identity.
    /// </summary>
    public string? ProfileIdentity { get; set; }

    /// <summary>
    /// Worker role category (e.g. "coder", "reviewer", "validator").
    /// Separate from binding <see cref="Role"/> which is the advertised role.
    /// </summary>
    public string? WorkerRole { get; set; }

    /// <summary>
    /// Gateway adapter instance id for direct-message routing to this worker's
    /// concrete session. Provided when the pool member has an adapter binding.
    /// </summary>
    public string? AdapterInstanceId { get; set; }

    // ── Active assignment / run linkage ───────────────────────────────

    /// <summary>
    /// The active (non-terminal) assignment id currently linked to this binding,
    /// if any. Null when no active assignment is in progress.
    /// </summary>
    public int? AssignmentId { get; set; }

    /// <summary>
    /// The worker run id tracking execution (e.g. spawned-Hermes run_id).
    /// Populated from the active assignment's run_id.
    /// </summary>
    public string? WorkerRunId { get; set; }

    /// <summary>
    /// The assignment's current lifecycle state (ack, running, checkpoint_waiting,
    /// blocked, completed, failed, expired).
    /// </summary>
    public string? AssignmentState { get; set; }

    /// <summary>
    /// The role this agent is handling in the active assignment
    /// (e.g. "coder", "reviewer").
    /// </summary>
    public string? AssignmentRole { get; set; }

    /// <summary>
    /// The task id associated with the active assignment, if task-scoped.
    /// </summary>
    public int? TaskId { get; set; }

    // ── Outbox / readback semantics ───────────────────────────────────

    /// <summary>
    /// Outbox cursor for delivery-readback polling. Local adapters can use this
    /// to track which delivery events they have already processed.
    /// </summary>
    public string? OutboxCursor { get; set; }

    /// <summary>
    /// When this binding record was last updated in Core.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    // ── Capability / metadata ────────────────────────────────────────

    /// <summary>
    /// JSON array of capability strings this worker advertises (from pool member).
    /// </summary>
    public string? Capabilities { get; set; }

    /// <summary>Arbitrary JSON metadata from the binding and/or pool member.</summary>
    public JsonElement? Metadata { get; set; }
}

// ── Direct Delivery Envelope (message contract for adapter wake/delivery) ─

/// <summary>
/// Core-owned direct delivery envelope — the message contract for direct
/// agent wake/delivery without making Gateway the conceptual owner.
///
/// Local adapters/Bridge consume this envelope to route work to the correct
/// concrete agent instance/session. Channels owns visible ops/direct-agent
/// request recording; the adapter owns harness/local process/session mechanics.
///
/// Completion/block/failure is linked to the worker checkpoint/completion
/// packet via <see cref="AssignmentId"/> and <see cref="WorkerRunId"/>.
/// </summary>
public sealed class DirectDeliveryEnvelope
{
    // ── Source context ────────────────────────────────────────────────

    /// <summary>Who initiated the delivery (e.g. "den-mcp-runner", a channel id).</summary>
    public required string SourceContext { get; set; }

    /// <summary>Optional Den channel id where the delivery originated.</summary>
    public string? SourceChannelId { get; set; }

    // ── Target work ───────────────────────────────────────────────────

    /// <summary>Project id for the target work.</summary>
    public required string ProjectId { get; set; }

    /// <summary>Optional task id for the target work.</summary>
    public int? TaskId { get; set; }

    /// <summary>Role for the target work (e.g. "coder", "reviewer").</summary>
    public required string TargetRole { get; set; }

    /// <summary>Human-readable description of the target work.</summary>
    public string? TargetWorkDescription { get; set; }

    // ── Assignment / lease identity ───────────────────────────────────

    /// <summary>The Core assignment id for this delivery.</summary>
    public int? AssignmentId { get; set; }

    /// <summary>The worker run id tracking execution.</summary>
    public required string WorkerRunId { get; set; }

    /// <summary>Concrete pool member identity performing the work.</summary>
    public required string PoolMemberId { get; set; }

    /// <summary>Shared profile identity for the worker pool lane.</summary>
    public string? ProfileIdentity { get; set; }

    /// <summary>Worker role for the target worker.</summary>
    public string? WorkerRole { get; set; }

    // ── Concrete agent instance / session owner ───────────────────────

    /// <summary>Concrete agent instance binding id for the recipient.</summary>
    public required string AgentInstanceId { get; set; }

    /// <summary>Session owner identity (who owns the session).</summary>
    public string? SessionOwnerId { get; set; }

    /// <summary>Hermes/worker session id.</summary>
    public string? SessionId { get; set; }

    /// <summary>Adapter instance id for direct-message routing.</summary>
    public string? AdapterInstanceId { get; set; }

    // ── Completion / block / failure linkage ──────────────────────────

    /// <summary>
    /// Completion status for this delivery. Linked to the worker checkpoint
    /// / completion packet via assignment_id + worker_run_id.
    /// Values: pending, running, completed, failed, blocked.
    /// </summary>
    public string DeliveryStatus { get; set; } = "pending";

    /// <summary>
    /// The latest checkpoint id for this delivery's assignment, if any.
    /// </summary>
    public int? LatestCheckpointId { get; set; }

    /// <summary>
    /// Completion artifact path or URI, when delivery_status is completed/failed.
    /// </summary>
    public string? CompletionArtifact { get; set; }

    /// <summary>
    /// Human-readable completion summary, when delivery_status is completed/failed.
    /// </summary>
    public string? CompletionSummary { get; set; }

    /// <summary>
    /// Outbox cursor for this delivery event. Adapters use this for readback
    /// to advance their delivery cursor.
    /// </summary>
    public string? OutboxCursor { get; set; }

    // ── Timestamps ────────────────────────────────────────────────────

    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
