using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

/// <summary>
/// REST API routes for Core worker pool management.
/// These are Core-owned endpoints. Gateway/Channels/Hermes Bridge consume
/// these APIs; they do not access the database schema directly.
///
/// IDENTITY CONTRACT (v2):
/// All member endpoints accept and return profile_identity, worker_role,
/// agent_instance_id, channel_id, session_id alongside worker_identity.
/// Lifecycle operations (lease, quarantine, transition, release) key on
/// concrete worker_identity only — never on profile_identity alone.
/// </summary>
public static class WorkerPoolRoutes
{
    public static void MapWorkerPoolRoutes(this WebApplication app)
    {
        var pool = app.MapGroup("/api/worker-pool");

        // ── Members ──────────────────────────────────────────────────

        pool.MapPost("/members", async (IWorkerPoolRepository repo, JsonElement body) =>
        {
            WorkerPoolMember? member;
            try
            {
                member = JsonSerializer.Deserialize<WorkerPoolMember>(body.GetRawText(), JsonOpts.Default);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid request body" });
            }
            if (member is null || string.IsNullOrWhiteSpace(member.WorkerIdentity))
                return Results.BadRequest(new { error = "worker_identity is required" });
            var result = await repo.UpsertMemberAsync(member);
            return Results.Ok(result);
        });

        pool.MapGet("/members", async (IWorkerPoolRepository repo,
            string? status, string? workerIdentity, string? profileIdentity, string? workerRole, int limit = 50) =>
        {
            var members = await repo.ListMembersAsync(new WorkerPoolMemberListOptions
            {
                Status = status,
                WorkerIdentity = workerIdentity,
                ProfileIdentity = profileIdentity ?? string.Empty,
                WorkerRole = workerRole,
                Limit = limit
            });
            return Results.Ok(new { members, count = members.Count });
        });

        pool.MapGet("/members/{workerIdentity}", async (IWorkerPoolRepository repo, string workerIdentity) =>
        {
            var member = await repo.GetMemberAsync(workerIdentity);
            return member is not null ? Results.Ok(member) : Results.NotFound(new { error = $"Worker {workerIdentity} not found" });
        });

        pool.MapPost("/members/{workerIdentity}/quarantine", async (IWorkerPoolRepository repo, string workerIdentity, QuarantineRequest req) =>
        {
            var ok = await repo.QuarantineWorkerAsync(workerIdentity, req.QuarantinedBy, req.Reason);
            return ok ? Results.Ok(new { status = "quarantined" }) : Results.NotFound(new { error = $"Worker {workerIdentity} not found" });
        });

        // ── Assignments ──────────────────────────────────────────────

        pool.MapPost("/leases", async (IWorkerPoolRepository repo, JsonElement body) =>
        {
            LeaseWorkerInput? input;
            try
            {
                input = JsonSerializer.Deserialize<LeaseWorkerInput>(body.GetRawText(), JsonOpts.Default);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid request body" });
            }
            if (input is null || string.IsNullOrWhiteSpace(input.ProjectId) || string.IsNullOrWhiteSpace(input.Role) || string.IsNullOrWhiteSpace(input.RunId) || string.IsNullOrWhiteSpace(input.AssignedBy))
                return Results.BadRequest(new { error = "project_id, role, run_id, and assigned_by are required" });
            var result = await repo.LeaseWorkerWithDiagnosticsAsync(input);
            if (result.IsSuccess && result.Assignment is not null)
                return Results.Created($"/api/worker-pool/assignments/{result.Assignment.Id}", result.Assignment);
            // No-capacity — return 409 Conflict with typed diagnostic
            return Results.Conflict(new
            {
                error = "No available worker matching criteria",
                reason_code = result.NoCapacity?.ReasonCode,
                diagnostic_message = result.NoCapacity?.DiagnosticMessage,
                candidate_details = result.NoCapacity?.CandidateDetails,
                no_capacity_id = result.NoCapacity?.Id,
            });
        });

        pool.MapGet("/assignments", async (IWorkerPoolRepository repo,
            string? projectId, int? taskId, string? workerIdentity, string? state, string? role, int limit = 50) =>
        {
            var assignments = await repo.ListAssignmentsAsync(new WorkerAssignmentListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                WorkerIdentity = workerIdentity,
                State = state,
                Role = role,
                Limit = limit
            });
            return Results.Ok(new { assignments, count = assignments.Count });
        });

