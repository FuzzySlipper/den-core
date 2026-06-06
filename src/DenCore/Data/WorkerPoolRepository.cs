using System.Globalization;
using System.Text.Json;
using DenCore.Models;
using Microsoft.Data.Sqlite;

namespace DenCore.Data;

/// <summary>
/// Core repository for worker pool members, assignments, checkpoints, and responses.
/// Gateway/Channels/Hermes Bridge use these APIs via injected dependencies; they do
/// not access the schema directly.
/// </summary>
public interface IWorkerPoolRepository
{
    // ── Members ──────────────────────────────────────────────────────────
    Task<WorkerPoolMember> UpsertMemberAsync(WorkerPoolMember member);
    Task<WorkerPoolMember?> GetMemberAsync(string workerIdentity);
    Task<List<WorkerPoolMember>> ListMembersAsync(WorkerPoolMemberListOptions options);
    Task<int> SetMemberStatusAsync(string workerIdentity, string status, string? metadata = null);

    // ── Assignments ──────────────────────────────────────────────────────
    Task<WorkerAssignment?> LeaseAvailableWorkerAsync(LeaseWorkerInput input);
    Task<WorkerAssignment?> GetAssignmentAsync(int assignmentId);
    Task<WorkerAssignment?> GetAssignmentByRunIdAsync(string runId);
    Task<List<WorkerAssignment>> ListAssignmentsAsync(WorkerAssignmentListOptions options);
    Task<WorkerAssignment?> TransitionAssignmentStateAsync(int assignmentId, string newState, string? metadata = null);

    // ── Checkpoints ──────────────────────────────────────────────────────
    Task<WorkerCheckpoint> AppendCheckpointAsync(int assignmentId, string runId, string checkpointType, string payload);
    Task<List<WorkerCheckpoint>> ListCheckpointsAsync(WorkerCheckpointListOptions options);

    // ── Checkpoint Responses ─────────────────────────────────────────────
    Task<CheckpointResponse> AppendCheckpointResponseAsync(int checkpointId, int? assignmentId, string runId, string responseType, string payload);
    Task<List<CheckpointResponse>> ListResponsesAsync(int checkpointId);
    Task<List<CheckpointResponse>> ListResponsesByRunIdAsync(string runId, int limit = 50);

    // ── Release & Quarantine ─────────────────────────────────────────────
    Task<WorkerAssignment?> RecordCleanupEvidenceAsync(int assignmentId, string evidenceJson);
    Task<WorkerAssignment?> ReleaseAssignmentAsync(int assignmentId);
    Task<bool> QuarantineWorkerAsync(string workerIdentity, string quarantinedBy, string? reason = null);

    // ── Summary ──────────────────────────────────────────────────────────
    Task<WorkerPoolSummary> GetSummaryAsync();

    // ── No-Capacity Diagnostics ─────────────────────────────────────────
    /// <summary>
    /// Lease an available worker with typed diagnostics on failure.
    /// Returns a <see cref="LeaseWorkerResult"/> that distinguishes success
    /// from typed no-capacity reasons. The diagnostics are persisted to the
    /// <c>worker_no_capacity_requests</c> table for readback.
    /// </summary>
    Task<LeaseWorkerResult> LeaseWorkerWithDiagnosticsAsync(LeaseWorkerInput input);

    /// <summary>
    /// List no-capacity request records with optional filtering.
    /// </summary>
    Task<List<WorkerNoCapacityRequest>> ListNoCapacityRequestsAsync(NoCapacityRequestListOptions options);

    /// <summary>
    /// Get a single no-capacity request record by id.
    /// </summary>
    Task<WorkerNoCapacityRequest?> GetNoCapacityRequestAsync(int id);

    // ── Pool Lanes (shared-profile capacity) ─────────────────────────────
    /// <summary>
    /// Upsert a pool lane definition. ProfileIdentity + WorkerRole is the
    /// composite key. A lane defines the maximum concurrent active assignments
    /// for workers sharing this profile+role combination.
    /// </summary>
    Task<WorkerPoolLane> UpsertLaneAsync(WorkerPoolLane lane);

    /// <summary>
    /// Get a pool lane by its composite key.
    /// </summary>
    Task<WorkerPoolLane?> GetLaneAsync(string profileIdentity, string workerRole);

    /// <summary>
    /// List pool lanes, optionally filtered by status or profile identity.
    /// </summary>
    Task<List<WorkerPoolLane>> ListLanesAsync(string? profileIdentity = null, string? status = null, int limit = 50);

    /// <summary>
    /// Set lane status (active/quarantined/disabled). Quarantining a lane
    /// blocks new leases for that profile+role but preserves existing
    /// non-terminal assignments.
    /// </summary>
    Task<int> SetLaneStatusAsync(string profileIdentity, string workerRole, string status);

    /// <summary>
    /// Get a per-profile capacity summary: total capacity, active leases,
    /// available slots, and per-lane breakdown. Answers "spawned-coder: 2/4 busy".
    /// Active leases count only non-terminal assignments.
    /// </summary>
    Task<ProfileCapacitySummary> GetProfileCapacitySummaryAsync(string profileIdentity);

    // ── Stale Detection ───────────────────────────────────────────────────
    /// <summary>
    /// Release stale assignments: find members with stale_after_seconds
    /// exceeded vs last_heartbeat, whose status is busy, and release all
    /// their non-terminal assignments. Returns the count of released assignments.
    /// Computed stale — no persistent 'stale' status on members.
    /// </summary>
    Task<int> ReleaseStaleLeasesAsync();

    // ── Stale Worker Sweep ────────────────────────────────────────────────
    /// <summary>
    /// Sweep all Core records to detect stale/stalled worker workflow states.
    /// Returns a <see cref="StaleWorkerSweepResult"/> with classified conditions
    /// and deduped stale signatures. This is a read-only diagnostic projection;
    /// it does not mutate any state.
    ///
    /// Detects at least:
    /// - stale_ack: assignment in 'ack' with no completion packet past threshold
    /// - stale_running: assignment in 'running' with no checkpoint/completion past threshold
    /// - missing_reviewer_completion: review round with no verdict, reviewer assignment stuck
    /// - completion_not_terminalized: completion checkpoint exists but assignment not terminal
    /// - orphaned_orchestrator_lease: active orchestrator lease with no child progress
    /// - duplicate_assignment_for_run: multiple non-terminal assignments for same run
    /// </summary>
    Task<StaleWorkerSweepResult> SweepStaleWorkersAsync(StaleSweepOptions options);

    // ── Orchestrator Leases ───────────────────────────────────────────────

    /// <summary>
    /// Create a new project-duration orchestrator lease. Selects an available
    /// pool member matching the profile/capability filter and transitions it
    /// to 'busy'. Returns the created lease record on success.
    /// </summary>
    Task<OrchestratorLease> CreateOrchestratorLeaseAsync(CreateOrchestratorLeaseInput input);

    /// <summary>
    /// Get an orchestrator lease by its internal id.
    /// </summary>
    Task<OrchestratorLease?> GetOrchestratorLeaseAsync(int id);

    /// <summary>
    /// Get an orchestrator lease by its unique lease_id.
    /// </summary>
    Task<OrchestratorLease?> GetOrchestratorLeaseByLeaseIdAsync(string leaseId);

    /// <summary>
    /// List orchestrator leases with optional filters.
    /// </summary>
    Task<List<OrchestratorLease>> ListOrchestratorLeasesAsync(OrchestratorLeaseListOptions options);

    /// <summary>
    /// Transition an orchestrator lease to a new state. Validates state machine transitions.
    /// For terminal transitions, records cleanup evidence if provided and computes actual duration.
    /// </summary>
    Task<OrchestratorLease?> TransitionOrchestratorLeaseAsync(TransitionOrchestratorLeaseInput input);

    /// <summary>
    /// Record cleanup/release evidence for a terminal orchestrator lease.
    /// </summary>
    Task<OrchestratorLease?> RecordOrchestratorLeaseCleanupAsync(int leaseId, string evidenceJson);

    /// <summary>
    /// Expire or degrade stale orchestrator leases whose lease_expires_at has passed
    /// or whose pool member heartbeat is stale. Returns the count of expired/degraded leases.
    /// Does not mark the profile permanently busy — expired leases release the pool member.
    /// </summary>
    Task<int> ReconcileStaleOrchestratorLeasesAsync();

    /// <summary>
    /// Get the pool residency projection for a project — lists all active residencies
    /// (task-worker assignments, orchestrator leases, bindings) to distinguish
    /// between different membership/binding/lease kinds.
    /// </summary>
    Task<List<PoolResidencyProjection>> GetPoolResidencyProjectionAsync(string projectId);

}

public sealed class WorkerPoolRepository : IWorkerPoolRepository
{
    private readonly DbConnectionFactory _db;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Column list for SELECT queries on worker_pool_members.
    /// Must match the order expected by <see cref="ReadMember"/>.
    /// </summary>
    private const string MemberColumns =
        "worker_identity, profile_identity, worker_role, display_name, capabilities, " +
        "status, last_heartbeat, agent_instance_id, channel_id, session_id, " +
        "adapter_instance_id, log_pointer, stale_after_seconds, metadata, " +
        "created_at, updated_at";

    /// <summary>
    /// Column list for SELECT queries on worker_assignments (no alias prefix — for single-table use).
    /// Must match the order expected by <see cref="ReadAssignment"/>.
    /// </summary>
    private const string AssignmentColumns =
        "id, worker_identity, run_id, project_id, task_id, role, assigned_by, state, " +
        "lease_id, profile_identity, " +
        "latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at, " +
        "created_at, updated_at";

    /// <summary>
    /// Column list for JOIN queries on worker_assignments with alias 'wa'.
    /// Must match the order expected by <see cref="ReadAssignment"/> then followed by
    /// denormalized member fields (worker_role, agent_instance_id, channel_id).
    /// profile_identity is now a column on wa itself (denormalized at insert).
    /// </summary>
    private const string AssignmentColumnsPrefixed =
        "wa.id, wa.worker_identity, wa.run_id, wa.project_id, wa.task_id, wa.role, wa.assigned_by, wa.state, " +
        "wa.lease_id, wa.profile_identity, " +
        "wa.latest_checkpoint_id, wa.cleanup_evidence, wa.cleanup_recorded_at, wa.acquired_at, wa.released_at, " +
        "wa.created_at, wa.updated_at";

    public WorkerPoolRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    // ── Members ──────────────────────────────────────────────────────────

    public async Task<WorkerPoolMember> UpsertMemberAsync(WorkerPoolMember member)
    {
        var poolMemberId = member.PoolMemberId ?? member.WorkerIdentity;

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO worker_pool_members (worker_identity, profile_identity, worker_role, display_name, capabilities, status, last_heartbeat, agent_instance_id, channel_id, session_id, adapter_instance_id, log_pointer, stale_after_seconds, metadata)
            VALUES (@workerIdentity, @profileIdentity, @workerRole, @displayName, @capabilities, @status, @lastHeartbeat, @agentInstanceId, @channelId, @sessionId, @adapterInstanceId, @logPointer, @staleAfterSeconds, @metadata)
            ON CONFLICT(worker_identity) DO UPDATE SET
                profile_identity = excluded.profile_identity,
                worker_role = COALESCE(excluded.worker_role, worker_pool_members.worker_role),
                display_name = COALESCE(excluded.display_name, worker_pool_members.display_name),
                capabilities = COALESCE(excluded.capabilities, worker_pool_members.capabilities),
                status = excluded.status,
                last_heartbeat = excluded.last_heartbeat,
                agent_instance_id = COALESCE(excluded.agent_instance_id, worker_pool_members.agent_instance_id),
                channel_id = COALESCE(excluded.channel_id, worker_pool_members.channel_id),
                session_id = COALESCE(excluded.session_id, worker_pool_members.session_id),
                adapter_instance_id = COALESCE(excluded.adapter_instance_id, worker_pool_members.adapter_instance_id),
                log_pointer = COALESCE(excluded.log_pointer, worker_pool_members.log_pointer),
                stale_after_seconds = COALESCE(excluded.stale_after_seconds, worker_pool_members.stale_after_seconds),
                metadata = COALESCE(excluded.metadata, worker_pool_members.metadata),
                updated_at = datetime('now')
            RETURNING {MemberColumns}
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", member.WorkerIdentity);
        cmd.Parameters.AddWithValue("@profileIdentity", member.ProfileIdentity);
        cmd.Parameters.AddWithValue("@workerRole", (object?)member.WorkerRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@displayName", (object?)member.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@capabilities", (object?)member.Capabilities ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", member.Status);
        cmd.Parameters.AddWithValue("@lastHeartbeat", (object?)member.LastHeartbeat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@agentInstanceId", (object?)member.AgentInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channelId", (object?)member.ChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sessionId", (object?)member.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@adapterInstanceId", (object?)member.AdapterInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@logPointer", (object?)member.LogPointer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@staleAfterSeconds", (object?)member.StaleAfterSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)member.Metadata ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadMember(reader);
    }

    public async Task<WorkerPoolMember?> GetMemberAsync(string workerIdentity)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {MemberColumns}
            FROM worker_pool_members
            WHERE worker_identity = @workerIdentity
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", workerIdentity);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadMember(reader) : null;
    }

