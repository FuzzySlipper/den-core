using System.ComponentModel;
using System.Text.Json;
using DenCore.Data;
using DenCore.Mcp;
using DenCore.Models;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

/// <summary>
/// MCP facade tools for Core worker pool management.
/// These are Core-owned tools — Gateway/Channels/Hermes Bridge use the
/// REST APIs or inject IWorkerPoolRepository for programmatic access.
///
/// IDENTITY CONTRACT (v2):
/// All pool member tools accept profile_identity, worker_role, agent_instance_id,
/// channel_id, session_id. Lifecycle operations use concrete worker_identity only.
/// Assignment readback includes denormalized PoolMemberId, ProfileIdentity, WorkerRole,
/// AgentInstanceId, ChannelId for disambiguation.
/// </summary>
[McpServerToolType]
public sealed class WorkerPoolTools
{
    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "upsert_pool_member"), Description(
        "Register or update a worker pool member. Workers are tracked agents " +
        "that can accept assignment leases for project/task/role work. " +
        "Multiple members can share the same profile_identity (e.g. 'spawned-coder') " +
        "but each has a distinct worker_identity for concrete lifecycle tracking.")]
    public static async Task<string> UpsertPoolMember(
        IWorkerPoolRepository repo,
        [Description("Unique worker identity (e.g. spawned-Hermes agent id / concrete member id).")] string worker_identity,
        [Description("Shared role/profile identity (e.g. 'spawned-coder'). Multiple members can share this.")] string? profile_identity = null,
        [Description("Worker role: coder, reviewer, validator, drift_checker, packet_auditor.")] string? worker_role = null,
        [Description("Optional display name.")] string? display_name = null,
        [Description("JSON array of capability strings, e.g. [\"dotnet\",\"sqlite\"]. " +
                     "Do NOT use role names (coder/reviewer/validator) as capability strings; " +
                     "those are resolved through worker_role.")] string? capabilities = null,
        [Description("Pool status: available, busy, quarantined, offboarded. Default: available.")] string status = "available",
        [Description("Optional agent instance binding id.")] string? agent_instance_id = null,
        [Description("Optional Den channel id for correlation.")] string? channel_id = null,
        [Description("Optional Hermes/session id for correlation.")] string? session_id = null,
        [Description("Optional JSON metadata (provider, model, toolsets, etc.).")] string? metadata = null)
    {
        var member = await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = worker_identity,
            ProfileIdentity = profile_identity ?? string.Empty,
            WorkerRole = worker_role,
            DisplayName = display_name,
            Capabilities = capabilities,
            Status = status,
            AgentInstanceId = agent_instance_id,
            ChannelId = channel_id,
            SessionId = session_id,
            Metadata = metadata,
        });
        return JsonSerializer.Serialize(new
        {
            summary = $"upserted pool member '{member.WorkerIdentity}' (status={member.Status}, profile={member.ProfileIdentity ?? "(none)"}, role={member.WorkerRole ?? "(none)"})",
            worker_identity = member.WorkerIdentity,
            profile_identity = member.ProfileIdentity,
            worker_role = member.WorkerRole,
            status = member.Status,
            agent_instance_id = member.AgentInstanceId,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "list_pool_members"), Description(
        "List worker pool members with optional status/profile/role/capability filtering. " +
        "For Agents Overview/caretaker reads of pool health. " +
        "Filter by profile_identity to find all members sharing a role profile (e.g. all spawned-coder instances).")]
    public static async Task<string> ListPoolMembers(
        IWorkerPoolRepository repo,
        [Description("Optional status filter: available, busy, quarantined, offboarded.")] string? status = null,
        [Description("Optional worker identity filter.")] string? worker_identity = null,
        [Description("Optional profile identity filter (e.g. 'spawned-coder').")] string? profile_identity = null,
        [Description("Optional worker role filter (e.g. 'coder', 'reviewer').")] string? worker_role = null,
        [Description("Maximum items to return (max 200).")] int limit = 50,
        [Description("If true, return full member records.")] bool verbose = false)
    {
        var members = await repo.ListMembersAsync(new WorkerPoolMemberListOptions
        {
            Status = status,
            WorkerIdentity = worker_identity,
            ProfileIdentity = profile_identity ?? string.Empty,
            WorkerRole = worker_role,
            Limit = Math.Clamp(limit, 1, 200),
        });

        if (verbose)
            return JsonSerializer.Serialize(new { members, count = members.Count }, JsonOpts.Default);

        var summaries = members.Select(m => new
        {
            worker = m.WorkerIdentity,
            profile = m.ProfileIdentity,
            role = m.WorkerRole,
            status = m.Status,
            last_heartbeat = m.LastHeartbeat,
        });
        return JsonSerializer.Serialize(new
        {
            summary = $"listed {members.Count} pool member(s)",
            count = members.Count,
            members = summaries,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "lease_worker"), Description(
        "Lease an available worker from the pool for a project/task/role. " +
        "Returns the assignment record with denormalized pool member identity fields on success. " +
        "On failure, returns a typed no-capacity diagnostic with reason code, candidate statistics, " +
        "and next-allowed guidance. Core-owned — Gateway/Channels use this to dispatch work. " +
        "Lifecycle operations key on worker_identity (concrete member id). " +
        "Use role/worker_role as the primary worker selector — do not pass role names as capabilities. " +
        "Use profile_identity to filter by shared role profile when multiple members share it.")]
    public static async Task<string> LeaseWorker(
        IWorkerPoolRepository repo,
        [Description("Project ID.")] string project_id,
        [Description("Worker role: coder, reviewer, validator, drift_checker, packet_auditor.")] string role,
        [Description("Assigning entity.")] string assigned_by,
        [Description("Worker run id for this assignment.")] string run_id,
        [Description("Optional Den task id.")] int? task_id = null,
        [Description("Optional preferred specific worker identity.")] string? preferred_worker_identity = null,
        [Description("Optional profile identity filter (e.g. 'spawned-coder'). Only workers with this profile are considered.")] string? profile_identity = null,
        [Description("Optional worker role filter (e.g. 'coder'). Only workers with this role are considered.")] string? worker_role = null,
        [Description("JSON array of hard capability constraints, e.g. [\"dotnet\",\"sqlite\"]. " +
                     "Do NOT pass role names (coder/reviewer/validator) here — use role/worker_role instead. " +
                     "Role-name aliases are normalised and matched against worker_role automatically.")] string? required_capabilities = null,
        [Description("If true, return full assignment record.")] bool verbose = false)
    {
        var capabilities = required_capabilities is not null
            ? JsonSerializer.Deserialize<string[]>(required_capabilities)
            : null;

        var result = await repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = project_id,
            TaskId = task_id,
            Role = role,
            AssignedBy = assigned_by,
            RunId = run_id,
            RequiredCapabilities = capabilities,
            PreferredWorkerIdentity = preferred_worker_identity,
            ProfileIdentity = profile_identity ?? string.Empty,
            WorkerRole = worker_role,
        });

        if (result.IsSuccess && result.Assignment is not null)
        {
            var a = result.Assignment;
            return JsonSerializer.Serialize(new
            {
                summary = $"leased worker '{a.WorkerIdentity}' for {role} in {project_id} (assignment #{a.Id})",
                assignment_id = a.Id,
                worker_identity = a.WorkerIdentity,
                pool_member_id = a.PoolMemberId,
                profile_identity = a.ProfileIdentity,
                worker_role = a.WorkerRole,
                agent_instance_id = a.AgentInstanceId,
                run_id = a.RunId,
                role = a.Role,
                state = a.State,
                project_id = a.ProjectId,
                task_id = a.TaskId,
                assignment_detail = verbose ? a : null,
            }, JsonOpts.Default);
        }

        // No-capacity diagnostic
        var nc = result.NoCapacity;
        if (nc is null)
        {
            return JsonSerializer.Serialize(new
            {
                summary = "no available worker matching criteria (no diagnostic)",
                error = true,
            }, JsonOpts.Default);
        }

        return JsonSerializer.Serialize(new
        {
            summary = $"no capacity: {nc.ReasonCode} — {nc.DiagnosticMessage}",
            error = true,
            no_capacity = new
            {
                id = nc.Id,
                reason_code = nc.ReasonCode,
                diagnostic_message = nc.DiagnosticMessage,
                candidate_details = nc.CandidateDetails,
                run_id = nc.RunId,
                project_id = nc.ProjectId,
                role = nc.Role,
                profile_identity = nc.ProfileIdentity,
                worker_role = nc.WorkerRole,
                required_capabilities = nc.RequiredCapabilities,
                preferred_worker_identity = nc.PreferredWorkerIdentity,
                created_at = nc.CreatedAt.ToString("o"),
            },
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "list_assignments"), Description(
        "List worker assignments with optional project/task/worker/state/role filters. " +
        "Use this for caretaker reads and overview projection. " +
        "Assignment records include denormalized profile_identity, worker_role, agent_instance_id for display.")]
    public static async Task<string> ListAssignments(
        IWorkerPoolRepository repo,
        [Description("Optional project filter.")] string? project_id = null,
        [Description("Optional task id filter.")] int? task_id = null,
        [Description("Optional worker identity filter.")] string? worker_identity = null,
        [Description("Optional state filter: ack, running, checkpoint_waiting, blocked, completed, failed, expired.")] string? state = null,
        [Description("Optional role filter.")] string? role = null,
        [Description("Maximum items to return (max 200).")] int limit = 50,
        [Description("If true, return full assignment records.")] bool verbose = false)
    {
        var assignments = await repo.ListAssignmentsAsync(new WorkerAssignmentListOptions
        {
            ProjectId = project_id,
            TaskId = task_id,
            WorkerIdentity = worker_identity,
            State = state,
            Role = role,
            Limit = Math.Clamp(limit, 1, 200),
        });

        if (verbose)
            return JsonSerializer.Serialize(new { assignments, count = assignments.Count }, JsonOpts.Default);

        var summaries = assignments.Select(a => new
        {
            id = a.Id,
            worker = a.WorkerIdentity,
            pool_member_id = a.PoolMemberId,
            profile = a.ProfileIdentity,
            worker_role = a.WorkerRole,
            agent_instance_id = a.AgentInstanceId,
            project = a.ProjectId,
            task = a.TaskId,
            role = a.Role,
            state = a.State,
            run = a.RunId,
        });
        return JsonSerializer.Serialize(new
        {
            summary = $"listed {assignments.Count} assignment(s)",
            count = assignments.Count,
            assignments = summaries,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "append_checkpoint"), Description(
        "Append a checkpoint packet for an assignment. Core-owned; checkpoints are the " +
        "primary progress/completion/failure communication from workers. " +
        "Gateway/Channels consume these via checkpoint response mechanisms.")]
    public static async Task<string> AppendCheckpoint(
        IWorkerPoolRepository repo,
        [Description("Assignment id.")] int assignment_id,
        [Description("Worker run id.")] string run_id,
        [Description("Checkpoint type: checkpoint, progress, completion, failure, state_snapshot.")] string checkpoint_type,
        [Description("JSON payload with checkpoint data.")] string payload)
    {
        var checkpoint = await repo.AppendCheckpointAsync(assignment_id, run_id, checkpoint_type, payload);
        return JsonSerializer.Serialize(new
        {
            summary = $"appended checkpoint #{checkpoint.Id} (type={checkpoint_type}) for assignment #{assignment_id}",
            checkpoint_id = checkpoint.Id,
            assignment_id,
            checkpoint_type,
            created_at = checkpoint.CreatedAt.ToString("o"),
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "respond_to_checkpoint"), Description(
        "Respond to a checkpoint with guidance/redirect/abort signals. " +
        "Core-owned; responses are the primary orchestrator-to-worker communication mechanism. " +
        "An 'ack' response transitions the assignment back to running. " +
        "An 'abort' response transitions the assignment to expired.")]
    public static async Task<string> RespondToCheckpoint(
        IWorkerPoolRepository repo,
        [Description("Checkpoint id.")] int checkpoint_id,
        [Description("Worker run id.")] string run_id,
        [Description("Response type: ack, guidance, redirect, abort, checkpoint_request.")] string response_type,
        [Description("JSON payload with response data.")] string payload,
        [Description("Optional assignment id for automatic assignment state transitions.")] int? assignment_id = null)
    {
        var response = await repo.AppendCheckpointResponseAsync(checkpoint_id, assignment_id, run_id, response_type, payload);
        return JsonSerializer.Serialize(new
        {
            summary = $"responded to checkpoint #{checkpoint_id} with '{response_type}'",
            response_id = response.Id,
            checkpoint_id,
            response_type,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "quarantine_pool_member"), Description(
        "Quarantine a worker pool member by concrete worker_identity. " +
        "Sets status to quarantined and records who quarantined them and why. " +
        "This prevents the worker from receiving new leases. " +
        "Uses concrete worker_identity — one quarantined member does not affect " +
        "other members sharing the same profile_identity.")]
    public static async Task<string> QuarantinePoolMember(
        IWorkerPoolRepository repo,
        [Description("Worker identity to quarantine (concrete member id).")] string worker_identity,
        [Description("Entity requesting quarantine.")] string quarantined_by,
        [Description("Optional reason.")] string? reason = null)
    {
        var ok = await repo.QuarantineWorkerAsync(worker_identity, quarantined_by, reason);
        if (!ok)
            return JsonSerializer.Serialize(new
            {
                summary = $"worker '{worker_identity}' not found in pool",
                error = true,
            }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            summary = $"quarantined worker '{worker_identity}'",
            worker_identity,
            status = "quarantined",
            quarantined_by,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "record_cleanup_evidence"), Description(
        "Record cleanup evidence for a terminal assignment. Required before release. " +
        "The assignment must be in a terminal state (completed, failed, or expired).")]
    public static async Task<string> RecordCleanupEvidence(
        IWorkerPoolRepository repo,
        [Description("Assignment id.")] int assignment_id,
        [Description("JSON evidence payload (e.g. {\"log_path\":\"...\",\"artifact_path\":\"...\"}).")] string evidence)
    {
        var result = await repo.RecordCleanupEvidenceAsync(assignment_id, evidence);
        if (result is null)
            return JsonSerializer.Serialize(new
            {
                summary = $"assignment #{assignment_id} not found or not in terminal state",
                error = true,
            }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            summary = $"recorded cleanup evidence for assignment #{assignment_id}",
            assignment_id,
            state = result.State,
            cleanup_recorded_at = result.CleanupRecordedAt,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "release_assignment"), Description(
        "Release a terminal assignment with cleanup evidence. Only succeeds when " +
        "the assignment is in a terminal state AND cleanup evidence has been recorded.")]
    public static async Task<string> ReleaseAssignment(
        IWorkerPoolRepository repo,
        [Description("Assignment id to release.")] int assignment_id)
    {
        var result = await repo.ReleaseAssignmentAsync(assignment_id);
        if (result is null)
            return JsonSerializer.Serialize(new
            {
                summary = $"assignment #{assignment_id} not found, not terminal, or missing cleanup evidence",
                error = true,
            }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            summary = $"released assignment #{assignment_id}",
            assignment_id,
            state = result.State,
            released_at = result.ReleasedAt,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "get_worker_pool_summary"), Description(
        "Get a bounded summary projection of the worker pool: member counts by status, " +
        "active/completed/failed assignment counts, and recent checkpoint activity. " +
        "Suitable for Agents Overview and caretaker reads.")]
    public static async Task<string> GetWorkerPoolSummary(
        IWorkerPoolRepository repo)
    {
        var summary = await repo.GetSummaryAsync();
        var orphanNote = summary.OrphanedLaunchingAssignments > 0
            ? $" | {summary.OrphanedLaunchingAssignments} orphaned-launching"
            : "";
        return JsonSerializer.Serialize(new
        {
            summary = $"pool: {summary.AvailableMembers} available, {summary.BusyMembers} busy, {summary.QuarantinedMembers} quarantined | " +
                      $"assignments: {summary.ActiveAssignments} active, {summary.CompletedAssignments} completed, " +
                      $"{summary.FailedAssignments} failed, {summary.ExpiredAssignments} expired{orphanNote} | " +
                      $"{summary.RecentCheckpoints} checkpoints in 24h",
            members = new
            {
                total = summary.TotalMembers,
                available = summary.AvailableMembers,
                busy = summary.BusyMembers,
                quarantined = summary.QuarantinedMembers,
            },
            assignments = new
            {
                active = summary.ActiveAssignments,
                completed = summary.CompletedAssignments,
                failed = summary.FailedAssignments,
                expired = summary.ExpiredAssignments,
                orphaned_launching = summary.OrphanedLaunchingAssignments,
            },
            recent_checkpoints_24h = summary.RecentCheckpoints,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "get_assignment"), Description(
        "Get a single assignment by id or run id. " +
        "Returns denormalized profile_identity, worker_role, agent_instance_id for display.")]
    public static async Task<string> GetAssignment(
        IWorkerPoolRepository repo,
        [Description("Assignment id (mutually exclusive with run_id).")] int? assignment_id = null,
        [Description("Run id (mutually exclusive with assignment_id).")] string? run_id = null,
        [Description("If true, return full record.")] bool verbose = false)
    {
        WorkerAssignment? assignment = null;
        if (assignment_id is not null)
            assignment = await repo.GetAssignmentAsync(assignment_id.Value);
        else if (!string.IsNullOrWhiteSpace(run_id))
            assignment = await repo.GetAssignmentByRunIdAsync(run_id);

        if (assignment is null)
            return JsonSerializer.Serialize(new
            {
                summary = "assignment not found",
                error = true,
            }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            summary = $"assignment #{assignment.Id}: worker={assignment.WorkerIdentity} state={assignment.State} role={assignment.Role}",
            assignment_id = assignment.Id,
            worker_identity = assignment.WorkerIdentity,
            pool_member_id = assignment.PoolMemberId,
            profile_identity = assignment.ProfileIdentity,
            worker_role = assignment.WorkerRole,
            agent_instance_id = assignment.AgentInstanceId,
            run_id = assignment.RunId,
            state = assignment.State,
            role = assignment.Role,
            project_id = assignment.ProjectId,
            task_id = assignment.TaskId,
            detail = verbose ? assignment : null,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "list_no_capacity_requests"), Description(
        "List worker pool no-capacity request records — typed diagnostics for lease " +
        "attempts that could not be fulfilled. Each record includes the typed reason code, " +
        "candidate statistics by status, the original request parameters, and a diagnostic message. " +
        "Reason codes: no_matching_worker, all_busy, all_quarantined_or_offline, ambiguous, " +
        "preferred_not_found_or_busy, hard_selector_mismatch. Use this for readback, automation, and Den Web diagnostics.")]
    public static async Task<string> ListNoCapacityRequests(
        IWorkerPoolRepository repo,
        [Description("Optional project filter.")] string? project_id = null,
        [Description("Optional run id filter.")] string? run_id = null,
        [Description("Optional reason code filter (no_matching_worker, all_busy, all_quarantined_or_offline, ambiguous, preferred_not_found_or_busy, hard_selector_mismatch).")] string? reason_code = null,
        [Description("Maximum items to return (max 200).")] int limit = 50,
        [Description("If true, return full records.")] bool verbose = false)
    {
        var records = await repo.ListNoCapacityRequestsAsync(new NoCapacityRequestListOptions
        {
            ProjectId = project_id,
            RunId = run_id,
            ReasonCode = reason_code,
            Limit = Math.Clamp(limit, 1, 200),
        });

        if (verbose)
            return JsonSerializer.Serialize(new { records, count = records.Count }, JsonOpts.Default);

        var summaries = records.Select(r => new
        {
            id = r.Id,
            reason_code = r.ReasonCode,
            diagnostic_message = r.DiagnosticMessage,
            candidate_details = r.CandidateDetails,
            project_id = r.ProjectId,
            role = r.Role,
            run_id = r.RunId,
            created_at = r.CreatedAt.ToString("o"),
        });
        return JsonSerializer.Serialize(new
        {
            summary = $"listed {records.Count} no-capacity request(s)",
            count = records.Count,
            records = summaries,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "get_no_capacity_request"), Description(
        "Get a single worker pool no-capacity request record by id. " +
        "Includes the typed reason code, candidate statistics, original request parameters, " +
        "and diagnostic message explaining why a lease could not be fulfilled.")]
    public static async Task<string> GetNoCapacityRequest(
        IWorkerPoolRepository repo,
        [Description("No-capacity request id.")] int id,
        [Description("If true, return full record.")] bool verbose = false)
    {
        var record = await repo.GetNoCapacityRequestAsync(id);
        if (record is null)
            return JsonSerializer.Serialize(new
            {
                summary = "no-capacity request not found",
                error = true,
            }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            summary = $"no-capacity request #{record.Id}: {record.ReasonCode} — {record.DiagnosticMessage}",
            id = record.Id,
            reason_code = record.ReasonCode,
            diagnostic_message = record.DiagnosticMessage,
            candidate_details = record.CandidateDetails,
            project_id = record.ProjectId,
            task_id = record.TaskId,
            role = record.Role,
            assigned_by = record.AssignedBy,
            run_id = record.RunId,
            profile_identity = record.ProfileIdentity,
            worker_role = record.WorkerRole,
            required_capabilities = record.RequiredCapabilities,
            preferred_worker_identity = record.PreferredWorkerIdentity,
            created_at = record.CreatedAt.ToString("o"),
            detail = verbose ? record : null,
        }, JsonOpts.Default);
    }

    // ── Orchestrator Lease Tools ──────────────────────────────────────────

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "create_orchestrator_lease"), Description(
        "Create a project-duration orchestrator lease. Selects an available pool member " +
        "matching the profile/capability filter and assigns them as a temporary orchestrator " +
        "for the specified project/channel/task/workstream scope. Distinct from bounded " +
        "task-scoped worker assignments — represents pooled orchestrator residency.")]
    public static async Task<string> CreateOrchestratorLease(
        IWorkerPoolRepository repo,
        [Description("Project ID for the orchestrator lease.")] string project_id,
        [Description("Entity that owns this lease (e.g. 'den-mcp-runner').")] string lease_owner,
        [Description("Scope type: project, channel, task, or workstream. Default: project.")] string scope_type = "project",
        [Description("Optional channel ID for channel-scoped leases.")] string? channel_id = null,
        [Description("Optional task ID for task-scoped engagement.")] int? task_id = null,
        [Description("Optional workstream handle.")] string? workstream_handle = null,
        [Description("Optional objective/mission for this lease.")] string? objective = null,
        [Description("Preferred orchestrator pool member identity.")] string? preferred_orchestrator_identity = null,
        [Description("Profile identity filter (e.g. 'pooled-orchestrator').")] string? profile_identity = null,
        [Description("Requested duration in seconds. Null = indefinite.")] int? requested_duration_seconds = null,
        [Description("Renewal policy: allow, deny, auto. Default: deny.")] string renewal_policy = "deny",
        [Description("Drain policy: graceful, immediate. Default: graceful.")] string drain_policy = "graceful",
        [Description("Optional JSON array of required capabilities.")] string? required_capabilities = null,
        [Description("Optional run id for correlation.")] string? run_id = null)
    {
        var capabilities = required_capabilities is not null
            ? System.Text.Json.JsonSerializer.Deserialize<string[]>(required_capabilities)
            : null;

        try
        {
            var lease = await repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
            {
                ProjectId = project_id,
                LeaseOwner = lease_owner,
                ScopeType = scope_type,
                ChannelId = channel_id,
                TaskId = task_id,
                WorkstreamHandle = workstream_handle,
                Objective = objective,
                PreferredOrchestratorIdentity = preferred_orchestrator_identity,
                ProfileIdentity = profile_identity ?? string.Empty,
                RequestedDurationSeconds = requested_duration_seconds,
                RenewalPolicy = renewal_policy,
                DrainPolicy = drain_policy,
                RequiredCapabilities = capabilities,
                RunId = run_id,
            });

            return JsonSerializer.Serialize(new
            {
                summary = $"created orchestrator lease '{lease.LeaseId}' for {project_id} (state={lease.State}, scope={lease.ScopeType})",
                lease_id = lease.LeaseId,
                id = lease.Id,
                orchestrator_identity = lease.OrchestratorIdentity,
                profile_identity = lease.ProfileIdentity,
                scope_type = lease.ScopeType,
                state = lease.State,
                lease_expires_at = lease.LeaseExpiresAt,
                renewal_policy = lease.RenewalPolicy,
                drain_policy = lease.DrainPolicy,
            }, JsonOpts.Default);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new
            {
                summary = $"failed to create orchestrator lease: {ex.Message}",
                error = true,
            }, JsonOpts.Default);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new
            {
                summary = $"failed to create orchestrator lease: {ex.Message}",
                error = true,
            }, JsonOpts.Default);
        }
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "list_orchestrator_leases"), Description(
        "List project-duration orchestrator leases with optional project/scope/state/identity filtering. " +
        "By default excludes terminal leases; use include_terminal=true to see all.")]
    public static async Task<string> ListOrchestratorLeases(
        IWorkerPoolRepository repo,
        [Description("Optional project filter.")] string? project_id = null,
        [Description("Optional scope type filter.")] string? scope_type = null,
        [Description("Optional orchestrator identity filter.")] string? orchestrator_identity = null,
        [Description("Optional state filter.")] string? state = null,
        [Description("Include terminal leases (released, quarantined, expired, degraded).")] bool include_terminal = false,
        [Description("Maximum items to return (max 200).")] int limit = 50)
    {
        var leases = await repo.ListOrchestratorLeasesAsync(new OrchestratorLeaseListOptions
        {
            ProjectId = project_id,
            ScopeType = scope_type,
            OrchestratorIdentity = orchestrator_identity,
            State = state,
            IncludeTerminal = include_terminal,
            Limit = Math.Clamp(limit, 1, 200),
        });

        var summaries = leases.Select(l => new
        {
            id = l.Id,
            lease_id = l.LeaseId,
            orchestrator = l.OrchestratorIdentity,
            profile = l.ProfileIdentity,
            project = l.ProjectId,
            scope = l.ScopeType,
            state = l.State,
            objective = l.Objective,
            expires_at = l.LeaseExpiresAt,
        });

        return JsonSerializer.Serialize(new
        {
            summary = $"listed {leases.Count} orchestrator lease(s)",
            count = leases.Count,
            leases = summaries,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "get_orchestrator_lease"), Description(
        "Get a single orchestrator lease by internal id or lease_id. " +
        "Returns full lease details including scope, objective, binding evidence, and expiry.")]
    public static async Task<string> GetOrchestratorLease(
        IWorkerPoolRepository repo,
        [Description("Internal lease id (mutually exclusive with lease_id).")] int? id = null,
        [Description("Unique lease id (mutually exclusive with id).")] string? lease_id = null,
        [Description("If true, return full record.")] bool verbose = false)
    {
        OrchestratorLease? lease = null;
        if (id is not null)
            lease = await repo.GetOrchestratorLeaseAsync(id.Value);
        else if (!string.IsNullOrWhiteSpace(lease_id))
            lease = await repo.GetOrchestratorLeaseByLeaseIdAsync(lease_id);

        if (lease is null)
            return JsonSerializer.Serialize(new { summary = "orchestrator lease not found", error = true }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            summary = $"orchestrator lease #{lease.Id}: state={lease.State} scope={lease.ScopeType} project={lease.ProjectId}",
            id = lease.Id,
            lease_id = lease.LeaseId,
            orchestrator_identity = lease.OrchestratorIdentity,
            profile_identity = lease.ProfileIdentity,
            scope_type = lease.ScopeType,
            project_id = lease.ProjectId,
            state = lease.State,
            objective = lease.Objective,
            lease_expires_at = lease.LeaseExpiresAt,
            renewal_policy = lease.RenewalPolicy,
            drain_policy = lease.DrainPolicy,
            agent_instance_id = lease.AgentInstanceId,
            session_id = lease.SessionId,
            run_id = lease.RunId,
            last_seen_at = lease.LastSeenAt,
            cleanup_evidence = lease.CleanupEvidence,
            detail = verbose ? lease : null,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "transition_orchestrator_lease"), Description(
        "Transition an orchestrator lease to a new state. Valid states: " +
        "proposed, leased, active, checkpoint_waiting, draining, released, quarantined, expired, degraded. " +
        "Terminal transitions release the pool member back to available.")]
    public static async Task<string> TransitionOrchestratorLease(
        IWorkerPoolRepository repo,
        [Description("Internal lease id.")] int lease_internal_id,
        [Description("New state.")] string new_state,
        [Description("Optional cleanup evidence JSON for terminal transitions.")] string? evidence = null)
    {
        var result = await repo.TransitionOrchestratorLeaseAsync(new TransitionOrchestratorLeaseInput
        {
            LeaseInternalId = lease_internal_id,
            NewState = new_state,
            Evidence = evidence,
        });

        if (result is null)
            return JsonSerializer.Serialize(new
            {
                summary = $"invalid transition to '{new_state}' for lease #{lease_internal_id}",
                error = true,
            }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            summary = $"transitioned orchestrator lease #{lease_internal_id} to '{new_state}'",
            id = result.Id,
            lease_id = result.LeaseId,
            state = result.State,
            orchestrator_identity = result.OrchestratorIdentity,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "get_pool_residency_projection"), Description(
        "Get the pool residency projection for a project — lists all active residencies " +
        "(task-worker assignments, orchestrator leases, gateway bindings) to distinguish " +
        "between: joined channel member, live binding, leased orchestrator, dedicated agent, " +
        "and bounded role-worker assignment.")]
    public static async Task<string> GetPoolResidencyProjection(
        IWorkerPoolRepository repo,
        [Description("Project ID to get residency projection for.")] string project_id)
    {
        var projections = await repo.GetPoolResidencyProjectionAsync(project_id);

        var grouped = projections.GroupBy(p => p.ResidencyKind)
            .ToDictionary(g => g.Key, g => g.ToList());

        return JsonSerializer.Serialize(new
        {
            summary = $"pool residency for '{project_id}': {projections.Count} active residencies " +
                      $"({string.Join(", ", grouped.Select(g => $"{g.Key}: {g.Value.Count}"))})",
            project_id,
            total = projections.Count,
            residencies = projections.Select(p => new
            {
                worker_identity = p.WorkerIdentity,
                profile_identity = p.ProfileIdentity,
                worker_role = p.WorkerRole,
                residency_kind = p.ResidencyKind,
                project_id = p.ProjectId,
                channel_id = p.ChannelId,
                task_id = p.TaskId,
                state = p.State,
                started_at = p.StartedAt,
                expires_at = p.ExpiresAt,
                agent_instance_id = p.AgentInstanceId,
            }),
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "reconcile_stale_orchestrator_leases"), Description(
        "Reconcile stale orchestrator leases — expire leases past their expiry time, " +
        "degrade leases whose pool member heartbeat is stale. Returns count of affected leases. " +
        "Does not permanently busy the shared profile.")]
    public static async Task<string> ReconcileStaleOrchestratorLeases(
        IWorkerPoolRepository repo)
    {
        var affected = await repo.ReconcileStaleOrchestratorLeasesAsync();
        return JsonSerializer.Serialize(new
        {
            summary = $"reconciled {affected} stale orchestrator lease(s)",
            expired_or_degraded = affected,
        }, JsonOpts.Default);
    }

    // ── Orphaned Worker-Run Detection & Reconciliation (#1879) ─────────

    [McpToolProfile("admin-current", "runner", "planner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "detect_orphaned_worker_runs"), Description(
        "Detect orphaned worker runs — pi_sessions stuck in 'launching' state with " +
        "no matching active assignment and no live process handle. These are registrations " +
        "that never completed launch. Returns diagnostics with age, staleness, and pool member info. " +
        "Use stale_threshold_minutes to control how old a launching session must be to qualify.")]
    public static async Task<string> DetectOrphanedWorkerRuns(
        IWorkerPoolRepository repo,
        [Description("Minutes a launching session must have existed to be considered orphaned. Default 10.")] int stale_threshold_minutes = 10)
    {
        var orphans = await repo.DetectOrphanedWorkerRunsAsync(stale_threshold_minutes);

        if (orphans.Count == 0)
            return JsonSerializer.Serialize(new
            {
                summary = "no orphaned worker runs detected",
                orphans = Array.Empty<object>(),
                count = 0,
            }, JsonOpts.Default);

        var diagnostics = orphans.Select(o => new
        {
            session_id = o.SessionId,
            run_id = o.RunId,
            project_id = o.ProjectId,
            task_id = o.TaskId,
            launch_profile_kind = o.LaunchProfileKind,
            created_at = o.CreatedAt.ToString("o"),
            age_minutes = o.AgeMinutes,
            has_busy_pool_member = o.HasBusyPoolMember,
            busy_worker_identity = o.BusyWorkerIdentity,
            no_active_assignment = o.NoActiveAssignment,
            is_stale = o.IsStale,
        });

        return JsonSerializer.Serialize(new
        {
            summary = $"detected {orphans.Count} orphaned worker run(s)",
            count = orphans.Count,
            orphans = diagnostics,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("worker-pool")]
    [McpServerTool(Name = "force_terminate_orphan_run"), Description(
        "Force-terminate an orphaned worker run by session_id. Sets the pi_session to 'failed', " +
        "expires any non-terminal assignment, and releases any busy pool member. " +
        "Idempotent — safe to call on already-terminal sessions. Creates an audit trail. " +
        "Use detect_orphaned_worker_runs first to identify candidates.")]
    public static async Task<string> ForceTerminateOrphanRun(
        IWorkerPoolRepository repo,
        [Description("Session id of the orphaned worker run to force-terminate.")] string session_id,
        [Description("Actor requesting termination (e.g. 'runner', admin agent name).")] string terminated_by,
        [Description("Optional reason for the force termination.")] string? reason = null)
    {
        try
        {
            var result = await repo.ForceTerminateOrphanRunAsync(session_id, terminated_by, reason);

            return JsonSerializer.Serialize(new
            {
                summary = result.IsAlreadyTerminal
                    ? $"session '{result.SessionId}' was already terminal"
                    : result.WasTerminated
                        ? $"force-terminated orphaned run '{result.RunId}' (session {result.SessionId})"
                        : $"session '{result.SessionId}' could not be terminated (may have been race-resolved)",
                session_id = result.SessionId,
                run_id = result.RunId,
                was_terminated = result.WasTerminated,
                already_terminal = result.IsAlreadyTerminal,
                released_pool_member = result.ReleasedPoolMember,
                released_worker_identity = result.ReleasedWorkerIdentity,
                expired_assignment = result.ExpiredAssignment,
                expired_assignment_id = result.ExpiredAssignmentId,
                reconciled_at = result.ReconciledAt.ToString("o"),
            }, JsonOpts.Default);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new
            {
                summary = $"force-terminate failed: {ex.Message}",
                error = true,
            }, JsonOpts.Default);
        }
    }
}