        pool.MapGet("/assignments/{assignmentId:int}", async (IWorkerPoolRepository repo, int assignmentId) =>
        {
            var assignment = await repo.GetAssignmentAsync(assignmentId);
            return assignment is not null ? Results.Ok(assignment) : Results.NotFound(new { error = $"Assignment {assignmentId} not found" });
        });

        pool.MapGet("/assignments/by-run/{runId}", async (IWorkerPoolRepository repo, string runId) =>
        {
            var assignment = await repo.GetAssignmentByRunIdAsync(runId);
            return assignment is not null ? Results.Ok(assignment) : Results.NotFound(new { error = $"Assignment for run {runId} not found" });
        });

        pool.MapPost("/assignments/{assignmentId:int}/transition", async (IWorkerPoolRepository repo, int assignmentId, TransitionRequest req) =>
        {
            var result = await repo.TransitionAssignmentStateAsync(assignmentId, req.State);
            return result is not null ? Results.Ok(result) : Results.BadRequest(new { error = $"Invalid transition to {req.State}" });
        });

        // ── No-Capacity Requests ────────────────────────────────────────

        pool.MapGet("/no-capacity", async (IWorkerPoolRepository repo,
            string? projectId, string? runId, string? reasonCode, int limit = 50) =>
        {
            var records = await repo.ListNoCapacityRequestsAsync(new NoCapacityRequestListOptions
            {
                ProjectId = projectId,
                RunId = runId,
                ReasonCode = reasonCode,
                Limit = limit,
            });
            return Results.Ok(new { records, count = records.Count });
        });

        pool.MapGet("/no-capacity/{id:int}", async (IWorkerPoolRepository repo, int id) =>
        {
            var record = await repo.GetNoCapacityRequestAsync(id);
            return record is not null ? Results.Ok(record) : Results.NotFound(new { error = $"No-capacity request {id} not found" });
        });

        // ── Checkpoints ──────────────────────────────────────────────