    public async Task<List<WorkerPoolMember>> ListMembersAsync(WorkerPoolMemberListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Status))
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", options.Status);
        }
        if (!string.IsNullOrWhiteSpace(options.WorkerIdentity))
        {
            where.Add("worker_identity = @workerIdentity");
            cmd.Parameters.AddWithValue("@workerIdentity", options.WorkerIdentity);
        }
        if (!string.IsNullOrWhiteSpace(options.ProfileIdentity))
        {
            where.Add("profile_identity = @profileIdentity");
            cmd.Parameters.AddWithValue("@profileIdentity", options.ProfileIdentity);
        }
        if (!string.IsNullOrWhiteSpace(options.WorkerRole))
        {
            where.Add("worker_role = @workerRole");
            cmd.Parameters.AddWithValue("@workerRole", options.WorkerRole);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT {MemberColumns}
            FROM worker_pool_members
            {whereClause}
            ORDER BY updated_at DESC, worker_identity
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<WorkerPoolMember>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadMember(reader));
        return results;
    }

    public async Task<int> SetMemberStatusAsync(string workerIdentity, string status, string? metadata = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var setClauses = new List<string> { "status = @status", "updated_at = datetime('now')" };
        if (metadata is not null)
        {
            setClauses.Add("metadata = @metadata");
            cmd.Parameters.AddWithValue("@metadata", metadata);
        }

        cmd.CommandText = $"""
            UPDATE worker_pool_members
            SET {string.Join(", ", setClauses)}
            WHERE worker_identity = @workerIdentity
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", workerIdentity);
        cmd.Parameters.AddWithValue("@status", status);

        return await cmd.ExecuteNonQueryAsync();
    }

    // ── Assignments ──────────────────────────────────────────────────────

    public async Task<WorkerAssignment?> LeaseAvailableWorkerAsync(LeaseWorkerInput input)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            WorkerAssignment? result;

            // If a preferred worker was specified, try to lease that specific one
            if (!string.IsNullOrWhiteSpace(input.PreferredWorkerIdentity))
            {
                result = await TryLeaseSpecificWorkerAsync(conn, input);
            }
            else
            {
                // Otherwise find an available worker matching capability requirements and optional profile/role
                var availableWorkers = await FindAvailableWorkersAsync(conn, input.RequiredCapabilities, input.ProfileIdentity, input.WorkerRole);
                result = null;
                foreach (var workerId in availableWorkers)
                {
                    var leased = await TryLeaseSpecificWorkerAsync(conn, input with { PreferredWorkerIdentity = workerId });
                    if (leased is not null)
                    {
                        result = leased;
                        break;
                    }
                }
            }

            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task<WorkerAssignment?> TryLeaseSpecificWorkerAsync(SqliteConnection conn, LeaseWorkerInput input)
    {
        // Check worker is available
        var worker = await GetMemberByConnAsync(conn, input.PreferredWorkerIdentity!);
        if (worker is null || worker.Status != WorkerPoolStates.MemberAvailable)
            return null;

        // Check no existing non-terminal assignment for this worker+run conflict
        if (await HasActiveAssignmentAsync(conn, input.PreferredWorkerIdentity!, input.RunId))
            return null;

        // If this member belongs to a configured shared-profile lane, enforce
        // lane status and capacity before consuming the concrete member. Legacy
        // members without a lane remain backward-compatible and lease as before.
        if (!await LaneCanAcceptLeaseAsync(conn, worker, input.Role))
            return null;

        // Update member to busy
        await SetMemberStatusByConnAsync(conn, input.PreferredWorkerIdentity!, WorkerPoolStates.MemberBusy);

        // Create assignment — include lease_id, profile_identity, and denormalized pool member fields
        var leaseId = $"{input.PreferredWorkerIdentity!}:{input.RunId}";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO worker_assignments (worker_identity, run_id, project_id, task_id, role, assigned_by,
                state, lease_id, profile_identity, acquired_at)
            VALUES (@workerIdentity, @runId, @projectId, @taskId, @role, @assignedBy,
                'ack', @leaseId, @profileIdentity, datetime('now'))
            RETURNING id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                      lease_id, profile_identity,
                      latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", input.PreferredWorkerIdentity!);
        cmd.Parameters.AddWithValue("@runId", input.RunId);
        cmd.Parameters.AddWithValue("@projectId", input.ProjectId);
        cmd.Parameters.AddWithValue("@taskId", (object?)input.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", input.Role);
        cmd.Parameters.AddWithValue("@assignedBy", input.AssignedBy);
        cmd.Parameters.AddWithValue("@leaseId", leaseId);
        cmd.Parameters.AddWithValue("@profileIdentity", worker.ProfileIdentity);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadAssignment(reader, worker);
    }

    public async Task<WorkerAssignment?> GetAssignmentAsync(int assignmentId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        return await GetAssignmentByIdAsync(conn, assignmentId);
    }

    public async Task<WorkerAssignment?> GetAssignmentByRunIdAsync(string runId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {AssignmentColumnsPrefixed}, wm.worker_role, wm.agent_instance_id, wm.channel_id
            FROM worker_assignments wa
            LEFT JOIN worker_pool_members wm ON wa.worker_identity = wm.worker_identity
            WHERE wa.run_id = @runId
            ORDER BY wa.id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@runId", runId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignmentWithJoin(reader) : null;
    }

    public async Task<List<WorkerAssignment>> ListAssignmentsAsync(WorkerAssignmentListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("wa.project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }
        if (options.TaskId is not null)
        {
            where.Add("wa.task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.WorkerIdentity))
        {
            where.Add("wa.worker_identity = @workerIdentity");
            cmd.Parameters.AddWithValue("@workerIdentity", options.WorkerIdentity);
        }
        if (!string.IsNullOrWhiteSpace(options.State))
        {
            where.Add("wa.state = @state");
            cmd.Parameters.AddWithValue("@state", options.State);
        }
        if (!string.IsNullOrWhiteSpace(options.Role))
        {
            where.Add("wa.role = @role");
            cmd.Parameters.AddWithValue("@role", options.Role);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT {AssignmentColumnsPrefixed}, wm.worker_role, wm.agent_instance_id, wm.channel_id
            FROM worker_assignments wa
            LEFT JOIN worker_pool_members wm ON wa.worker_identity = wm.worker_identity
            {whereClause}
            ORDER BY wa.updated_at DESC, wa.id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<WorkerAssignment>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadAssignmentWithJoin(reader));
        return results;
    }

    public async Task<WorkerAssignment?> TransitionAssignmentStateAsync(int assignmentId, string newState, string? metadata = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var assignment = await GetAssignmentByIdAsync(conn, assignmentId);
            if (assignment is null)
            {
                await tx.CommitAsync();
                return null;
            }

            // Validate transition
            if (!IsValidTransition(assignment.State, newState))
            {
                await tx.CommitAsync();
                return null;
            }

            var setClauses = new List<string>
            {
                "state = @newState",
                "updated_at = datetime('now')"
            };

            // If transitioning to terminal, record released_at
            if (WorkerPoolStates.IsTerminal(newState) && assignment.ReleasedAt is null)
            {
                setClauses.Add("released_at = datetime('now')");
            }

            // If transitioning out of checkpoint_waiting, clear the flag
            if (newState == WorkerPoolStates.Ack || newState == WorkerPoolStates.Running)
            {
                // No checkpoint_id change needed here
            }
            if (newState == WorkerPoolStates.Completed || newState == WorkerPoolStates.Failed)
            {
                // Terminal — set member back to available if not quarantined later
                await SetMemberStatusByConnAsync(conn, assignment.WorkerIdentity, WorkerPoolStates.MemberAvailable);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE worker_assignments
                SET {string.Join(", ", setClauses)}
                WHERE id = @id
                RETURNING {AssignmentColumns}
                """;
            cmd.Parameters.AddWithValue("@id", assignmentId);
            cmd.Parameters.AddWithValue("@newState", newState);

            await using var reader = await cmd.ExecuteReaderAsync();
            var updated = await reader.ReadAsync() ? ReadAssignment(reader) : null;
            await reader.CloseAsync();
            await tx.CommitAsync();
            return updated;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ── Checkpoints ──────────────────────────────────────────────────────

    public async Task<WorkerCheckpoint> AppendCheckpointAsync(int assignmentId, string runId, string checkpointType, string payload)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // Run ID mismatch guard: verify assignment exists and run_id matches.
            var assignment = await GetAssignmentByIdAsync(conn, assignmentId);
            if (assignment is null)
                throw new InvalidOperationException($"Assignment {assignmentId} not found");
            if (assignment.RunId != runId)
                throw new InvalidOperationException(
                    $"Run ID mismatch for assignment {assignmentId}: checkpoint claims '{runId}', assignment has '{assignment.RunId}'");

            var newState = StateForCheckpointType(checkpointType);
            if (WorkerPoolStates.IsTerminal(assignment.State))
            {
                // Terminal assignments may receive an idempotent terminal checkpoint repost
                // as audit evidence, but must never accept non-terminal checkpoints that
                // would move them back to checkpoint_waiting/running and re-route workers.
                if (!string.Equals(assignment.State, newState, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Assignment {assignmentId} is terminal ({assignment.State}); refusing checkpoint '{checkpointType}' for run '{runId}'");
                }
            }

            // Insert checkpoint
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO worker_checkpoints (assignment_id, run_id, checkpoint_type, payload)
                VALUES (@assignmentId, @runId, @checkpointType, @payload)
                RETURNING id, assignment_id, run_id, checkpoint_type, payload, created_at
                """;
            cmd.Parameters.AddWithValue("@assignmentId", assignmentId);
            cmd.Parameters.AddWithValue("@runId", runId);
            cmd.Parameters.AddWithValue("@checkpointType", checkpointType);
            cmd.Parameters.AddWithValue("@payload", payload);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var checkpoint = ReadCheckpoint(reader);
            await reader.CloseAsync();

            // Update assignment: link latest checkpoint and derive state. Idempotent
            // terminal reposts are persisted as audit rows but intentionally do not
            // replace latest_checkpoint_id or mutate the terminal assignment.
            if (WorkerPoolStates.IsTerminal(assignment.State))
            {
                await tx.CommitAsync();
                return checkpoint;
            }

            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = """
                UPDATE worker_assignments
                SET latest_checkpoint_id = @checkpointId,
                    state = @newState,
                    released_at = CASE WHEN @isTerminal = 1 AND released_at IS NULL THEN datetime('now') ELSE released_at END,
                    updated_at = datetime('now')
                WHERE id = @assignmentId
                RETURNING worker_identity
                """;
            updateCmd.Parameters.AddWithValue("@checkpointId", checkpoint.Id);
            updateCmd.Parameters.AddWithValue("@assignmentId", assignmentId);
            updateCmd.Parameters.AddWithValue("@newState", newState);
            updateCmd.Parameters.AddWithValue("@isTerminal", WorkerPoolStates.IsTerminal(newState) ? 1 : 0);

            var workerIdentity = (string?)await updateCmd.ExecuteScalarAsync();

            // If terminal via checkpoint, set member back to available
            if (WorkerPoolStates.IsTerminal(newState) && workerIdentity is not null)
            {
                await SetMemberStatusByConnAsync(conn, workerIdentity, WorkerPoolStates.MemberAvailable);
            }

            await tx.CommitAsync();
            return checkpoint;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<WorkerCheckpoint>> ListCheckpointsAsync(WorkerCheckpointListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (options.AssignmentId is not null)
        {
            where.Add("assignment_id = @assignmentId");
            cmd.Parameters.AddWithValue("@assignmentId", options.AssignmentId.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.RunId))
        {
            where.Add("run_id = @runId");
            cmd.Parameters.AddWithValue("@runId", options.RunId);
        }
        if (!string.IsNullOrWhiteSpace(options.CheckpointType))
        {
            where.Add("checkpoint_type = @checkpointType");
            cmd.Parameters.AddWithValue("@checkpointType", options.CheckpointType);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT id, assignment_id, run_id, checkpoint_type, payload, created_at
            FROM worker_checkpoints
            {whereClause}
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<WorkerCheckpoint>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadCheckpoint(reader));
        return results;
    }

    // ── Checkpoint Responses ─────────────────────────────────────────────

    public async Task<CheckpointResponse> AppendCheckpointResponseAsync(int checkpointId, int? assignmentId, string runId, string responseType, string payload)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var checkpoint = await GetCheckpointByIdAsync(conn, checkpointId);
            if (checkpoint is null)
                throw new InvalidOperationException($"Checkpoint {checkpointId} not found");
            if (!string.Equals(checkpoint.RunId, runId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Run ID mismatch for checkpoint {checkpointId}: response claims '{runId}', checkpoint has '{checkpoint.RunId}'");
            if (assignmentId is not null && checkpoint.AssignmentId != assignmentId.Value)
                throw new InvalidOperationException(
                    $"Assignment ID mismatch for checkpoint {checkpointId}: response claims '{assignmentId.Value}', checkpoint has '{checkpoint.AssignmentId}'");

            var effectiveAssignmentId = assignmentId ?? checkpoint.AssignmentId;
            var assignment = await GetAssignmentByIdAsync(conn, effectiveAssignmentId);
            if (assignment is null)
                throw new InvalidOperationException($"Assignment {effectiveAssignmentId} not found");
            if (assignment.RunId != runId)
                throw new InvalidOperationException(
                    $"Run ID mismatch for assignment {effectiveAssignmentId}: response claims '{runId}', assignment has '{assignment.RunId}'");

            if (WorkerPoolStates.IsTerminal(assignment.State)
                && responseType != WorkerPoolStates.ResponseAbort)
            {
                throw new InvalidOperationException(
                    $"Assignment {effectiveAssignmentId} is terminal ({assignment.State}); refusing response '{responseType}' for checkpoint {checkpointId}");
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO checkpoint_responses (checkpoint_id, assignment_id, run_id, response_type, payload)
                VALUES (@checkpointId, @assignmentId, @runId, @responseType, @payload)
                RETURNING id, checkpoint_id, assignment_id, run_id, response_type, payload, created_at
                """;
            cmd.Parameters.AddWithValue("@checkpointId", checkpointId);
            cmd.Parameters.AddWithValue("@assignmentId", effectiveAssignmentId);
            cmd.Parameters.AddWithValue("@runId", runId);
            cmd.Parameters.AddWithValue("@responseType", responseType);
            cmd.Parameters.AddWithValue("@payload", payload);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var response = ReadResponse(reader);
            await reader.CloseAsync();

            // If response is ack, transition assignment back to running
            if (responseType == WorkerPoolStates.ResponseAck)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE worker_assignments
                    SET state = 'running', updated_at = datetime('now')
                    WHERE id = @assignmentId AND state = 'checkpoint_waiting'
                    """;
                updateCmd.Parameters.AddWithValue("@assignmentId", effectiveAssignmentId);
                await updateCmd.ExecuteNonQueryAsync();
            }

            // If response is abort, transition to expired for non-terminal assignments.
            // Abort responses against terminal assignments are still recorded as audit
            // evidence, but the terminal assignment state is preserved.
            if (responseType == WorkerPoolStates.ResponseAbort)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE worker_assignments
                    SET state = 'expired', released_at = datetime('now'), updated_at = datetime('now')
                    WHERE id = @assignmentId AND state NOT IN ('completed', 'failed', 'expired')
                    """;
                updateCmd.Parameters.AddWithValue("@assignmentId", effectiveAssignmentId);
                await updateCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return response;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<CheckpointResponse>> ListResponsesAsync(int checkpointId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, checkpoint_id, assignment_id, run_id, response_type, payload, created_at
            FROM checkpoint_responses
            WHERE checkpoint_id = @checkpointId
            ORDER BY created_at ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("@checkpointId", checkpointId);

        var results = new List<CheckpointResponse>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadResponse(reader));
        return results;
    }

    public async Task<List<CheckpointResponse>> ListResponsesByRunIdAsync(string runId, int limit = 50)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, checkpoint_id, assignment_id, run_id, response_type, payload, created_at
            FROM checkpoint_responses
            WHERE run_id = @runId
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var results = new List<CheckpointResponse>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadResponse(reader));
        return results;
    }

    // ── Release & Quarantine ─────────────────────────────────────────────

    public async Task<WorkerAssignment?> RecordCleanupEvidenceAsync(int assignmentId, string evidenceJson)
    {
        await using var conn = await _db.CreateConnectionAsync();
        var assignment = await GetAssignmentByIdAsync(conn, assignmentId);
        if (assignment is null)
            return null;

        if (!WorkerPoolStates.IsTerminal(assignment.State))
            return null; // Only terminal assignments can have cleanup evidence

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE worker_assignments
            SET cleanup_evidence = @evidenceJson,
                cleanup_recorded_at = datetime('now'),
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING {AssignmentColumns}
            """;
        cmd.Parameters.AddWithValue("@id", assignmentId);
        cmd.Parameters.AddWithValue("@evidenceJson", evidenceJson);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignment(reader) : null;
    }

    public async Task<WorkerAssignment?> ReleaseAssignmentAsync(int assignmentId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        var assignment = await GetAssignmentByIdAsync(conn, assignmentId);
        if (assignment is null)
            return null;

        // Release requires terminal state + cleanup evidence
        if (!WorkerPoolStates.IsTerminal(assignment.State))
            return null;

        if (string.IsNullOrWhiteSpace(assignment.CleanupEvidence))
            return null;

        // Already released?
        if (assignment.ReleasedAt is not null && assignment.CleanupRecordedAt is not null)
            return assignment; // Idempotent

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE worker_assignments
            SET released_at = COALESCE(released_at, datetime('now')),
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING {AssignmentColumns}
            """;
        cmd.Parameters.AddWithValue("@id", assignmentId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignment(reader) : null;
    }

    public async Task<bool> QuarantineWorkerAsync(string workerIdentity, string quarantinedBy, string? reason = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var worker = await GetMemberByConnAsync(conn, workerIdentity);
            if (worker is null)
                return false;

            var metadata = worker.Metadata;
            try
            {
                var metaObj = string.IsNullOrWhiteSpace(metadata)
                    ? new Dictionary<string, object?>()
                    : JsonSerializer.Deserialize<Dictionary<string, object?>>(metadata) ?? new Dictionary<string, object?>();
                metaObj["quarantined_by"] = quarantinedBy;
                metaObj["quarantine_reason"] = reason;
                metaObj["quarantined_at"] = DateTime.UtcNow.ToString("o");
                metadata = JsonSerializer.Serialize(metaObj);
            }
            catch
            {
                // If existing metadata is malformed, replace it
                metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["quarantined_by"] = quarantinedBy,
                    ["quarantine_reason"] = reason,
                    ["quarantined_at"] = DateTime.UtcNow.ToString("o")
                });
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE worker_pool_members
                SET status = 'quarantined',
                    metadata = @metadata,
                    updated_at = datetime('now')
                WHERE worker_identity = @workerIdentity
                """;
            cmd.Parameters.AddWithValue("@workerIdentity", workerIdentity);
            cmd.Parameters.AddWithValue("@metadata", metadata);

            var result = await cmd.ExecuteNonQueryAsync() > 0;
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ── Summary ──────────────────────────────────────────────────────────

    public async Task<WorkerPoolSummary> GetSummaryAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();

        var summary = new WorkerPoolSummary();

        // Member counts
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT status, count(*) FROM worker_pool_members GROUP BY status";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var status = reader.GetString(0);
                var count = Convert.ToInt32(reader.GetValue(1));
                summary.TotalMembers += count;
                switch (status)
                {
                    case "available": summary.AvailableMembers = count; break;
                    case "busy": summary.BusyMembers = count; break;
                    case "quarantined": summary.QuarantinedMembers = count; break;
                }
            }
        }

        // Assignment counts by state
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT wa.state, count(*)
                FROM worker_assignments wa
                GROUP BY wa.state
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var state = reader.GetString(0);
                var count = Convert.ToInt32(reader.GetValue(1));
                switch (state)
                {
                    case "completed": summary.CompletedAssignments = count; break;
                    case "failed": summary.FailedAssignments = count; break;
                    case "expired": summary.ExpiredAssignments = count; break;
                    default: summary.ActiveAssignments += count; break;
                }
            }
        }

        // Recent checkpoints (last 24h)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM worker_checkpoints WHERE created_at >= datetime('now', '-1 day')";
            var result = await cmd.ExecuteScalarAsync();
            summary.RecentCheckpoints = Convert.ToInt32(result ?? 0L);
        }

        // Stale launching assignments: assignments in 'ack' state (leased but never
        // claimed/started by any runtime substrate) older than 10 minutes.
        // Detected purely from generic worker_assignments state — no pi_sessions,
        // Hermes, or Pi runtime table join. den-host provides the host-level
        // process confirmation path.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT count(*)
                FROM worker_assignments
                WHERE state = 'ack'
                  AND created_at <= datetime('now', '-10 minutes')
                """;
            var result = await cmd.ExecuteScalarAsync();
            summary.StaleLaunchingAssignments = Convert.ToInt32(result ?? 0L);
        }

        // Per-profile lane capacity breakdown for observability ("spawned-coder: 2/4 busy").
        var laneProfiles = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT profile_identity
                FROM worker_pool_lanes
                ORDER BY profile_identity
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                laneProfiles.Add(reader.GetString(0));
        }
        if (laneProfiles.Count > 0)
        {
            var breakdown = new List<ProfileCapacitySummary>();
            foreach (var profileIdentity in laneProfiles)
                breakdown.Add(await GetProfileCapacitySummaryAsync(profileIdentity));
            summary.PerProfileBreakdown = JsonSerializer.Serialize(breakdown, JsonOptions);
        }

        return summary;
    }

    // ── No-Capacity Diagnostics ──────────────────────────────────────

    public async Task<LeaseWorkerResult> LeaseWorkerWithDiagnosticsAsync(LeaseWorkerInput input)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // First try the normal lease path
            WorkerAssignment? assignment;
            var preferred = input.PreferredWorkerIdentity;

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                // Try preferred worker first
                assignment = await TryLeaseSpecificWorkerAsync(conn, input);
                if (assignment is not null)
                {
                    await tx.CommitAsync();
                    var capacity = await GetCapacityForLeaseResultAsync(assignment.ProfileIdentity);
                    return new LeaseWorkerResult
                    {
                        IsSuccess = true,
                        Assignment = assignment,
                        Capacity = capacity,
                    };
                }

                // Preferred worker failed — check why
                var preferredWorker = await GetMemberByConnAsync(conn, preferred);
                if (preferredWorker is null)
                {
                    // Worker doesn't exist at all
                    var stats = await CountCandidatesByStatusAsync(conn, input.ProfileIdentity, input.WorkerRole);
                    var record = await InsertNoCapacityRequestAsync(conn, input,
                        WorkerPoolStates.NoCapacityPreferredNotFoundOrBusy,
                        stats,
                        $"Preferred worker '{preferred}' not found in pool. {stats.Total} total workers matching filters.");
                    await tx.CommitAsync();
                    return new LeaseWorkerResult
                    {
                        IsSuccess = false,
                        NoCapacity = record,
                        Capacity = await GetCapacityForLeaseResultAsync(input.ProfileIdentity),
                    };
                }

                if (preferredWorker.Status != WorkerPoolStates.MemberAvailable)
                {
                    var stats = await CountCandidatesByStatusAsync(conn, input.ProfileIdentity, input.WorkerRole);
                    var busyCheck = await HasActiveAssignmentAsync(conn, preferred, input.RunId);
                    var msg = busyCheck
                        ? $"Preferred worker '{preferred}' has active assignment for run '{input.RunId}'"
                        : $"Preferred worker '{preferred}' status is '{preferredWorker.Status}' (not available).";
                    var record = await InsertNoCapacityRequestAsync(conn, input,
                        WorkerPoolStates.NoCapacityPreferredNotFoundOrBusy,
                        stats,
                        msg);
                    await tx.CommitAsync();
                    return new LeaseWorkerResult
                    {
                        IsSuccess = false,
                        NoCapacity = record,
                        Capacity = await GetCapacityForLeaseResultAsync(input.ProfileIdentity ?? preferredWorker.ProfileIdentity),
                    };
                }
            }

            // Find candidates
            var candidates = await FindAvailableWorkersAsync(conn, input.RequiredCapabilities, input.ProfileIdentity, input.WorkerRole);
            if (candidates.Count == 0)
            {
                // No matching workers — determine why
                var reasonCode = await DiagnoseNoMatchingWorkersAsync(conn, input);
                var stats = await CountCandidatesByStatusAsync(conn, input.ProfileIdentity, input.WorkerRole);

                // Build diagnostic message with role-alias normalisation info when relevant
                string? diagMessage;
                if (reasonCode == WorkerPoolStates.NoCapacityHardSelectorMismatch)
                {
                    var roleAliasCaps = input.RequiredCapabilities?
                        .Where(c => WorkerPoolStates.IsRoleAlias(c))
                        .ToArray() ?? [];
                    var hardCaps = input.RequiredCapabilities?
                        .Where(c => !WorkerPoolStates.IsRoleAlias(c))
                        .ToArray() ?? [];

                    var roleAliasNote = roleAliasCaps.Length > 0
                        ? $"role aliases normalized (matched via worker_role): [{string.Join(", ", roleAliasCaps)}]. "
                        : "";
                    diagMessage = roleAliasNote +
                        $"Hard capability mismatch: [{string.Join(", ", hardCaps)}] not satisfied by any of {stats.Available} available worker(s) matching role/profile.";
                }
                else if (stats.Available == 0 && stats.Busy > 0)
                {
                    diagMessage = $"No available workers matching criteria. {stats.Busy} worker(s) are busy.";
                }
                else if (stats.Available == 0 && stats.Quarantined > 0)
                {
                    diagMessage = $"No available workers matching criteria. {stats.Quarantined} worker(s) quarantined.";
                }
                else if (stats.Total == 0)
                {
                    var roleAliasNote = input.RequiredCapabilities?
                        .Any(c => WorkerPoolStates.IsRoleAlias(c)) == true
                        ? $" (Note: input contained role-alias capabilities [{string.Join(", ", input.RequiredCapabilities!.Where(WorkerPoolStates.IsRoleAlias))}] that were normalized against worker_role; no worker with matching role registered.)"
                        : "";
                    diagMessage = $"No workers registered in the pool matching the requested role/profile/capabilities.{roleAliasNote}";
                }
                else
                {
                    diagMessage = $"No matching candidate workers available. Total candidates: {stats.Total}.";
                }

                var record = await InsertNoCapacityRequestAsync(conn, input, reasonCode, stats, diagMessage);
                await tx.CommitAsync();
                return new LeaseWorkerResult
                {
                    IsSuccess = false,
                    NoCapacity = record,
                    Capacity = await GetCapacityForLeaseResultAsync(input.ProfileIdentity),
                };
            }

            // Try each candidate
            assignment = null;
            foreach (var workerId in candidates)
            {
                var leased = await TryLeaseSpecificWorkerAsync(conn, input with { PreferredWorkerIdentity = workerId });
                if (leased is not null)
                {
                    assignment = leased;
                    break;
                }
            }

            if (assignment is not null)
            {
                await tx.CommitAsync();
                return new LeaseWorkerResult
                {
                    IsSuccess = true,
                    Assignment = assignment,
                    Capacity = await GetCapacityForLeaseResultAsync(assignment.ProfileIdentity),
                };
            }

            // All candidates failed — likely all busy now (race condition or conflicting constraints)
            var finalStats = await CountCandidatesByStatusAsync(conn, input.ProfileIdentity, input.WorkerRole);
            var finalRecord = await InsertNoCapacityRequestAsync(conn, input,
                WorkerPoolStates.NoCapacityAllBusy,
                finalStats,
                $"All {candidates.Count} matching workers became unavailable. {finalStats.Busy} busy, {finalStats.Available} available.");
            await tx.CommitAsync();
            return new LeaseWorkerResult
            {
                IsSuccess = false,
                NoCapacity = finalRecord,
                Capacity = await GetCapacityForLeaseResultAsync(input.ProfileIdentity),
            };
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private async Task<ProfileCapacitySummary?> GetCapacityForLeaseResultAsync(string? profileIdentity)
    {
        if (string.IsNullOrWhiteSpace(profileIdentity))
            return null;

        var summary = await GetProfileCapacitySummaryAsync(profileIdentity);
        return summary.TotalCapacity > 0 || summary.Lanes.Count > 0 ? summary : null;
    }

    public async Task<List<WorkerNoCapacityRequest>> ListNoCapacityRequestsAsync(NoCapacityRequestListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }
        if (!string.IsNullOrWhiteSpace(options.RunId))
        {
            where.Add("run_id = @runId");
            cmd.Parameters.AddWithValue("@runId", options.RunId);
        }
        if (!string.IsNullOrWhiteSpace(options.ReasonCode))
        {
            where.Add("reason_code = @reasonCode");
            cmd.Parameters.AddWithValue("@reasonCode", options.ReasonCode);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT id, project_id, task_id, role, assigned_by, run_id,
                   profile_identity, worker_role, required_capabilities, preferred_worker_identity,
                   reason_code, candidate_details, diagnostic_message, created_at
            FROM worker_no_capacity_requests
            {whereClause}
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<WorkerNoCapacityRequest>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadNoCapacityRequest(reader));
        return results;
    }

    public async Task<WorkerNoCapacityRequest?> GetNoCapacityRequestAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_id, task_id, role, assigned_by, run_id,
                   profile_identity, worker_role, required_capabilities, preferred_worker_identity,
                   reason_code, candidate_details, diagnostic_message, created_at
            FROM worker_no_capacity_requests
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadNoCapacityRequest(reader) : null;
    }

    // ── No-Capacity Private Helpers ───────────────────────────────────

    /// <summary>
    /// Count matching workers by status to build candidate statistics.
    /// </summary>
    private static async Task<WorkerCandidateStats> CountCandidatesByStatusAsync(SqliteConnection conn, string? profileIdentity, string? workerRole)
    {
        var stats = new WorkerCandidateStats();
        await using var cmd = conn.CreateCommand();

        var sql = "SELECT status, count(*) FROM worker_pool_members";
        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(profileIdentity))
        {
            filters.Add("profile_identity = @profileIdentity");
            cmd.Parameters.AddWithValue("@profileIdentity", profileIdentity);
        }
        if (!string.IsNullOrWhiteSpace(workerRole))
        {
            filters.Add("worker_role = @workerRole");
            cmd.Parameters.AddWithValue("@workerRole", workerRole);
        }

        if (filters.Count > 0)
            sql += " WHERE " + string.Join(" AND ", filters);

        sql += " GROUP BY status";
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var status = reader.GetString(0);
            var count = Convert.ToInt32(reader.GetValue(1));
            stats.Total += count;
            switch (status)
            {
                case "available": stats.Available = count; break;
                case "busy": stats.Busy = count; break;
                case "quarantined": stats.Quarantined = count; break;
                case "offboarded": stats.Offboarded = count; break;
            }
        }
        return stats;
    }

    /// <summary>
    /// Diagnose why no matching workers were found — distinguishes
    /// no_matching_worker, all_busy, all_quarantined_or_offline, hard_selector_mismatch,
    /// and ambiguous.
    /// </summary>
    private static async Task<string> DiagnoseNoMatchingWorkersAsync(SqliteConnection conn, LeaseWorkerInput input)
    {
        var stats = await CountCandidatesByStatusAsync(conn, input.ProfileIdentity, input.WorkerRole);

        if (stats.Total == 0)
            return WorkerPoolStates.NoCapacityNoMatchingWorker;

        if (stats.Busy > 0 && stats.Available == 0 && stats.Quarantined == 0 && stats.Offboarded == 0)
            return WorkerPoolStates.NoCapacityAllBusy;

        if (stats.Quarantined > 0 || stats.Offboarded > 0)
        {
            var hasUnavailable = stats.Quarantined > 0 || stats.Offboarded > 0;
            var hasBusy = stats.Busy > 0;
            if (stats.Available == 0 && !hasBusy && hasUnavailable)
                return WorkerPoolStates.NoCapacityAllQuarantinedOrOffline;
        }

        // Workers matching role/profile exist in some status combination but none available:
        // if there are available workers, the mismatch is from hard capability constraints.
        if (stats.Available > 0 && input.RequiredCapabilities is { Length: > 0 })
        {
            // Workers exist and are available, but capability filter eliminated them.
            // Check if the only constraints are role aliases (shouldn't happen here
            // since role-aliases are checked against worker_role in FindAvailableWorkersAsync).
            var hardCaps = input.RequiredCapabilities
                .Where(c => !WorkerPoolStates.IsRoleAlias(c))
                .ToArray();
            if (hardCaps.Length > 0)
                return WorkerPoolStates.NoCapacityHardSelectorMismatch;
        }

        // Multiple statuses present but none available: ambiguous
        if (stats.Available == 0 && stats.Total > 0)
            return WorkerPoolStates.NoCapacityAmbiguous;

        // Workers exist but capabilities don't match — still no_matching_worker
        return WorkerPoolStates.NoCapacityNoMatchingWorker;
    }

    /// <summary>
    /// Insert a no-capacity request record and return the created entity.
    /// </summary>
    private static async Task<WorkerNoCapacityRequest> InsertNoCapacityRequestAsync(
        SqliteConnection conn, LeaseWorkerInput input, string reasonCode,
        WorkerCandidateStats stats, string? diagnosticMessage)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO worker_no_capacity_requests
                (project_id, task_id, role, assigned_by, run_id,
                 profile_identity, worker_role, required_capabilities, preferred_worker_identity,
                 reason_code, candidate_details, diagnostic_message)
            VALUES
                (@projectId, @taskId, @role, @assignedBy, @runId,
                 @profileIdentity, @workerRole, @requiredCapabilities, @preferredWorkerIdentity,
                 @reasonCode, @candidateDetails, @diagnosticMessage)
            RETURNING id, project_id, task_id, role, assigned_by, run_id,
                      profile_identity, worker_role, required_capabilities, preferred_worker_identity,
                      reason_code, candidate_details, diagnostic_message, created_at
            """;
        cmd.Parameters.AddWithValue("@projectId", input.ProjectId);
        cmd.Parameters.AddWithValue("@taskId", (object?)input.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", input.Role);
        cmd.Parameters.AddWithValue("@assignedBy", input.AssignedBy);
        cmd.Parameters.AddWithValue("@runId", input.RunId);
        cmd.Parameters.AddWithValue("@profileIdentity", (object?)input.ProfileIdentity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@workerRole", (object?)input.WorkerRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requiredCapabilities", (object?)(input.RequiredCapabilities is not null
            ? System.Text.Json.JsonSerializer.Serialize(input.RequiredCapabilities)
            : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@preferredWorkerIdentity", (object?)input.PreferredWorkerIdentity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reasonCode", reasonCode);
        cmd.Parameters.AddWithValue("@candidateDetails", stats.ToJson());
        cmd.Parameters.AddWithValue("@diagnosticMessage", (object?)diagnosticMessage ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadNoCapacityRequest(reader);
    }

    /// <summary>
    /// Read a WorkerNoCapacityRequest from column order:
    /// 0 id, 1 project_id, 2 task_id, 3 role, 4 assigned_by, 5 run_id,
    /// 6 profile_identity, 7 worker_role, 8 required_capabilities, 9 preferred_worker_identity,
    /// 10 reason_code, 11 candidate_details, 12 diagnostic_message, 13 created_at
    /// </summary>
    private static WorkerNoCapacityRequest ReadNoCapacityRequest(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ProjectId = reader.GetString(1),
        TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        Role = reader.GetString(3),
        AssignedBy = reader.GetString(4),
        RunId = reader.GetString(5),
        ProfileIdentity = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
        WorkerRole = reader.IsDBNull(7) ? null : reader.GetString(7),
        RequiredCapabilities = reader.IsDBNull(8) ? null : reader.GetString(8),
        PreferredWorkerIdentity = reader.IsDBNull(9) ? null : reader.GetString(9),
        ReasonCode = reader.GetString(10),
        CandidateDetails = reader.GetString(11),
        DiagnosticMessage = reader.IsDBNull(12) ? null : reader.GetString(12),
        CreatedAt = DateTime.Parse(reader.GetString(13)),
    };

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Read a WorkerPoolMember from column order:
    /// 0 worker_identity, 1 profile_identity, 2 worker_role, 3 display_name, 4 capabilities,
    /// 5 status, 6 last_heartbeat, 7 agent_instance_id, 8 channel_id, 9 session_id, 10 metadata,
    /// 11 created_at, 12 updated_at
    /// </summary>
    private static WorkerPoolMember ReadMember(SqliteDataReader reader) => new()
    {
        WorkerIdentity = reader.GetString(0),
        ProfileIdentity = reader.GetString(1),
        WorkerRole = reader.IsDBNull(2) ? null : reader.GetString(2),
        DisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
        Capabilities = reader.IsDBNull(4) ? null : reader.GetString(4),
        Status = reader.GetString(5),
        LastHeartbeat = reader.IsDBNull(6) ? null : reader.GetString(6),
        AgentInstanceId = reader.IsDBNull(7) ? null : reader.GetString(7),
        ChannelId = reader.IsDBNull(8) ? null : reader.GetString(8),
        SessionId = reader.IsDBNull(9) ? null : reader.GetString(9),
        AdapterInstanceId = reader.IsDBNull(10) ? null : reader.GetString(10),
        LogPointer = reader.IsDBNull(11) ? null : reader.GetString(11),
        StaleAfterSeconds = reader.IsDBNull(12) ? null : reader.GetInt32(12),
        Metadata = reader.IsDBNull(13) ? null : reader.GetString(13),
        CreatedAt = DateTime.Parse(reader.GetString(14)),
        UpdatedAt = DateTime.Parse(reader.GetString(15)),
    };

    /// <summary>
    /// Read a WorkerAssignment from column order (standalone, no JOIN):
    /// 0 id, 1 worker_identity, 2 run_id, 3 project_id, 4 task_id, 5 role, 6 assigned_by,
    /// 7 state, 8 lease_id, 9 profile_identity, 10 latest_checkpoint_id, 11 cleanup_evidence,
    /// 12 cleanup_recorded_at, 13 acquired_at, 14 released_at, 15 created_at, 16 updated_at
    /// </summary>
    private static WorkerAssignment ReadAssignment(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        WorkerIdentity = reader.GetString(1),
        RunId = reader.GetString(2),
        ProjectId = reader.GetString(3),
        TaskId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
        Role = reader.GetString(5),
        AssignedBy = reader.GetString(6),
        State = reader.GetString(7),
        LeaseId = reader.IsDBNull(8) ? null : reader.GetString(8),
        ProfileIdentity = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
        LatestCheckpointId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        CleanupEvidence = reader.IsDBNull(11) ? null : reader.GetString(11),
        CleanupRecordedAt = reader.IsDBNull(12) ? null : reader.GetString(12),
        AcquiredAt = reader.IsDBNull(13) ? null : reader.GetString(13),
        ReleasedAt = reader.IsDBNull(14) ? null : reader.GetString(14),
        CreatedAt = DateTime.Parse(reader.GetString(15)),
        UpdatedAt = DateTime.Parse(reader.GetString(16)),
    };

    /// <summary>
    /// Read a WorkerAssignment with denormalized profile fields from a LEFT JOIN.
    /// Manual JOIN columns are at positions 17-19: worker_role, agent_instance_id, channel_id.
    /// profile_identity is now a column on wa itself (denormalized at insert).
    /// </summary>
    private static WorkerAssignment ReadAssignmentWithJoin(SqliteDataReader reader)
    {
        var assignment = ReadAssignment(reader);
        // Columns 17-19 are the LEFT JOIN denormalized fields
        assignment.PoolMemberId = assignment.WorkerIdentity; // PoolMemberId alias
        assignment.WorkerRole = reader.IsDBNull(17) ? null : reader.GetString(17);
        assignment.AgentInstanceId = reader.IsDBNull(18) ? null : reader.GetString(18);
        assignment.ChannelId = reader.IsDBNull(19) ? null : reader.GetString(19);
        return assignment;
    }

    /// <summary>
    /// Read a WorkerAssignment with denormalized profile fields, passing the known worker.
    /// Used in TryLeaseSpecificWorkerAsync where we already have the worker.
    /// Column order: 0 id, 1 worker_identity, 2 run_id, 3 project_id, 4 task_id, 5 role,
    /// 6 assigned_by, 7 state, 8 lease_id, 9 profile_identity, 10 latest_checkpoint_id,
    /// 11 cleanup_evidence, 12 cleanup_recorded_at, 13 acquired_at, 14 released_at,
    /// 15 created_at, 16 updated_at
    /// </summary>
    private static WorkerAssignment ReadAssignment(SqliteDataReader reader, WorkerPoolMember worker) => new()
    {
        Id = reader.GetInt32(0),
        WorkerIdentity = reader.GetString(1),
        PoolMemberId = worker.WorkerIdentity,
        ProfileIdentity = worker.ProfileIdentity,
        WorkerRole = worker.WorkerRole,
        AgentInstanceId = worker.AgentInstanceId,
        ChannelId = worker.ChannelId,
        RunId = reader.GetString(2),
        ProjectId = reader.GetString(3),
        TaskId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
        Role = reader.GetString(5),
        AssignedBy = reader.GetString(6),
        State = reader.GetString(7),
        LeaseId = reader.IsDBNull(8) ? null : reader.GetString(8),
        LatestCheckpointId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        CleanupEvidence = reader.IsDBNull(11) ? null : reader.GetString(11),
        CleanupRecordedAt = reader.IsDBNull(12) ? null : reader.GetString(12),
        AcquiredAt = reader.IsDBNull(13) ? null : reader.GetString(13),
        ReleasedAt = reader.IsDBNull(14) ? null : reader.GetString(14),
        CreatedAt = DateTime.Parse(reader.GetString(15)),
        UpdatedAt = DateTime.Parse(reader.GetString(16)),
    };

    private static async Task<WorkerPoolMember?> GetMemberByConnAsync(SqliteConnection conn, string workerIdentity)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {MemberColumns}
            FROM worker_pool_members
            WHERE worker_identity = @workerIdentity
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", workerIdentity);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadMember(reader) : null;
    }

    private static async Task SetMemberStatusByConnAsync(SqliteConnection conn, string workerIdentity, string status)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE worker_pool_members
            SET status = @status, updated_at = datetime('now')
            WHERE worker_identity = @workerIdentity
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", workerIdentity);
        cmd.Parameters.AddWithValue("@status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> HasActiveAssignmentAsync(SqliteConnection conn, string workerIdentity, string runId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM worker_assignments
            WHERE worker_identity = @workerIdentity
              AND run_id = @runId
              AND state NOT IN ('completed', 'failed', 'expired')
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", workerIdentity);
        cmd.Parameters.AddWithValue("@runId", runId);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }

    private static async Task<List<string>> FindAvailableWorkersAsync(SqliteConnection conn, string[]? requiredCapabilities, string? profileIdentity = null, string? workerRole = null)
    {
        var workers = new List<string>();
        await using var cmd = conn.CreateCommand();

        var sql = "SELECT worker_identity, capabilities, worker_role FROM worker_pool_members WHERE status = 'available'";
        if (!string.IsNullOrWhiteSpace(profileIdentity))
        {
            sql += " AND profile_identity = @profileIdentity";
            cmd.Parameters.AddWithValue("@profileIdentity", profileIdentity);
        }
        if (!string.IsNullOrWhiteSpace(workerRole))
        {
            sql += " AND worker_role = @workerRole";
            cmd.Parameters.AddWithValue("@workerRole", workerRole);
        }
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var workerId = reader.GetString(0);
            var capabilitiesJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            var memberWorkerRole = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (requiredCapabilities is null || requiredCapabilities.Length == 0)
            {
                workers.Add(workerId);
                continue;
            }

            // Classify capabilities: role-aliases vs hard capabilities
            var roleAliasCaps = new List<string>();
            var hardCaps = new List<string>();
            foreach (var c in requiredCapabilities)
            {
                if (WorkerPoolStates.IsRoleAlias(c))
                    roleAliasCaps.Add(c);
                else
                    hardCaps.Add(c);
            }

            // Check role-alias capabilities against worker_role.
            // If the worker's role matches a role alias, it's satisfied.
            // Otherwise fall back to capabilities JSON for backward compatibility.
            var unsatisfiedRoleAliases = roleAliasCaps
                .Where(rac => memberWorkerRole is null
                    || !string.Equals(memberWorkerRole, rac, StringComparison.Ordinal))
                .ToList();

            // For role aliases not satisfied by worker_role, fall back to capabilities check
            var allCapsToCheck = hardCaps.Concat(unsatisfiedRoleAliases).ToList();
            string[]? workerCaps = null;

            if (allCapsToCheck.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(capabilitiesJson))
                    continue; // Hard caps or unsatisfied role aliases, but no capabilities to match

                try
                {
                    workerCaps = JsonSerializer.Deserialize<string[]>(capabilitiesJson);
                }
                catch
                {
                    continue; // Malformed capabilities JSON — skip
                }

                if (workerCaps is null)
                    continue;
            }

            // All required caps (hard + unsatisfied role aliases) must be in workerCaps
            if (allCapsToCheck.Count > 0
                && !allCapsToCheck.All(c => workerCaps!.Contains(c, StringComparer.Ordinal)))
                continue;

            workers.Add(workerId);
        }

        return workers;
    }

    private static async Task<WorkerAssignment?> GetAssignmentByIdAsync(SqliteConnection conn, int assignmentId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {AssignmentColumnsPrefixed}, wm.worker_role, wm.agent_instance_id, wm.channel_id
            FROM worker_assignments wa
            LEFT JOIN worker_pool_members wm ON wa.worker_identity = wm.worker_identity
            WHERE wa.id = @id
            """;
        cmd.Parameters.AddWithValue("@id", assignmentId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignmentWithJoin(reader) : null;
    }

    private static async Task<WorkerCheckpoint?> GetCheckpointByIdAsync(SqliteConnection conn, int checkpointId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, assignment_id, run_id, checkpoint_type, payload, created_at
            FROM worker_checkpoints
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", checkpointId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCheckpoint(reader) : null;
    }

    private static string StateForCheckpointType(string checkpointType) => checkpointType switch
    {
        WorkerPoolStates.CheckpointCompletion => WorkerPoolStates.Completed,
        WorkerPoolStates.CheckpointFailure => WorkerPoolStates.Failed,
        _ => WorkerPoolStates.CheckpointWaiting,
    };

    private static WorkerCheckpoint ReadCheckpoint(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        AssignmentId = reader.GetInt32(1),
        RunId = reader.GetString(2),
        CheckpointType = reader.GetString(3),
        Payload = reader.GetString(4),
        CreatedAt = DateTime.Parse(reader.GetString(5)),
    };

    private static CheckpointResponse ReadResponse(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        CheckpointId = reader.GetInt32(1),
        AssignmentId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        RunId = reader.GetString(3),
        ResponseType = reader.GetString(4),
        Payload = reader.GetString(5),
        CreatedAt = DateTime.Parse(reader.GetString(6)),
    };

    private static bool IsValidTransition(string currentState, string newState)
    {
        if (currentState == newState)
            return true; // Idempotent

        if (!WorkerPoolStates.ValidAssignmentStates.Contains(newState))
            return false;

        // Terminal -> anything is invalid (already done)
        if (WorkerPoolStates.IsTerminal(currentState))
            return false;

        // From any non-terminal state, allow these transitions
        return newState switch
        {
            WorkerPoolStates.Running => currentState == WorkerPoolStates.Ack || currentState == WorkerPoolStates.CheckpointWaiting || currentState == WorkerPoolStates.Blocked,
            WorkerPoolStates.CheckpointWaiting => currentState == WorkerPoolStates.Ack || currentState == WorkerPoolStates.Running,
            WorkerPoolStates.Blocked => currentState == WorkerPoolStates.Ack || currentState == WorkerPoolStates.Running || currentState == WorkerPoolStates.CheckpointWaiting,
            WorkerPoolStates.Completed => currentState != WorkerPoolStates.Completed && !WorkerPoolStates.IsTerminal(currentState),
            WorkerPoolStates.Failed => currentState != WorkerPoolStates.Failed && !WorkerPoolStates.IsTerminal(currentState),
            WorkerPoolStates.Expired => currentState != WorkerPoolStates.Expired && !WorkerPoolStates.IsTerminal(currentState),
            _ => false,
        };
    }

    // ── Pool Lanes ────────────────────────────────────────────────────────

    private const string LaneColumns =
        "profile_identity, worker_role, capacity, status, metadata, created_at, updated_at";

    private static WorkerPoolLane ReadLane(SqliteDataReader reader) => new()
    {
        ProfileIdentity = reader.GetString(0),
        WorkerRole = reader.GetString(1),
        Capacity = reader.GetInt32(2),
        Status = reader.GetString(3),
        Metadata = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt = DateTime.Parse(reader.GetString(5)),
        UpdatedAt = DateTime.Parse(reader.GetString(6)),
    };

    public async Task<WorkerPoolLane> UpsertLaneAsync(WorkerPoolLane lane)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO worker_pool_lanes (profile_identity, worker_role, capacity, status, metadata)
            VALUES (@profileIdentity, @workerRole, @capacity, @status, @metadata)
            ON CONFLICT(profile_identity, worker_role) DO UPDATE SET
                capacity = excluded.capacity,
                status = excluded.status,
                metadata = COALESCE(excluded.metadata, worker_pool_lanes.metadata),
                updated_at = datetime('now')
            RETURNING {LaneColumns}
            """;
        cmd.Parameters.AddWithValue("@profileIdentity", lane.ProfileIdentity);
        cmd.Parameters.AddWithValue("@workerRole", lane.WorkerRole);
        cmd.Parameters.AddWithValue("@capacity", lane.Capacity);
        cmd.Parameters.AddWithValue("@status", lane.Status);
        cmd.Parameters.AddWithValue("@metadata", (object?)lane.Metadata ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadLane(reader);
    }

    public async Task<WorkerPoolLane?> GetLaneAsync(string profileIdentity, string workerRole)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {LaneColumns}
            FROM worker_pool_lanes
            WHERE profile_identity = @pi AND worker_role = @wr
            """;
        cmd.Parameters.AddWithValue("@pi", profileIdentity);
        cmd.Parameters.AddWithValue("@wr", workerRole);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadLane(reader) : null;
    }

    public async Task<List<WorkerPoolLane>> ListLanesAsync(string? profileIdentity = null, string? status = null, int limit = 50)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(profileIdentity))
        {
            where.Add("profile_identity = @pi");
            cmd.Parameters.AddWithValue("@pi", profileIdentity);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", status);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT {LaneColumns}
            FROM worker_pool_lanes
            {whereClause}
            ORDER BY updated_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var results = new List<WorkerPoolLane>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadLane(reader));
        return results;
    }

    public async Task<int> SetLaneStatusAsync(string profileIdentity, string workerRole, string status)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE worker_pool_lanes
                SET status = @status, updated_at = datetime('now')
                WHERE profile_identity = @pi AND worker_role = @wr
                """;
            cmd.Parameters.AddWithValue("@pi", profileIdentity);
            cmd.Parameters.AddWithValue("@wr", workerRole);
            cmd.Parameters.AddWithValue("@status", status);

            var rows = await cmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            return rows;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<ProfileCapacitySummary> GetProfileCapacitySummaryAsync(string profileIdentity)
    {
        await using var conn = await _db.CreateConnectionAsync();

        var laneRows = new List<(string WorkerRole, int Capacity, int BusyCount)>();

        // Count active non-terminal assignments per lane — collect rows first
        await using (var laneCmd = conn.CreateCommand())
        {
            laneCmd.CommandText = """
                SELECT wl.worker_role, wl.capacity,
                       COUNT(wa.id) as busy_count
                FROM worker_pool_lanes wl
                LEFT JOIN worker_assignments wa ON wa.profile_identity = wl.profile_identity
                    AND wa.role = wl.worker_role
                    AND wa.state NOT IN ('completed', 'failed', 'expired')
                WHERE wl.profile_identity = @pi AND wl.status = 'active'
                GROUP BY wl.worker_role
                """;
            laneCmd.Parameters.AddWithValue("@pi", profileIdentity);

            await using var reader = await laneCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                laneRows.Add((reader.GetString(0), reader.GetInt32(1), Convert.ToInt32(reader.GetValue(2))));
            }
        }

        var lanes = new List<LaneCapacitySummary>();
        foreach (var (wr, cap, busy) in laneRows)
        {
            // Count quarantined members for this lane
            var quarantinedCount = 0;
            await using (var qCmd = conn.CreateCommand())
            {
                qCmd.CommandText = """
                    SELECT count(*) FROM worker_pool_members
                    WHERE profile_identity = @pi2 AND worker_role = @wr AND status = 'quarantined'
                    """;
                qCmd.Parameters.AddWithValue("@pi2", profileIdentity);
                qCmd.Parameters.AddWithValue("@wr", wr);
                var result = await qCmd.ExecuteScalarAsync();
                quarantinedCount = Convert.ToInt32(result ?? 0L);
            }

            lanes.Add(new LaneCapacitySummary
            {
                ProfileIdentity = profileIdentity,
                WorkerRole = wr,
                Capacity = cap,
                BusyCount = busy,
                AvailableCount = Math.Max(0, cap - busy),
                QuarantinedCount = quarantinedCount,
            });
        }

        var totalCapacity = lanes.Sum(l => l.Capacity);
        var activeLeases = lanes.Sum(l => l.BusyCount);

        return new ProfileCapacitySummary
        {
            ProfileIdentity = profileIdentity,
            TotalCapacity = totalCapacity,
            ActiveLeases = activeLeases,
            AvailableSlots = Math.Max(0, totalCapacity - activeLeases),
            Lanes = lanes,
        };
    }

    // ── Stale Detection ───────────────────────────────────────────────────

    public async Task<int> ReleaseStaleLeasesAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // Find busy members whose last_heartbeat + stale_after_seconds < now.
            // Computed stale — no persistent 'stale' member status.
            await using var findCmd = conn.CreateCommand();
            findCmd.CommandText = """
                SELECT wm.worker_identity
                FROM worker_pool_members wm
                WHERE wm.status = 'busy'
                  AND wm.stale_after_seconds IS NOT NULL
                  AND wm.last_heartbeat IS NOT NULL
                  AND datetime(wm.last_heartbeat, '+' || wm.stale_after_seconds || ' seconds') < datetime('now')
                """;

            var staleWorkers = new List<string>();
            await using (var reader = await findCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    staleWorkers.Add(reader.GetString(0));
            }

            var releasedCount = 0;
            foreach (var workerId in staleWorkers)
            {
                await using var relCmd = conn.CreateCommand();
                relCmd.CommandText = """
                    UPDATE worker_assignments
                    SET state = 'expired',
                        released_at = datetime('now'),
                        updated_at = datetime('now')
                    WHERE worker_identity = @workerId
                      AND state NOT IN ('completed', 'failed', 'expired')
                      RETURNING id
                    """;
                relCmd.Parameters.AddWithValue("@workerId", workerId);

                await using var relReader = await relCmd.ExecuteReaderAsync();
                while (await relReader.ReadAsync())
                    releasedCount++;

                await using var availCmd = conn.CreateCommand();
                availCmd.CommandText = """
                    UPDATE worker_pool_members
                    SET status = 'available', updated_at = datetime('now')
                    WHERE worker_identity = @workerId
                    """;
                availCmd.Parameters.AddWithValue("@workerId", workerId);
                await availCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return releasedCount;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ── Staleness metadata helpers ───────────────────────────────────────

    /// <summary>
    /// Compute an ISO 8601 staleness deadline from the last-activity timestamp and a threshold in minutes.
    /// </summary>
    private static string ComputeStalenessDeadline(string? lastActivityAt, int thresholdMinutes)
    {
        if (!string.IsNullOrWhiteSpace(lastActivityAt) && DateTime.TryParse(lastActivityAt, out var activityTime))
        {
            return activityTime.AddMinutes(thresholdMinutes).ToString("o");
        }
        return DateTime.UtcNow.ToString("o");
    }

    /// <summary>
    /// Compute a human-readable age string from an ISO 8601 or SQLite datetime timestamp.
    /// </summary>
    private static string ComputeAge(string? activityAt)
    {
        if (!string.IsNullOrWhiteSpace(activityAt) && DateTime.TryParse(activityAt, out var activityTime))
        {
            var age = DateTime.UtcNow - activityTime.ToUniversalTime();
            if (age.TotalMinutes < 1) return "less than a minute";
            if (age.TotalMinutes < 2) return "about 1 minute";
            if (age.TotalMinutes < 60) return $"about {(int)age.TotalMinutes} minutes";
            if (age.TotalHours < 2) return "about 1 hour";
            if (age.TotalHours < 24) return $"about {(int)age.TotalHours} hours";
            if (age.TotalDays < 2) return "about 1 day";
            return $"about {(int)age.TotalDays} days";
        }
        return "unknown";
    }

    // ── Stale Worker Sweep ────────────────────────────────────────────────

    public async Task<StaleWorkerSweepResult> SweepStaleWorkersAsync(StaleSweepOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        var conditions = new List<StaleWorkerCondition>();
        var sweptAt = DateTime.UtcNow.ToString("o");
        var limit = Math.Clamp(options.Limit, 1, 200);

        var piFilter = !string.IsNullOrWhiteSpace(options.ProjectId);
        var taskFilter = options.TaskId is not null;

        await AddStaleAckConditionsAsync(conn, options, conditions, piFilter, taskFilter);
        await AddStaleRunningConditionsAsync(conn, options, conditions, piFilter, taskFilter);
        await AddMissingReviewerCompletionAsync(conn, options, conditions, piFilter, taskFilter);
        await AddCompletionNotTerminalizedAsync(conn, options, conditions, piFilter, taskFilter);
        await AddOrphanedOrchestratorLeaseAsync(conn, options, conditions, piFilter, taskFilter);
        await AddDuplicateAssignmentForRunAsync(conn, options, conditions, piFilter, taskFilter);

        return new StaleWorkerSweepResult
        {
            StaleCount = conditions.Count,
            Conditions = conditions
                .OrderBy(c => c.Severity == "critical" ? 0 : c.Severity == "warning" ? 1 : 2)
                .ThenByDescending(c => c.LastActivityAt)
                .Take(limit)
                .ToList(),
            SweptAt = sweptAt,
        };
    }

    private static async Task AddStaleAckConditionsAsync(
        SqliteConnection conn, StaleSweepOptions options, List<StaleWorkerCondition> conditions,
        bool piFilter, bool taskFilter)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>
        {
            "wa.state = 'ack'",
            $"wa.created_at <= datetime('now', '-{options.AckStaleThresholdMinutes} minutes')",
            "wc.id IS NULL"
        };
        AppendStaleFilters(cmd, where, piFilter, taskFilter, options);

        cmd.CommandText = $"""
            SELECT wa.id, wa.worker_identity, wa.run_id, wa.project_id, wa.task_id,
                   wa.role, wa.state, wa.profile_identity, wa.created_at
            FROM worker_assignments wa
            LEFT JOIN worker_checkpoints wc ON wc.assignment_id = wa.id AND wc.checkpoint_type IN ('completion', 'failure')
            WHERE {string.Join(" AND ", where)}
            ORDER BY wa.created_at ASC
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var assignmentId = reader.GetInt32(0);
            var workerId = reader.GetString(1);
            var runId = reader.GetString(2);
            var projectId = reader.GetString(3);
            var taskId = reader.IsDBNull(4) ? null : (int?)reader.GetInt32(4);
            var role = reader.GetString(5);
            var state = reader.GetString(6);
            var profileId = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var createdAt = reader.GetString(8);

            conditions.Add(new StaleWorkerCondition
            {
                StaleSignature = $"stale_ack:{projectId}:{runId}",
                Classification = StaleClassificationTypes.StaleAck,
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                AssignmentId = assignmentId,
                WorkerIdentity = workerId,
                ProfileIdentity = profileId,
                WorkerRole = role,
                CurrentState = state,
                LastActivityAt = createdAt,
                StalenessDeadline = ComputeStalenessDeadline(createdAt, options.AckStaleThresholdMinutes),
                Age = ComputeAge(createdAt),
                StateReason = $"Assignment #{assignmentId} for role '{role}' was leased but never acknowledged or started. "
                    + $"Created at {createdAt}.",
                SuggestedNextAction = $"Release or expire assignment #{assignmentId}. "
                    + "If the worker runtime is unavailable, quarantine the worker slot.",
                EvidenceIds = $"[{assignmentId}]",
                Severity = "warning",
                DetectedAt = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    private static async Task AddStaleRunningConditionsAsync(
        SqliteConnection conn, StaleSweepOptions options, List<StaleWorkerCondition> conditions,
        bool piFilter, bool taskFilter)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>
        {
            "wa.state = 'running'",
            $"wa.created_at <= datetime('now', '-{options.RunningStaleThresholdMinutes} minutes')",
            "latest_wc.latest_checkpoint_at IS NULL"
        };
        AppendStaleFilters(cmd, where, piFilter, taskFilter, options);

        cmd.CommandText = $"""
            SELECT wa.id, wa.worker_identity, wa.run_id, wa.project_id, wa.task_id,
                   wa.role, wa.state, wa.profile_identity, wa.created_at
            FROM worker_assignments wa
            LEFT JOIN (
                SELECT assignment_id, MAX(created_at) AS latest_checkpoint_at
                FROM worker_checkpoints
                GROUP BY assignment_id
            ) latest_wc ON latest_wc.assignment_id = wa.id
            WHERE {string.Join(" AND ", where)}
            ORDER BY wa.created_at ASC
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var assignmentId = reader.GetInt32(0);
            var workerId = reader.GetString(1);
            var runId = reader.GetString(2);
            var projectId = reader.GetString(3);
            var taskId = reader.IsDBNull(4) ? null : (int?)reader.GetInt32(4);
            var role = reader.GetString(5);
            var state = reader.GetString(6);
            var profileId = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var createdAt = reader.GetString(8);

            conditions.Add(new StaleWorkerCondition
            {
                StaleSignature = $"stale_running:{projectId}:{runId}",
                Classification = StaleClassificationTypes.StaleRunning,
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                AssignmentId = assignmentId,
                WorkerIdentity = workerId,
                ProfileIdentity = profileId,
                WorkerRole = role,
                CurrentState = state,
                LastActivityAt = createdAt,
                StalenessDeadline = ComputeStalenessDeadline(createdAt, options.RunningStaleThresholdMinutes),
                Age = ComputeAge(createdAt),
                StateReason = $"Assignment #{assignmentId} is 'running' but has produced no checkpoints or completion packets. "
                    + $"Started at {createdAt}.",
                SuggestedNextAction = $"Investigate worker #{workerId} — it may be stalled or the runtime has lost track. "
                    + "Expire or abort the assignment if the worker is unrecoverable.",
                EvidenceIds = $"[{assignmentId}]",
                Severity = "critical",
                DetectedAt = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    private static async Task AddMissingReviewerCompletionAsync(
        SqliteConnection conn, StaleSweepOptions options, List<StaleWorkerCondition> conditions,
        bool piFilter, bool taskFilter)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>
        {
            "rr.verdict IS NULL",
            $"rr.requested_at <= datetime('now', '-{options.ReviewerStaleThresholdMinutes} minutes')"
        };
        if (piFilter)
        {
            where.Add("t.project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }
        if (taskFilter)
        {
            where.Add("rr.task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId!.Value);
        }

        cmd.CommandText = $"""
            SELECT rr.id, rr.task_id, rr.round_number, rr.branch, rr.requested_at,
                   t.project_id, t.title,
                   wa_reviewer.id, wa_reviewer.run_id, wa_reviewer.state,
                   wa_reviewer.worker_identity, wa_reviewer.profile_identity,
                   wa_reviewer.role, wa_reviewer.created_at
            FROM review_rounds rr
            JOIN tasks t ON t.id = rr.task_id
            LEFT JOIN worker_assignments wa_reviewer
                ON wa_reviewer.task_id = rr.task_id
                AND wa_reviewer.role = 'reviewer'
                AND wa_reviewer.state NOT IN ('completed', 'failed', 'expired')
            WHERE {string.Join(" AND ", where)}
            ORDER BY rr.requested_at ASC
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var roundId = reader.GetInt32(0);
            var taskId = reader.GetInt32(1);
            var roundNumber = reader.GetInt32(2);
            var branch = reader.GetString(3);
            var requestedAt = reader.GetString(4);
            var projectId = reader.GetString(5);
            var title = reader.GetString(6);
            var reviewerAssignmentId = reader.IsDBNull(7) ? null : (int?)reader.GetInt32(7);
            var reviewerRunId = reader.IsDBNull(8) ? null : reader.GetString(8);
            var reviewerState = reader.IsDBNull(9) ? null : reader.GetString(9);
            var reviewerWorkerId = reader.IsDBNull(10) ? null : reader.GetString(10);
            var reviewerProfileId = reader.IsDBNull(11) ? string.Empty : reader.GetString(11);
            var reviewerRole = reader.IsDBNull(12) ? null : reader.GetString(12);
            var reviewerCreatedAt = reader.IsDBNull(13) ? null : reader.GetString(13);

            var evidenceIds = new List<int> { roundId };
            var runId = reviewerRunId;
            var assignmentId = reviewerAssignmentId;
            var workerId = reviewerWorkerId;
            var profileId = reviewerProfileId;
            var workerRole = reviewerRole;
            var currentState = reviewerState;
            var lastActivityAt = reviewerCreatedAt ?? requestedAt;

            if (reviewerAssignmentId is not null)
                evidenceIds.Add(reviewerAssignmentId.Value);

            conditions.Add(new StaleWorkerCondition
            {
                StaleSignature = $"missing_reviewer_completion:{projectId}:{taskId}:r{roundNumber}",
                Classification = StaleClassificationTypes.MissingReviewerCompletion,
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                AssignmentId = assignmentId,
                ReviewRoundId = roundId,
                WorkerIdentity = workerId,
                ProfileIdentity = profileId,
                WorkerRole = workerRole,
                CurrentState = currentState,
                LastActivityAt = lastActivityAt,
                StalenessDeadline = ComputeStalenessDeadline(lastActivityAt, options.ReviewerStaleThresholdMinutes),
                Age = ComputeAge(lastActivityAt),
                StateReason = $"Review round R{roundNumber} for task #{taskId} ({title}) on branch '{branch}' has no verdict. "
                    + (reviewerAssignmentId is not null
                        ? $"Reviewer assignment #{reviewerAssignmentId} is '{reviewerState}' — no completion packet received."
                        : "No reviewer assignment found for this review round."),
                SuggestedNextAction = reviewerAssignmentId is not null
                    ? $"Expire reviewer assignment #{reviewerAssignmentId} and launch a fresh reviewer for round R{roundNumber}."
                    : $"Launch a reviewer directly for review round R{roundNumber} on task #{taskId}.",
                EvidenceIds = JsonSerializer.Serialize(evidenceIds),
                Severity = reviewerAssignmentId is null ? "critical" : "warning",
                DetectedAt = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    private static async Task AddCompletionNotTerminalizedAsync(
        SqliteConnection conn, StaleSweepOptions options, List<StaleWorkerCondition> conditions,
        bool piFilter, bool taskFilter)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>
        {
            "wa.state NOT IN ('completed', 'failed', 'expired')"
        };
        AppendStaleFilters(cmd, where, piFilter, taskFilter, options);

        cmd.CommandText = $"""
            SELECT wa.id, wa.worker_identity, wa.run_id, wa.project_id, wa.task_id,
                   wa.role, wa.state, wa.profile_identity, wa.created_at,
                   wc.id, wc.checkpoint_type, wc.created_at
            FROM worker_assignments wa
            JOIN worker_checkpoints wc ON wc.assignment_id = wa.id
            WHERE {string.Join(" AND ", where)}
              AND wc.checkpoint_type IN ('completion', 'failure')
            ORDER BY wc.created_at ASC
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var assignmentId = reader.GetInt32(0);
            var workerId = reader.GetString(1);
            var runId = reader.GetString(2);
            var projectId = reader.GetString(3);
            var taskId = reader.IsDBNull(4) ? null : (int?)reader.GetInt32(4);
            var role = reader.GetString(5);
            var state = reader.GetString(6);
            var profileId = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var waCreatedAt = reader.GetString(8);
            var checkpointId = reader.GetInt32(9);
            var checkpointType = reader.GetString(10);
            var checkpointCreatedAt = reader.GetString(11);

            conditions.Add(new StaleWorkerCondition
            {
                StaleSignature = $"completion_not_terminalized:{projectId}:{runId}",
                Classification = StaleClassificationTypes.CompletionNotTerminalized,
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                AssignmentId = assignmentId,
                WorkerIdentity = workerId,
                ProfileIdentity = profileId,
                WorkerRole = role,
                CurrentState = state,
                LastActivityAt = checkpointCreatedAt,
                StalenessDeadline = ComputeStalenessDeadline(checkpointCreatedAt, options.CompletionTerminalizedStaleThresholdMinutes),
                Age = ComputeAge(checkpointCreatedAt),
                StateReason = $"Assignment #{assignmentId} received a '{checkpointType}' checkpoint (#{checkpointId}) at {checkpointCreatedAt}, "
                    + $"but assignment state remains '{state}' instead of a terminal state.",
                SuggestedNextAction = $"Transition assignment #{assignmentId} to 'completed' or 'failed' to release the worker. "
                    + "The work was reported finished but the assignment was never terminalized.",
                EvidenceIds = $"[{assignmentId}, {checkpointId}]",
                Severity = "warning",
                DetectedAt = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    private static async Task AddOrphanedOrchestratorLeaseAsync(
        SqliteConnection conn, StaleSweepOptions options, List<StaleWorkerCondition> conditions,
        bool piFilter, bool taskFilter)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>
        {
            $"ol.state IN ({string.Join(", ", WorkerPoolStates.OrchLeaseNonTerminalStates.Select(s => $"'{s}'"))})",
            $"ol.created_at <= datetime('now', '-{options.OrchestratorStaleThresholdMinutes} minutes')"
        };
        if (piFilter)
        {
            where.Add("ol.project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }
        if (taskFilter)
        {
            where.Add("ol.task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId!.Value);
        }

        cmd.CommandText = $"""
            SELECT ol.id, ol.lease_id, ol.project_id, ol.task_id, ol.orchestrator_identity,
                   ol.profile_identity, ol.state, ol.objective, ol.created_at,
                   COALESCE(child_count.child_count, 0) AS child_count
            FROM orchestrator_leases ol
            LEFT JOIN (
                SELECT wa.project_id, COUNT(*) AS child_count, MAX(wa.created_at) AS latest_child_at
                FROM worker_assignments wa
                WHERE wa.state NOT IN ('completed', 'failed', 'expired')
                GROUP BY wa.project_id
            ) child_count ON child_count.project_id = ol.project_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY ol.created_at ASC
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var leaseId = reader.GetInt32(0);
            var leaseIdStr = reader.GetString(1);
            var projectId = reader.GetString(2);
            var taskId = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3);
            var orchId = reader.GetString(4);
            var profileId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var state = reader.GetString(6);
            var objective = reader.IsDBNull(7) ? null : reader.GetString(7);
            var createdAt = reader.GetString(8);
            var childCount = reader.GetInt64(9);

            var severity = childCount == 0 ? "critical" : "warning";
            var reason = childCount == 0
                ? $"Orchestrator lease #{leaseId} ({leaseIdStr}) has been {state} since {createdAt} with no child assignment progress. "
                    + "The orchestrator may be stalled or the project has no active work."
                : $"Orchestrator lease #{leaseId} ({leaseIdStr}) has been {state} since {createdAt} with {childCount} active child assignments. "
                    + "The orchestrator may need attention to finalize downstream work.";

            conditions.Add(new StaleWorkerCondition
            {
                StaleSignature = $"orphaned_orchestrator:{projectId}:{leaseIdStr}",
                Classification = StaleClassificationTypes.OrphanedOrchestratorLease,
                ProjectId = projectId,
                TaskId = taskId,
                OrchestratorLeaseId = leaseId,
                WorkerIdentity = orchId,
                ProfileIdentity = profileId,
                WorkerRole = "project_orchestrator",
                CurrentState = state,
                LastActivityAt = createdAt,
                StalenessDeadline = ComputeStalenessDeadline(createdAt, options.OrchestratorStaleThresholdMinutes),
                Age = ComputeAge(createdAt),
                StateReason = reason,
                SuggestedNextAction = childCount == 0
                    ? $"Drain or expire orchestrator lease #{leaseId}. "
                        + "If the project still needs orchestration, reconcile and relaunch."
                    : $"Check orchestrator lease #{leaseId} — it may need to finalize or release child assignments.",
                EvidenceIds = $"[{leaseId}]",
                Severity = severity,
                DetectedAt = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    private static async Task AddDuplicateAssignmentForRunAsync(
        SqliteConnection conn, StaleSweepOptions options, List<StaleWorkerCondition> conditions,
        bool piFilter, bool taskFilter)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>
        {
            "wa.state NOT IN ('completed', 'failed', 'expired')"
        };
        AppendStaleFilters(cmd, where, piFilter, taskFilter, options);

        cmd.CommandText = $"""
            SELECT wa.run_id, wa.project_id, COUNT(*) AS cnt,
                   GROUP_CONCAT(wa.id) AS assignment_ids,
                   GROUP_CONCAT(wa.worker_identity) AS worker_ids,
                   GROUP_CONCAT(wa.state) AS states,
                   MAX(wa.created_at) AS latest_created
            FROM worker_assignments wa
            WHERE {string.Join(" AND ", where)}
            GROUP BY wa.run_id
            HAVING COUNT(*) > 1
            ORDER BY latest_created DESC
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var runId = reader.GetString(0);
            var projectId = reader.GetString(1);
            var count = reader.GetInt64(2);
            var assignmentIds = reader.GetString(3);
            var workerIds = reader.GetString(4);
            var states = reader.GetString(5);
            var latestCreated = reader.GetString(6);

            conditions.Add(new StaleWorkerCondition
            {
                StaleSignature = $"duplicate_assignment:{projectId}:{runId}",
                Classification = StaleClassificationTypes.DuplicateAssignmentForRun,
                ProjectId = projectId,
                RunId = runId,
                AssignmentId = null,
                WorkerIdentity = workerIds.Split(',')[0],
                CurrentState = states,
                LastActivityAt = latestCreated,
                StalenessDeadline = ComputeStalenessDeadline(latestCreated, options.DuplicateAssignmentStaleThresholdMinutes),
                Age = ComputeAge(latestCreated),
                StateReason = $"Run '{runId}' has {count} non-terminal assignments holding capacity: [{assignmentIds}]. "
                    + $"Worker identities: [{workerIds}]. States: [{states}]. Only one assignment per run should be active.",
                SuggestedNextAction = $"Expire all but the most recent assignment for run '{runId}'. "
                    + "These duplicate assignments are leaking pool capacity.",
                EvidenceIds = $"[{assignmentIds}]",
                Severity = "critical",
                DetectedAt = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    private static void AppendStaleFilters(
        SqliteCommand cmd, List<string> where, bool piFilter, bool taskFilter, StaleSweepOptions options)
    {
        if (piFilter)
        {
            where.Add("wa.project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }
        if (taskFilter)
        {
            where.Add("wa.task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId!.Value);
        }
    }

    private static async Task<bool> LaneCanAcceptLeaseAsync(SqliteConnection conn, WorkerPoolMember worker, string assignmentRole)
    {
        if (string.IsNullOrWhiteSpace(worker.ProfileIdentity))
            return true;

        var workerRole = !string.IsNullOrWhiteSpace(worker.WorkerRole)
            ? worker.WorkerRole!
            : assignmentRole;

        await using (var laneCmd = conn.CreateCommand())
        {
            laneCmd.CommandText = """
                SELECT capacity, status
                FROM worker_pool_lanes
                WHERE profile_identity = @profileIdentity
                  AND worker_role = @workerRole
                """;
            laneCmd.Parameters.AddWithValue("@profileIdentity", worker.ProfileIdentity);
            laneCmd.Parameters.AddWithValue("@workerRole", workerRole);

            await using var reader = await laneCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return true;

            var capacity = reader.GetInt32(0);
            var status = reader.GetString(1);
            if (status != WorkerPoolStates.LaneActive)
                return false;

            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = """
                SELECT COUNT(*)
                FROM worker_assignments wa
                LEFT JOIN worker_pool_members wm ON wa.worker_identity = wm.worker_identity
                WHERE COALESCE(NULLIF(wa.profile_identity, ''), wm.profile_identity) = @profileIdentity
                  AND COALESCE(wm.worker_role, wa.role) = @workerRole
                  AND wa.state NOT IN ('completed', 'failed', 'expired')
                """;
            countCmd.Parameters.AddWithValue("@profileIdentity", worker.ProfileIdentity);
            countCmd.Parameters.AddWithValue("@workerRole", workerRole);
            var active = Convert.ToInt32(await countCmd.ExecuteScalarAsync() ?? 0L);

            return active < capacity;
        }
    }

    // ── Orchestrator Leases ────────────────────────────────────────────────

    private const string OrchLeaseColumns =
        "id, lease_id, lease_kind, scope_type, project_id, channel_id, task_id, workstream_handle, " +
        "objective, lease_owner, orchestrator_identity, profile_identity, display_name, " +
        "capability_metadata, state, requested_duration_seconds, actual_duration_seconds, " +
        "lease_expires_at, renewal_policy, drain_policy, " +
        "agent_instance_id, adapter_instance_id, session_id, run_id, last_seen_at, " +
        "latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, metadata, " +
        "created_at, updated_at";

    private static OrchestratorLease ReadOrchLease(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        LeaseId = reader.GetString(1),
        LeaseKind = reader.GetString(2),
        ScopeType = reader.GetString(3),
        ProjectId = reader.GetString(4),
        ChannelId = reader.IsDBNull(5) ? null : reader.GetString(5),
        TaskId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
        WorkstreamHandle = reader.IsDBNull(7) ? null : reader.GetString(7),
        Objective = reader.IsDBNull(8) ? null : reader.GetString(8),
        LeaseOwner = reader.GetString(9),
        OrchestratorIdentity = reader.GetString(10),
        ProfileIdentity = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
        DisplayName = reader.IsDBNull(12) ? null : reader.GetString(12),
        CapabilityMetadata = reader.IsDBNull(13) ? null : reader.GetString(13),
        State = reader.GetString(14),
        RequestedDurationSeconds = reader.IsDBNull(15) ? null : reader.GetInt32(15),
        ActualDurationSeconds = reader.IsDBNull(16) ? null : reader.GetInt32(16),
        LeaseExpiresAt = reader.IsDBNull(17) ? null : reader.GetString(17),
        RenewalPolicy = reader.IsDBNull(18) ? WorkerPoolStates.RenewalPolicyDeny : reader.GetString(18),
        DrainPolicy = reader.IsDBNull(19) ? WorkerPoolStates.DrainPolicyGraceful : reader.GetString(19),
        AgentInstanceId = reader.IsDBNull(20) ? null : reader.GetString(20),
        AdapterInstanceId = reader.IsDBNull(21) ? null : reader.GetString(21),
        SessionId = reader.IsDBNull(22) ? null : reader.GetString(22),
        RunId = reader.IsDBNull(23) ? null : reader.GetString(23),
        LastSeenAt = reader.IsDBNull(24) ? null : reader.GetString(24),
        LatestCheckpointId = reader.IsDBNull(25) ? null : reader.GetInt32(25),
        CleanupEvidence = reader.IsDBNull(26) ? null : reader.GetString(26),
        CleanupRecordedAt = reader.IsDBNull(27) ? null : reader.GetString(27),
        Metadata = reader.IsDBNull(28) ? null : reader.GetString(28),
        CreatedAt = DateTime.Parse(reader.GetString(29)),
        UpdatedAt = DateTime.Parse(reader.GetString(30)),
    };

    public async Task<OrchestratorLease> CreateOrchestratorLeaseAsync(CreateOrchestratorLeaseInput input)
    {
        if (input.RequestedDurationSeconds is <= 0)
            throw new ArgumentException("requested_duration_seconds must be greater than zero when provided", nameof(input));

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // Find an available pool member matching profile/capability filters
            var candidates = await FindAvailableWorkersAsync(
                conn, input.RequiredCapabilities, input.ProfileIdentity, workerRole: "project_orchestrator");

            string orchestratorIdentity;
            WorkerPoolMember? selectedWorker = null;

            if (!string.IsNullOrWhiteSpace(input.PreferredOrchestratorIdentity))
            {
                // A preferred concrete identity is an explicit selection, not a hint:
                // fail closed unless that member satisfies the same project-orchestrator
                // role/profile/capability filter as automatic selection. A preferred
                // coder/reviewer worker must not be silently promoted or bypassed.
                if (!candidates.Contains(input.PreferredOrchestratorIdentity, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        "Preferred orchestrator is not an available project_orchestrator matching profile/capability criteria.");

                selectedWorker = await GetMemberByConnAsync(conn, input.PreferredOrchestratorIdentity);
            }

            if (selectedWorker is null)
            {
                foreach (var candidateId in candidates)
                {
                    var candidate = await GetMemberByConnAsync(conn, candidateId);
                    if (candidate is not null)
                    {
                        selectedWorker = candidate;
                        break;
                    }
                }
            }

            if (selectedWorker is null)
                throw new InvalidOperationException(
                    "No available pool member matching orchestrator profile/capability criteria.");

            orchestratorIdentity = selectedWorker.WorkerIdentity;

            // Mark the pool member busy
            await SetMemberStatusByConnAsync(conn, orchestratorIdentity, WorkerPoolStates.MemberBusy);

            // Compute lease_expires_at
            string? expiresAtExpr = input.RequestedDurationSeconds is not null
                ? $"datetime('now', '+{input.RequestedDurationSeconds.Value} seconds')"
                : null;

            // Build the INSERT with computed lease_id and optional expires
            var insertCols = new List<string>
            {
                "lease_id", "lease_kind", "scope_type", "project_id", "channel_id",
                "task_id", "workstream_handle", "objective", "lease_owner",
                "orchestrator_identity", "profile_identity", "display_name",
                "capability_metadata", "state", "requested_duration_seconds",
                "lease_expires_at", "renewal_policy", "drain_policy",
                "agent_instance_id", "adapter_instance_id", "session_id", "run_id"
            };
            var insertVals = new List<string>
            {
                "@leaseId", "@leaseKind", "@scopeType", "@projectId", "@channelId",
                "@taskId", "@workstreamHandle", "@objective", "@leaseOwner",
                "@orchestratorIdentity", "@profileIdentity", "@displayName",
                "@capabilityMetadata", "@state", "@requestedDurationSeconds",
                expiresAtExpr ?? "NULL", "@renewalPolicy", "@drainPolicy",
                "@agentInstanceId", "@adapterInstanceId", "@sessionId", "@runId"
            };

            var leaseId = $"{orchestratorIdentity}:{input.ProjectId}:{Guid.NewGuid():N}";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO orchestrator_leases ({string.Join(", ", insertCols)})
                VALUES ({string.Join(", ", insertVals)})
                RETURNING {OrchLeaseColumns}
                """;

            cmd.Parameters.AddWithValue("@leaseId", leaseId);
            cmd.Parameters.AddWithValue("@leaseKind", WorkerPoolStates.LeaseKindProjectOrchestrator);
            cmd.Parameters.AddWithValue("@scopeType", input.ScopeType);
            cmd.Parameters.AddWithValue("@projectId", input.ProjectId);
            cmd.Parameters.AddWithValue("@channelId", (object?)input.ChannelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@taskId", (object?)input.TaskId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@workstreamHandle", (object?)input.WorkstreamHandle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@objective", (object?)input.Objective ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@leaseOwner", input.LeaseOwner);
            cmd.Parameters.AddWithValue("@orchestratorIdentity", orchestratorIdentity);
            cmd.Parameters.AddWithValue("@profileIdentity", selectedWorker.ProfileIdentity);
            cmd.Parameters.AddWithValue("@displayName", (object?)selectedWorker.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@capabilityMetadata", (object?)selectedWorker.Capabilities ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@state", WorkerPoolStates.OrchLeaseLeased);
            cmd.Parameters.AddWithValue("@requestedDurationSeconds",
                (object?)input.RequestedDurationSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@renewalPolicy", input.RenewalPolicy);
            cmd.Parameters.AddWithValue("@drainPolicy", input.DrainPolicy);
            cmd.Parameters.AddWithValue("@agentInstanceId",
                (object?)selectedWorker.AgentInstanceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@adapterInstanceId",
                (object?)selectedWorker.AdapterInstanceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sessionId",
                (object?)selectedWorker.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@runId", (object?)input.RunId ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var lease = ReadOrchLease(reader);
            await reader.CloseAsync();

            await tx.CommitAsync();
            return lease;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<OrchestratorLease?> GetOrchestratorLeaseAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {OrchLeaseColumns}
            FROM orchestrator_leases
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadOrchLease(reader) : null;
    }

    public async Task<OrchestratorLease?> GetOrchestratorLeaseByLeaseIdAsync(string leaseId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {OrchLeaseColumns}
            FROM orchestrator_leases
            WHERE lease_id = @leaseId
            """;
        cmd.Parameters.AddWithValue("@leaseId", leaseId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadOrchLease(reader) : null;
    }

    public async Task<List<OrchestratorLease>> ListOrchestratorLeasesAsync(OrchestratorLeaseListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }
        if (!string.IsNullOrWhiteSpace(options.ScopeType))
        {
            where.Add("scope_type = @scopeType");
            cmd.Parameters.AddWithValue("@scopeType", options.ScopeType);
        }
        if (!string.IsNullOrWhiteSpace(options.OrchestratorIdentity))
        {
            where.Add("orchestrator_identity = @orchestratorIdentity");
            cmd.Parameters.AddWithValue("@orchestratorIdentity", options.OrchestratorIdentity);
        }
        if (!string.IsNullOrWhiteSpace(options.State))
        {
            where.Add("state = @state");
            cmd.Parameters.AddWithValue("@state", options.State);
        }
        if (!string.IsNullOrWhiteSpace(options.LeaseKind))
        {
            where.Add("lease_kind = @leaseKind");
            cmd.Parameters.AddWithValue("@leaseKind", options.LeaseKind);
        }
        if (!options.IncludeTerminal)
        {
            where.Add($"state NOT IN ({string.Join(", ", WorkerPoolStates.OrchLeaseTerminalStates.Select(s => $"'{s}'"))})");
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT {OrchLeaseColumns}
            FROM orchestrator_leases
            {whereClause}
            ORDER BY updated_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<OrchestratorLease>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadOrchLease(reader));
        return results;
    }

    public async Task<OrchestratorLease?> TransitionOrchestratorLeaseAsync(TransitionOrchestratorLeaseInput input)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using var getCmd = conn.CreateCommand();
            getCmd.CommandText = $"""
                SELECT {OrchLeaseColumns}
                FROM orchestrator_leases
                WHERE id = @id
                """;
            getCmd.Parameters.AddWithValue("@id", input.LeaseInternalId);

            await using var getReader = await getCmd.ExecuteReaderAsync();
            if (!await getReader.ReadAsync())
            {
                await tx.CommitAsync();
                return null;
            }
            var lease = ReadOrchLease(getReader);
            await getReader.CloseAsync();

            if (!IsValidOrchLeaseTransition(lease.State, input.NewState))
            {
                await tx.CommitAsync();
                return null;
            }

            var setClauses = new List<string> { "state = @newState", "updated_at = datetime('now')" };

            // Compute actual duration on terminal transition
            if (WorkerPoolStates.IsOrchLeaseTerminal(input.NewState))
            {
                setClauses.Add("actual_duration_seconds = CAST((julianday('now') - julianday(created_at)) * 86400 AS INTEGER)");
            }

            // Record cleanup evidence if provided on terminal transition
            if (!string.IsNullOrWhiteSpace(input.Evidence) && WorkerPoolStates.IsOrchLeaseTerminal(input.NewState))
            {
                setClauses.Add("cleanup_evidence = @evidence");
                setClauses.Add("cleanup_recorded_at = datetime('now')");
            }

            // Record metadata if provided
            if (!string.IsNullOrWhiteSpace(input.Metadata))
            {
                setClauses.Add("metadata = @metadata");
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE orchestrator_leases
                SET {string.Join(", ", setClauses)}
                WHERE id = @id
                RETURNING {OrchLeaseColumns}
                """;
            cmd.Parameters.AddWithValue("@id", input.LeaseInternalId);
            cmd.Parameters.AddWithValue("@newState", input.NewState);

            if (!string.IsNullOrWhiteSpace(input.Evidence) && WorkerPoolStates.IsOrchLeaseTerminal(input.NewState))
                cmd.Parameters.AddWithValue("@evidence", input.Evidence);
            if (!string.IsNullOrWhiteSpace(input.Metadata))
                cmd.Parameters.AddWithValue("@metadata", input.Metadata);

            await using var reader = await cmd.ExecuteReaderAsync();
            var updated = await reader.ReadAsync() ? ReadOrchLease(reader) : null;
            await reader.CloseAsync();

            // If terminal, release the pool member back to available
            if (WorkerPoolStates.IsOrchLeaseTerminal(input.NewState) && updated is not null)
            {
                await SetMemberStatusByConnAsync(conn, updated.OrchestratorIdentity, WorkerPoolStates.MemberAvailable);
            }

            await tx.CommitAsync();
            return updated;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<OrchestratorLease?> RecordOrchestratorLeaseCleanupAsync(int leaseId, string evidenceJson)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE orchestrator_leases
            SET cleanup_evidence = @evidence,
                cleanup_recorded_at = datetime('now'),
                updated_at = datetime('now')
            WHERE id = @id
              AND state IN ({string.Join(", ", WorkerPoolStates.OrchLeaseTerminalStates.Select(s => $"'{s}'"))})
            RETURNING {OrchLeaseColumns}
            """;
        cmd.Parameters.AddWithValue("@id", leaseId);
        cmd.Parameters.AddWithValue("@evidence", evidenceJson);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadOrchLease(reader) : null;
    }

    public async Task<int> ReconcileStaleOrchestratorLeasesAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // Find non-terminal leases where lease_expires_at has passed
            var staleLeaseIds = new List<int>();
            await using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = $"""
                    SELECT id, orchestrator_identity
                    FROM orchestrator_leases
                    WHERE state IN ({string.Join(", ", WorkerPoolStates.OrchLeaseNonTerminalStates.Select(s => $"'{s}'"))})
                      AND lease_expires_at IS NOT NULL
                      AND datetime(lease_expires_at) < datetime('now')
                    """;
                await using var reader = await findCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    staleLeaseIds.Add(reader.GetInt32(0));
            }

            // Also find non-terminal leases whose pool member is stale (heartbeat-based)
            var heartbeatStaleIds = new List<(int LeaseId, string OrchestratorIdentity)>();
            await using (var hbCmd = conn.CreateCommand())
            {
                hbCmd.CommandText = $"""
                    SELECT ol.id, ol.orchestrator_identity
                    FROM orchestrator_leases ol
                    JOIN worker_pool_members wm ON ol.orchestrator_identity = wm.worker_identity
                    WHERE ol.state IN ({string.Join(", ", WorkerPoolStates.OrchLeaseNonTerminalStates.Select(s => $"'{s}'"))})
                      AND wm.stale_after_seconds IS NOT NULL
                      AND wm.last_heartbeat IS NOT NULL
                      AND datetime(wm.last_heartbeat, '+' || wm.stale_after_seconds || ' seconds') < datetime('now')
                    """;
                await using var reader = await hbCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    heartbeatStaleIds.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            var affected = 0;

            // Expire time-based stale leases
            foreach (var leaseId in staleLeaseIds)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    UPDATE orchestrator_leases
                    SET state = '{WorkerPoolStates.OrchLeaseExpired}',
                        actual_duration_seconds = CAST((julianday('now') - julianday(created_at)) * 86400 AS INTEGER),
                        updated_at = datetime('now')
                    WHERE id = @id
                      AND state IN ({string.Join(", ", WorkerPoolStates.OrchLeaseNonTerminalStates.Select(s => $"'{s}'"))})
                    RETURNING orchestrator_identity
                    """;
                cmd.Parameters.AddWithValue("@id", leaseId);
                var orchId = await cmd.ExecuteScalarAsync() as string;
                if (orchId is not null)
                {
                    await SetMemberStatusByConnAsync(conn, orchId, WorkerPoolStates.MemberAvailable);
                    affected++;
                }
            }

            // Degrade heartbeat-stale leases (distinct from time-expired)
            foreach (var (leaseId, orchId) in heartbeatStaleIds)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    UPDATE orchestrator_leases
                    SET state = '{WorkerPoolStates.OrchLeaseDegraded}',
                        actual_duration_seconds = CAST((julianday('now') - julianday(created_at)) * 86400 AS INTEGER),
                        updated_at = datetime('now')
                    WHERE id = @id
                      AND state IN ({string.Join(", ", WorkerPoolStates.OrchLeaseNonTerminalStates.Select(s => $"'{s}'"))})
                    RETURNING orchestrator_identity
                    """;
                cmd.Parameters.AddWithValue("@id", leaseId);
                var result = await cmd.ExecuteScalarAsync() as string;
                if (result is not null)
                {
                    // Don't permanently busy the profile — just release member
                    await SetMemberStatusByConnAsync(conn, result, WorkerPoolStates.MemberAvailable);
                    affected++;
                }
            }

            await tx.CommitAsync();
            return affected;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<PoolResidencyProjection>> GetPoolResidencyProjectionAsync(string projectId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        var projections = new List<PoolResidencyProjection>();

        // Active task-worker assignments for this project
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT wa.worker_identity, COALESCE(wa.profile_identity, wm.profile_identity),
                       wm.worker_role, wa.project_id, wm.channel_id, wa.task_id,
                       wa.state, wa.acquired_at, NULL AS expires_at, wm.agent_instance_id
                FROM worker_assignments wa
                LEFT JOIN worker_pool_members wm ON wa.worker_identity = wm.worker_identity
                WHERE wa.project_id = @projectId
                  AND wa.state NOT IN ('completed', 'failed', 'expired')
                """;
            cmd.Parameters.AddWithValue("@projectId", projectId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var workerId = reader.GetString(0);
                var profileId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var workerRole = reader.IsDBNull(2) ? null : reader.GetString(2);
                var state = reader.IsDBNull(6) ? null : reader.GetString(6);
                var startedAt = reader.IsDBNull(7) ? null : reader.GetString(7);
                var agentInstId = reader.IsDBNull(9) ? null : reader.GetString(9);

                // Determine residency kind based on lease_kind
                projections.Add(new PoolResidencyProjection
                {
                    WorkerIdentity = workerId,
                    ProfileIdentity = profileId,
                    WorkerRole = workerRole,
                    ResidencyKind = "task_worker_assignment",
                    ProjectId = projectId,
                    ChannelId = null,
                    TaskId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    State = state,
                    StartedAt = startedAt,
                    ExpiresAt = null,
                    AgentInstanceId = agentInstId,
                });
            }
        }

        // Active orchestrator leases for this project
        await using (var cmd = conn.CreateCommand())
        {
            var terminalFilter = string.Join(", ",
                WorkerPoolStates.OrchLeaseTerminalStates.Select(s => $"'{s}'"));
            cmd.CommandText = $"""
                SELECT orchestrator_identity, profile_identity,
                       NULL AS worker_role, project_id, channel_id, task_id,
                       state, created_at, lease_expires_at, agent_instance_id
                FROM orchestrator_leases
                WHERE project_id = @projectId
                  AND state NOT IN ({terminalFilter})
                """;
            cmd.Parameters.AddWithValue("@projectId", projectId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                projections.Add(new PoolResidencyProjection
                {
                    WorkerIdentity = reader.GetString(0),
                    ProfileIdentity = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    WorkerRole = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ResidencyKind = "orchestrator_lease",
                    ProjectId = reader.GetString(3),
                    ChannelId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    TaskId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    State = reader.IsDBNull(6) ? null : reader.GetString(6),
                    StartedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ExpiresAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                    AgentInstanceId = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }
        }

        // Pool members with bindings to this project (via agent_instance_id, channel_id, session_id)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT worker_identity, profile_identity, worker_role,
                       NULL AS project_id, channel_id, NULL AS task_id,
                       status, NULL AS started_at, NULL AS expires_at, agent_instance_id
                FROM worker_pool_members
                WHERE agent_instance_id IS NOT NULL
                """;
            // Note: We can't easily filter by project_id here since pool members
            // are project-agnostic; the binding/project correlation comes from
            // assignments and leases. This captures members with live bindings.

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var workerId = reader.GetString(0);
                // Only add if not already covered by an assignment or lease for this project
                if (projections.Any(p => p.WorkerIdentity == workerId))
                    continue;

                projections.Add(new PoolResidencyProjection
                {
                    WorkerIdentity = workerId,
                    ProfileIdentity = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    WorkerRole = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ResidencyKind = "gateway_binding",
                    ProjectId = null,
                    ChannelId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    TaskId = null,
                    State = reader.IsDBNull(6) ? null : reader.GetString(6),
                    StartedAt = null,
                    ExpiresAt = null,
                    AgentInstanceId = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }
        }

        return projections;
    }

    private static bool IsValidOrchLeaseTransition(string current, string next)
    {
        if (current == next)
            return true;

        if (!WorkerPoolStates.ValidOrchLeaseStates.Contains(next))
            return false;

        // Terminal -> anything is invalid
        if (WorkerPoolStates.IsOrchLeaseTerminal(current))
            return false;

        return next switch
        {
            // Non-terminal transitions
            WorkerPoolStates.OrchLeaseLeased => current == WorkerPoolStates.OrchLeaseProposed,
            WorkerPoolStates.OrchLeaseActive => current == WorkerPoolStates.OrchLeaseLeased || current == WorkerPoolStates.OrchLeaseCheckpointWaiting,
            WorkerPoolStates.OrchLeaseCheckpointWaiting => current == WorkerPoolStates.OrchLeaseActive || current == WorkerPoolStates.OrchLeaseLeased,
            WorkerPoolStates.OrchLeaseDraining => current == WorkerPoolStates.OrchLeaseActive || current == WorkerPoolStates.OrchLeaseCheckpointWaiting || current == WorkerPoolStates.OrchLeaseLeased,
            // Terminal transitions — from any non-terminal
            WorkerPoolStates.OrchLeaseReleased => WorkerPoolStates.IsOrchLeaseNonTerminal(current),
            WorkerPoolStates.OrchLeaseQuarantined => WorkerPoolStates.IsOrchLeaseNonTerminal(current),
            WorkerPoolStates.OrchLeaseExpired => WorkerPoolStates.IsOrchLeaseNonTerminal(current),
            WorkerPoolStates.OrchLeaseDegraded => WorkerPoolStates.IsOrchLeaseNonTerminal(current),
            _ => false,
        };
    }

    // ── Pool Residency ────────────────────────────────────────────────
}