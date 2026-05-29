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
}

public sealed class WorkerPoolRepository : IWorkerPoolRepository
{
    private readonly DbConnectionFactory _db;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public WorkerPoolRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    // ── Members ──────────────────────────────────────────────────────────

    public async Task<WorkerPoolMember> UpsertMemberAsync(WorkerPoolMember member)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO worker_pool_members (worker_identity, display_name, capabilities, status, last_heartbeat, metadata)
            VALUES (@workerIdentity, @displayName, @capabilities, @status, @lastHeartbeat, @metadata)
            ON CONFLICT(worker_identity) DO UPDATE SET
                display_name = COALESCE(excluded.display_name, worker_pool_members.display_name),
                capabilities = COALESCE(excluded.capabilities, worker_pool_members.capabilities),
                status = excluded.status,
                last_heartbeat = excluded.last_heartbeat,
                metadata = COALESCE(excluded.metadata, worker_pool_members.metadata),
                updated_at = datetime('now')
            RETURNING worker_identity, display_name, capabilities, status, last_heartbeat, metadata, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@workerIdentity", member.WorkerIdentity);
        cmd.Parameters.AddWithValue("@displayName", (object?)member.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@capabilities", (object?)member.Capabilities ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", member.Status);
        cmd.Parameters.AddWithValue("@lastHeartbeat", (object?)member.LastHeartbeat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)member.Metadata ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadMember(reader);
    }

    public async Task<WorkerPoolMember?> GetMemberAsync(string workerIdentity)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT worker_identity, display_name, capabilities, status, last_heartbeat, metadata, created_at, updated_at
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

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT worker_identity, display_name, capabilities, status, last_heartbeat, metadata, created_at, updated_at
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
                // Otherwise find an available worker matching capability requirements
                var availableWorkers = await FindAvailableWorkersAsync(conn, input.RequiredCapabilities);
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

    private async Task<WorkerAssignment?> TryLeaseSpecificWorkerAsync(SqliteConnection conn, LeaseWorkerInput input)
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

        // Create assignment
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
        return ReadAssignment(reader);
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
        cmd.CommandText = """
            SELECT id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                   latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                   created_at, updated_at
            FROM worker_assignments
            WHERE run_id = @runId
            ORDER BY id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@runId", runId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignment(reader) : null;
    }

    public async Task<List<WorkerAssignment>> ListAssignmentsAsync(WorkerAssignmentListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }
        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.WorkerIdentity))
        {
            where.Add("worker_identity = @workerIdentity");
            cmd.Parameters.AddWithValue("@workerIdentity", options.WorkerIdentity);
        }
        if (!string.IsNullOrWhiteSpace(options.State))
        {
            where.Add("state = @state");
            cmd.Parameters.AddWithValue("@state", options.State);
        }
        if (!string.IsNullOrWhiteSpace(options.Role))
        {
            where.Add("role = @role");
            cmd.Parameters.AddWithValue("@role", options.Role);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                   latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                   created_at, updated_at
            FROM worker_assignments
            {whereClause}
            ORDER BY updated_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<WorkerAssignment>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadAssignment(reader));
        return results;
    }

    public async Task<WorkerAssignment?> TransitionAssignmentStateAsync(int assignmentId, string newState, string? metadata = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        var assignment = await GetAssignmentByIdAsync(conn, assignmentId);
        if (assignment is null)
            return null;

        // Validate transition
        if (!IsValidTransition(assignment.State, newState))
            return null;

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
            RETURNING id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                      latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", assignmentId);
        cmd.Parameters.AddWithValue("@newState", newState);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignment(reader) : null;
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
        cmd.CommandText = """
            UPDATE worker_assignments
            SET cleanup_evidence = @evidenceJson,
                cleanup_recorded_at = datetime('now'),
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                      latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                      created_at, updated_at
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
        cmd.CommandText = """
            UPDATE worker_assignments
            SET released_at = COALESCE(released_at, datetime('now')),
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                      latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                      created_at, updated_at
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

    // ── Private helpers ──────────────────────────────────────────────────

    private static WorkerPoolMember ReadMember(SqliteDataReader reader) => new()
    {
        WorkerIdentity = reader.GetString(0),
        DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
        Capabilities = reader.IsDBNull(2) ? null : reader.GetString(2),
        Status = reader.GetString(3),
        LastHeartbeat = reader.IsDBNull(4) ? null : reader.GetString(4),
        Metadata = reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedAt = DateTime.Parse(reader.GetString(6)),
        UpdatedAt = DateTime.Parse(reader.GetString(7)),
    };

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

    private static async Task<WorkerPoolMember?> GetMemberByConnAsync(SqliteConnection conn, string workerIdentity)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT worker_identity, display_name, capabilities, status, last_heartbeat, metadata, created_at, updated_at
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

    private static async Task<List<string>> FindAvailableWorkersAsync(SqliteConnection conn, string[]? requiredCapabilities)
    {
        var workers = new List<string>();
        await using var cmd = conn.CreateCommand();

        var sql = "SELECT worker_identity, capabilities FROM worker_pool_members WHERE status = 'available'";
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
        cmd.CommandText = """
            SELECT id, worker_identity, run_id, project_id, task_id, role, assigned_by, state,
                   latest_checkpoint_id, cleanup_evidence, cleanup_recorded_at, acquired_at, released_at,
                   created_at, updated_at
            FROM worker_assignments
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", assignmentId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAssignment(reader) : null;
    }

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