        pool.MapPost("/assignments/{assignmentId:int}/checkpoints", async (IWorkerPoolRepository repo, int assignmentId, AppendCheckpointRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.RunId) || string.IsNullOrWhiteSpace(req.CheckpointType) || string.IsNullOrWhiteSpace(req.Payload))
                return Results.BadRequest(new { error = "run_id, checkpoint_type, and payload are required" });
            try
            {
                var checkpoint = await repo.AppendCheckpointAsync(assignmentId, req.RunId, req.CheckpointType, req.Payload);
                return Results.Created($"/api/worker-pool/checkpoints/{checkpoint.Id}", checkpoint);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        pool.MapGet("/checkpoints", async (IWorkerPoolRepository repo,
            int? assignmentId, string? runId, string? checkpointType, int limit = 50) =>
        {
            var checkpoints = await repo.ListCheckpointsAsync(new WorkerCheckpointListOptions
            {
                AssignmentId = assignmentId,
                RunId = runId,
                CheckpointType = checkpointType,
                Limit = limit
            });
            return Results.Ok(new { checkpoints, count = checkpoints.Count });
        });

        // ── Checkpoint Responses ─────────────────────────────────────

        pool.MapPost("/checkpoints/{checkpointId:int}/responses", async (IWorkerPoolRepository repo, int checkpointId, AppendResponseRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.RunId) || string.IsNullOrWhiteSpace(req.ResponseType) || string.IsNullOrWhiteSpace(req.Payload))
                return Results.BadRequest(new { error = "run_id, response_type, and payload are required" });
            var response = await repo.AppendCheckpointResponseAsync(checkpointId, req.AssignmentId, req.RunId, req.ResponseType, req.Payload);
            return Results.Created($"/api/worker-pool/responses/{response.Id}", response);
        });

        pool.MapGet("/checkpoints/{checkpointId:int}/responses", async (IWorkerPoolRepository repo, int checkpointId) =>
        {
            var responses = await repo.ListResponsesAsync(checkpointId);
            return Results.Ok(new { responses, count = responses.Count });
        });

        pool.MapGet("/responses/by-run/{runId}", async (IWorkerPoolRepository repo, string runId, int limit = 50) =>
        {
            var responses = await repo.ListResponsesByRunIdAsync(runId, limit);
            return Results.Ok(new { responses, count = responses.Count });
        });

        // ── Cleanup & Release ────────────────────────────────────────

        pool.MapPost("/assignments/{assignmentId:int}/cleanup", async (IWorkerPoolRepository repo, int assignmentId, CleanupRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Evidence))
                return Results.BadRequest(new { error = "evidence is required" });
            var result = await repo.RecordCleanupEvidenceAsync(assignmentId, req.Evidence);
            return result is not null ? Results.Ok(result) : Results.BadRequest(new { error = "Assignment must be terminal before recording cleanup" });
        });

        pool.MapPost("/assignments/{assignmentId:int}/release", async (IWorkerPoolRepository repo, int assignmentId) =>
        {
            var result = await repo.ReleaseAssignmentAsync(assignmentId);
            return result is not null ? Results.Ok(result) : Results.BadRequest(new { error = "Assignment must be terminal with cleanup evidence to release" });
        });

        // ── Lanes / Capacity ──────────────────────────────────────────

        pool.MapPost("/lanes", async (IWorkerPoolRepository repo, JsonElement body) =>
        {
            WorkerPoolLane? lane;
            try
            {
                lane = JsonSerializer.Deserialize<WorkerPoolLane>(body.GetRawText(), JsonOpts.Default);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid request body" });
            }

            if (lane is null || string.IsNullOrWhiteSpace(lane.ProfileIdentity) || string.IsNullOrWhiteSpace(lane.WorkerRole))
                return Results.BadRequest(new { error = "profile_identity and worker_role are required" });
            if (lane.Capacity <= 0)
                return Results.BadRequest(new { error = "capacity must be greater than zero" });
            if (!WorkerPoolStates.ValidLaneStatuses.Contains(lane.Status))
                return Results.BadRequest(new { error = "status must be active, quarantined, or disabled" });

            var result = await repo.UpsertLaneAsync(lane);
            return Results.Ok(result);
        });

        pool.MapGet("/lanes", async (IWorkerPoolRepository repo, string? profileIdentity, string? status, int limit = 50) =>
        {
            var lanes = await repo.ListLanesAsync(profileIdentity, status, limit);
            return Results.Ok(new { lanes, count = lanes.Count });
        });

        pool.MapGet("/lanes/{profileIdentity}/{workerRole}", async (IWorkerPoolRepository repo, string profileIdentity, string workerRole) =>
        {
            var lane = await repo.GetLaneAsync(profileIdentity, workerRole);
            return lane is not null ? Results.Ok(lane) : Results.NotFound(new { error = $"Lane {profileIdentity}/{workerRole} not found" });
        });

        pool.MapPost("/lanes/{profileIdentity}/{workerRole}/status", async (IWorkerPoolRepository repo, string profileIdentity, string workerRole, LaneStatusRequest req) =>
        {
            if (!WorkerPoolStates.ValidLaneStatuses.Contains(req.Status))
                return Results.BadRequest(new { error = "status must be active, quarantined, or disabled" });
            var rows = await repo.SetLaneStatusAsync(profileIdentity, workerRole, req.Status);
            return rows > 0 ? Results.Ok(new { profile_identity = profileIdentity, worker_role = workerRole, status = req.Status }) : Results.NotFound(new { error = $"Lane {profileIdentity}/{workerRole} not found" });
        });

        pool.MapGet("/capacity/{profileIdentity}", async (IWorkerPoolRepository repo, string profileIdentity) =>
        {
            var summary = await repo.GetProfileCapacitySummaryAsync(profileIdentity);
            return Results.Ok(summary);
        });

        pool.MapPost("/stale/release", async (IWorkerPoolRepository repo) =>
        {
            var released = await repo.ReleaseStaleLeasesAsync();
            return Results.Ok(new { released });
        });

        // ── Orchestrator Leases ─────────────────────────────────────────

        pool.MapPost("/orchestrator-leases", async (IWorkerPoolRepository repo, JsonElement body) =>
        {
            CreateOrchestratorLeaseInput? input;
            try
            {
                input = JsonSerializer.Deserialize<CreateOrchestratorLeaseInput>(body.GetRawText(), JsonOpts.Default);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid request body" });
            }
            if (input is null || string.IsNullOrWhiteSpace(input.ProjectId) || string.IsNullOrWhiteSpace(input.LeaseOwner))
                return Results.BadRequest(new { error = "project_id and lease_owner are required" });
            if (!WorkerPoolStates.ValidScopeTypes.Contains(input.ScopeType))
                return Results.BadRequest(new { error = $"scope_type must be one of: {string.Join(", ", WorkerPoolStates.ValidScopeTypes)}" });
            if (!WorkerPoolStates.ValidRenewalPolicies.Contains(input.RenewalPolicy))
                return Results.BadRequest(new { error = $"renewal_policy must be one of: {string.Join(", ", WorkerPoolStates.ValidRenewalPolicies)}" });
            if (!WorkerPoolStates.ValidDrainPolicies.Contains(input.DrainPolicy))
                return Results.BadRequest(new { error = $"drain_policy must be one of: {string.Join(", ", WorkerPoolStates.ValidDrainPolicies)}" });
            if (input.RequestedDurationSeconds is <= 0)
                return Results.BadRequest(new { error = "requested_duration_seconds must be greater than zero when provided" });
            try
            {
                var lease = await repo.CreateOrchestratorLeaseAsync(input);
                return Results.Created($"/api/worker-pool/orchestrator-leases/{lease.Id}", lease);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        pool.MapGet("/orchestrator-leases", async (IWorkerPoolRepository repo,
            string? projectId, string? scopeType, string? orchestratorIdentity,
            string? state, string? leaseKind, bool includeTerminal = false, int limit = 50) =>
        {
            var leases = await repo.ListOrchestratorLeasesAsync(new OrchestratorLeaseListOptions
            {
                ProjectId = projectId,
                ScopeType = scopeType,
                OrchestratorIdentity = orchestratorIdentity,
                State = state,
                LeaseKind = leaseKind,
                IncludeTerminal = includeTerminal,
                Limit = limit,
            });
            return Results.Ok(new { leases, count = leases.Count });
        });

        pool.MapGet("/orchestrator-leases/{id:int}", async (IWorkerPoolRepository repo, int id) =>
        {
            var lease = await repo.GetOrchestratorLeaseAsync(id);
            return lease is not null ? Results.Ok(lease) : Results.NotFound(new { error = $"Orchestrator lease {id} not found" });
        });

        pool.MapGet("/orchestrator-leases/by-lease-id/{leaseId}", async (IWorkerPoolRepository repo, string leaseId) =>
        {
            var lease = await repo.GetOrchestratorLeaseByLeaseIdAsync(leaseId);
            return lease is not null ? Results.Ok(lease) : Results.NotFound(new { error = $"Orchestrator lease with lease_id '{leaseId}' not found" });
        });

        pool.MapPost("/orchestrator-leases/{id:int}/transition", async (IWorkerPoolRepository repo, int id, TransitionRequest req) =>
        {
            if (!WorkerPoolStates.ValidOrchLeaseStates.Contains(req.State))
                return Results.BadRequest(new { error = $"state must be one of: {string.Join(", ", WorkerPoolStates.ValidOrchLeaseStates)}" });
            var result = await repo.TransitionOrchestratorLeaseAsync(new TransitionOrchestratorLeaseInput
            {
                LeaseInternalId = id,
                NewState = req.State,
            });
            return result is not null ? Results.Ok(result) : Results.BadRequest(new { error = $"Invalid transition to {req.State}" });
        });

        pool.MapPost("/orchestrator-leases/{id:int}/cleanup", async (IWorkerPoolRepository repo, int id, CleanupRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Evidence))
                return Results.BadRequest(new { error = "evidence is required" });
            var result = await repo.RecordOrchestratorLeaseCleanupAsync(id, req.Evidence);
            return result is not null ? Results.Ok(result) : Results.BadRequest(new { error = "Orchestrator lease must be terminal before recording cleanup" });
        });

        pool.MapPost("/orchestrator-leases/reconcile-stale", async (IWorkerPoolRepository repo) =>
        {
            var affected = await repo.ReconcileStaleOrchestratorLeasesAsync();
            return Results.Ok(new { expired_or_degraded = affected });
        });

        // ── Pool Residency Projection ────────────────────────────────────

        pool.MapGet("/residency/{projectId}", async (IWorkerPoolRepository repo, string projectId) =>
        {
            var projections = await repo.GetPoolResidencyProjectionAsync(projectId);
            return Results.Ok(new { projections, count = projections.Count });
        });

        // ── Summary ──────────────────────────────────────────────────
        pool.MapGet("/summary", async (IWorkerPoolRepository repo) =>
        {
            var summary = await repo.GetSummaryAsync();
            return Results.Ok(summary);
        });
    }

    // ── Request DTOs ─────────────────────────────────────────────────

    public sealed record QuarantineRequest(string QuarantinedBy, string? Reason);
    public sealed record TransitionRequest(string State);
    public sealed record AppendCheckpointRequest(string RunId, string CheckpointType, string Payload);
    public sealed record AppendResponseRequest(int? AssignmentId, string RunId, string ResponseType, string Payload);
    public sealed record CleanupRequest(string Evidence);
    public sealed record LaneStatusRequest(string Status);
}
