using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

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
        "status, last_heartbeat, agent_instance_id, channel_id, session_id, metadata, " +
        "created_at, updated_at";

    /// <summary>
    /// Column list for SELECT queries on worker_assignments (no alias prefix — for single-table use).
    /// Must match the order expected by <see cref="ReadAssignment"/>.
    /// </summary>
    private const string AssignmentColumns =
        "id, worker_identity, run_id, project_id, task_id, role, assigned_by, state, " +
        "latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at, " +
        "created_at, updated_at";

    /// <summary>
    /// Column list for JOIN queries on worker_assignments with alias 'wa'.
    /// Must match the order expected by <see cref="ReadAssignment"/> then followed by
    /// denormalized member fields (profile_identity, worker_role, agent_instance_id, channel_id).
    /// </summary>
    private const string AssignmentColumnsPrefixed =
        "wa.id, wa.worker_identity, wa.run_id, wa.project_id, wa.task_id, wa.role, wa.assigned_by, wa.state, " +
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
            INSERT INTO worker_pool_members (worker_identity, profile_identity, worker_role, display_name, capabilities, status, last_heartbeat, agent_instance_id, channel_id, session_id, metadata)
            VALUES (@workerIdentity, @profileIdentity, @workerRole, @displayName, @capabilities, @status, @lastHeartbeat, @agentInstanceId, @channelId, @sessionId, @metadata)
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

        // Update member to busy
        await SetMemberStatusByConnAsync(conn, input.PreferredWorkerIdentity!, WorkerPoolStates.MemberBusy);

        // Create assignment — include denormalized pool member fields for readback
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO worker_assignments (worker_identity, run_id, project_id, task_id, role, assigned_by, state, acquired_at)
            VALUES (@workerIdentity, @runId, @projectId, @taskId, @role, @assignedBy, 'ack', datetime('now'))
            RETURNING id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                      latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", input.PreferredWorkerIdentity!);
        cmd.Parameters.AddWithValue("@runId", input.RunId);
        cmd.Parameters.AddWithValue("@projectId", input.ProjectId);
        cmd.Parameters.AddWithValue("@taskId", (object?)input.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", input.Role);
        cmd.Parameters.AddWithValue("@assignedBy", input.AssignedBy);

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
            SELECT {AssignmentColumnsPrefixed}, wm.profile_identity, wm.worker_role, wm.agent_instance_id, wm.channel_id
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
            SELECT {AssignmentColumnsPrefixed}, wm.profile_identity, wm.worker_role, wm.agent_instance_id, wm.channel_id
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

            // Update assignment: link latest checkpoint and derive state
            string newState;
            switch (checkpointType)
            {
                case WorkerPoolStates.CheckpointCompletion:
                    newState = WorkerPoolStates.Completed;
                    break;
                case WorkerPoolStates.CheckpointFailure:
                    newState = WorkerPoolStates.Failed;
                    break;
                default:
                    newState = WorkerPoolStates.CheckpointWaiting;
                    break;
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
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO checkpoint_responses (checkpoint_id, assignment_id, run_id, response_type, payload)
                VALUES (@checkpointId, @assignmentId, @runId, @responseType, @payload)
                RETURNING id, checkpoint_id, assignment_id, run_id, response_type, payload, created_at
                """;
            cmd.Parameters.AddWithValue("@checkpointId", checkpointId);
            cmd.Parameters.AddWithValue("@assignmentId", (object?)assignmentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@runId", runId);
            cmd.Parameters.AddWithValue("@responseType", responseType);
            cmd.Parameters.AddWithValue("@payload", payload);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var response = ReadResponse(reader);
            await reader.CloseAsync();

            // If response is ack, transition assignment back to running
            if (responseType == WorkerPoolStates.ResponseAck && assignmentId is not null)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE worker_assignments
                    SET state = 'running', updated_at = datetime('now')
                    WHERE id = @assignmentId AND state = 'checkpoint_waiting'
                    """;
                updateCmd.Parameters.AddWithValue("@assignmentId", assignmentId.Value);
                await updateCmd.ExecuteNonQueryAsync();
            }

            // If response is abort, transition to expired
            if (responseType == WorkerPoolStates.ResponseAbort && assignmentId is not null)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE worker_assignments
                    SET state = 'expired', released_at = datetime('now'), updated_at = datetime('now')
                    WHERE id = @assignmentId AND state NOT IN ('completed', 'failed', 'expired')
                    """;
                updateCmd.Parameters.AddWithValue("@assignmentId", assignmentId.Value);
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
            cmd.CommandText = "SELECT state, count(*) FROM worker_assignments GROUP BY state";
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
                    return new LeaseWorkerResult
                    {
                        IsSuccess = true,
                        Assignment = assignment,
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
                    return new LeaseWorkerResult { IsSuccess = false, NoCapacity = record };
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
                    return new LeaseWorkerResult { IsSuccess = false, NoCapacity = record };
                }
            }

            // Find candidates
            var candidates = await FindAvailableWorkersAsync(conn, input.RequiredCapabilities, input.ProfileIdentity, input.WorkerRole);
            if (candidates.Count == 0)
            {
                // No matching workers — determine why
                var reasonCode = await DiagnoseNoMatchingWorkersAsync(conn, input);
                var stats = await CountCandidatesByStatusAsync(conn, input.ProfileIdentity, input.WorkerRole);
                var record = await InsertNoCapacityRequestAsync(conn, input, reasonCode, stats,
                    stats.Available == 0 && stats.Busy > 0
                        ? $"No available workers matching criteria. {stats.Busy} worker(s) are busy."
                        : stats.Available == 0 && stats.Quarantined > 0
                            ? $"No available workers matching criteria. {stats.Quarantined} worker(s) quarantined."
                            : stats.Total == 0
                                ? "No workers registered in the pool matching the requested role/profile/capabilities."
                                : $"No matching candidate workers available. Total candidates: {stats.Total}.");
                await tx.CommitAsync();
                return new LeaseWorkerResult { IsSuccess = false, NoCapacity = record };
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
                return new LeaseWorkerResult { IsSuccess = true, Assignment = assignment };
            }

            // All candidates failed — likely all busy now (race condition or conflicting constraints)
            var finalStats = await CountCandidatesByStatusAsync(conn, input.ProfileIdentity, input.WorkerRole);
            var finalRecord = await InsertNoCapacityRequestAsync(conn, input,
                WorkerPoolStates.NoCapacityAllBusy,
                finalStats,
                $"All {candidates.Count} matching workers became unavailable. {finalStats.Busy} busy, {finalStats.Available} available.");
            await tx.CommitAsync();
            return new LeaseWorkerResult { IsSuccess = false, NoCapacity = finalRecord };
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
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
    /// no_matching_worker, all_busy, all_quarantined_or_offline, and ambiguous.
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
        Metadata = reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAt = DateTime.Parse(reader.GetString(11)),
        UpdatedAt = DateTime.Parse(reader.GetString(12)),
    };

    /// <summary>
    /// Read a WorkerAssignment from column order (standalone, no JOIN):
    /// 0 id, 1 worker_identity, 2 run_id, 3 project_id, 4 task_id, 5 role, 6 assigned_by,
    /// 7 state, 8 latest_checkpoint_id, 9 cleanup_evidence, 10 cleanup_recorded_at,
    /// 11 acquired_at, 12 released_at, 13 created_at, 14 updated_at
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
        LatestCheckpointId = reader.IsDBNull(8) ? null : reader.GetInt32(8),
        CleanupEvidence = reader.IsDBNull(9) ? null : reader.GetString(9),
        CleanupRecordedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
        AcquiredAt = reader.IsDBNull(11) ? null : reader.GetString(11),
        ReleasedAt = reader.IsDBNull(12) ? null : reader.GetString(12),
        CreatedAt = DateTime.Parse(reader.GetString(13)),
        UpdatedAt = DateTime.Parse(reader.GetString(14)),
    };

    /// <summary>
    /// Read a WorkerAssignment with denormalized profile fields from a LEFT JOIN.
    /// Manual JOIN columns are at positions 15-19: profile_identity, worker_role,
    /// agent_instance_id, channel_id. (15, 16, 17, 18)
    /// </summary>
    private static WorkerAssignment ReadAssignmentWithJoin(SqliteDataReader reader)
    {
        var assignment = ReadAssignment(reader);
        // Columns 15-18 are the LEFT JOIN denormalized fields
        assignment.PoolMemberId = assignment.WorkerIdentity; // PoolMemberId alias
        assignment.ProfileIdentity = reader.IsDBNull(15) ? string.Empty : reader.GetString(15);
        assignment.WorkerRole = reader.IsDBNull(16) ? null : reader.GetString(16);
        assignment.AgentInstanceId = reader.IsDBNull(17) ? null : reader.GetString(17);
        assignment.ChannelId = reader.IsDBNull(18) ? null : reader.GetString(18);
        return assignment;
    }

    /// <summary>
    /// Read a WorkerAssignment with denormalized profile fields, passing the known worker.
    /// Used in TryLeaseSpecificWorkerAsync where we already have the worker.
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
        LatestCheckpointId = reader.IsDBNull(8) ? null : reader.GetInt32(8),
        CleanupEvidence = reader.IsDBNull(9) ? null : reader.GetString(9),
        CleanupRecordedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
        AcquiredAt = reader.IsDBNull(11) ? null : reader.GetString(11),
        ReleasedAt = reader.IsDBNull(12) ? null : reader.GetString(12),
        CreatedAt = DateTime.Parse(reader.GetString(13)),
        UpdatedAt = DateTime.Parse(reader.GetString(14)),
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

        var sql = "SELECT worker_identity, capabilities FROM worker_pool_members WHERE status = 'available'";
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

            if (requiredCapabilities is null || requiredCapabilities.Length == 0)
            {
                workers.Add(workerId);
                continue;
            }

            // Check capability match
            if (string.IsNullOrWhiteSpace(capabilitiesJson))
                continue;

            try
            {
                var caps = JsonSerializer.Deserialize<string[]>(capabilitiesJson);
                if (caps is not null && requiredCapabilities.All(c => caps.Contains(c, StringComparer.Ordinal)))
                    workers.Add(workerId);
            }
            catch
            {
                // Malformed capabilities JSON — skip
            }
        }

        return workers;
    }

    private static async Task<WorkerAssignment?> GetAssignmentByIdAsync(SqliteConnection conn, int assignmentId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {AssignmentColumnsPrefixed}, wm.profile_identity, wm.worker_role, wm.agent_instance_id, wm.channel_id
            FROM worker_assignments wa
            LEFT JOIN worker_pool_members wm ON wa.worker_identity = wm.worker_identity
            WHERE wa.id = @id
            """;
        cmd.Parameters.AddWithValue("@id", assignmentId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignmentWithJoin(reader) : null;
    }

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
}
