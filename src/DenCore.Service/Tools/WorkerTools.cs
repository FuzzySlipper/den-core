using System.ComponentModel;
using DenCore.Mcp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

[McpServerToolType]
public sealed class WorkerTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [McpToolProfile("admin-current", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("worker")]
    [McpServerTool(Name = "register_worker_run"), Description("Register a tracked Den worker run before an external/local substrate posts completion packets.")]
    public static async Task<string> RegisterWorkerRun(
        IWorkerPoolRepository pool,
        [Description("Project ID.")] string project_id,
        [Description("Den task id.")] int task_id,
        [Description("Agent/user registering the worker.")] string requested_by,
        [Description("Worker role: coder, reviewer, validator, drift_checker, or packet_auditor.")] string role,
        [Description("Runtime substrate label supplied by the external worker launcher. Core records assignments but does not launch substrates.")] string substrate = "external",
        [Description("Optional explicit worker run id. Omit to allocate one.")] string? run_id = null,
        [Description("Deprecated runtime correlation id. Accepted for compatibility but not stored by Core.")] string? session_id = null,
        [Description("Deprecated workspace id. Accepted for compatibility but not stored by Core.")] string? workspace_id = null,
        [Description("Optional requested branch.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional requested/expected head commit.")] string? head_commit = null,
        [Description("Optional Hermes profile name. Do not pass secrets or profile contents.")] string? profile = null,
        [Description("Optional provider name. Do not pass API keys.")] string? provider = null,
        [Description("Optional model name.")] string? model = null,
        [Description("Optional comma-separated toolsets.")] string? toolsets = null,
        [Description("Optional working directory for the worker process.")] string? workdir = null,
        [Description("Optional host/runner identifier.")] string? host = null,
        [Description("Optional timeout in seconds.")] int? timeout_seconds = null,
        [Description("Optional expected completion artifact path.")] string? artifact_path = null,
        [Description("Optional worker log path.")] string? log_path = null,
        [Description("Optional Den task-thread prompt packet message id.")] int? prompt_packet_message_id = null,
        [Description("Optional Den-managed state file reference.")] string? state_file_ref = null,
        [Description("Optional idempotency key. When supplied, session identity is derived from it for retry-safe registration.")] string? dedupe_key = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var normalizedRole = NormalizeRole(role);
            var workerRunId = NormalizeIdentifier(run_id) ?? NewRunId();

            // Check for existing assignment by run_id. Run IDs are globally unique in the
            // repository today, so still fail closed if a caller tries to reuse a run_id
            // from a different project.
            var existing = await pool.GetAssignmentByRunIdAsync(workerRunId).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!BelongsToProject(existing, project_id))
                    return Error($"Worker run {workerRunId} belongs to project {existing.ProjectId}, not {project_id}.");
                return Serialize(new
                {
                    summary = $"existing worker {workerRunId} ({existing.State})",
                    idempotency = new { status = "existing" },
                    worker_run = ToWorkerRunProjection(existing, normalizedRole, substrate, branch, base_branch, base_commit, head_commit, profile, toolsets, workdir, host, timeout_seconds, artifact_path, log_path, prompt_packet_message_id, state_file_ref),
                }, verbose: true);
            }

            // Upsert the member and lease
            var identity = $"pi-{Guid.NewGuid():N}";
            var member = new WorkerPoolMember
            {
                WorkerIdentity = identity,
                ProfileIdentity = profile ?? "spawned-coder",
                WorkerRole = normalizedRole,
                Status = "available",
            };
            await pool.UpsertMemberAsync(member).ConfigureAwait(false);

            var leaseInput = new LeaseWorkerInput
            {
                ProjectId = project_id,
                TaskId = task_id,
                Role = normalizedRole,
                AssignedBy = requested_by,
                RunId = workerRunId,
                PreferredWorkerIdentity = identity,
                ProfileIdentity = profile ?? string.Empty,
                WorkerRole = normalizedRole,
            };
            var assignment = await pool.LeaseAvailableWorkerAsync(leaseInput).ConfigureAwait(false);

            if (assignment is null)
                return Error("Failed to lease worker; no available capacity.");

            return Serialize(new
            {
                summary = $"registered worker {workerRunId} ({assignment.State})",
                idempotency = new { status = "created" },
                worker_run = ToWorkerRunProjection(assignment, normalizedRole, substrate, branch, base_branch, base_commit, head_commit, profile, toolsets, workdir, host, timeout_seconds, artifact_path, log_path, prompt_packet_message_id, state_file_ref),
            }, verbose: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
        {
            return Error(ex.Message);
        }
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("worker")]
    [McpServerTool(Name = "get_worker_run"), Description("Get a tracked Den worker run by run id or session id.")]
    public static async Task<string> GetWorkerRun(
        IWorkerPoolRepository pool,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var assignment = await GetAssignmentInProjectAsync(pool, run_id, project_id).ConfigureAwait(false);
        if (assignment is null)
            return Error($"Worker run {run_id} not found in project {project_id}.");
        return Serialize(new { worker_run = ToWorkerRunProjection(assignment), summary = $"worker {run_id} is {assignment.State}" }, verbose);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("worker")]
    [McpServerTool(Name = "list_worker_runs"), Description("List tracked Den worker runs with optional filters.")]
    public static async Task<string> ListWorkerRuns(
        IWorkerPoolRepository pool,
        [Description("Project ID.")] string project_id,
        [Description("Optional task filter.")] int? task_id = null,
        [Description("Optional worker role filter.")] string? role = null,
        [Description("Optional state/status filter.")] string? state = null,
        [Description("Maximum entries to return. Default 50, max 200.")] int limit = 50,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var options = new WorkerAssignmentListOptions
        {
            ProjectId = project_id,
            TaskId = task_id,
            Limit = Math.Clamp(limit, 1, 200),
        };
        if (!string.IsNullOrWhiteSpace(state))
            options.State = state;
        if (!string.IsNullOrWhiteSpace(role))
            options.Role = NormalizeRole(role);

        var assignments = await pool.ListAssignmentsAsync(options).ConfigureAwait(false);
        var workers = assignments.Select(assignment => ToWorkerRunProjection(assignment)).ToList();
        return Serialize(new { worker_runs = workers, count = workers.Count, summary = $"listed {workers.Count} worker run(s)" }, verbose: true);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("worker")]
    [McpServerTool(Name = "get_worker_run_status"), Description("Get a tracked Den worker run status projection combining assignment state and latest completion-packet state.")]
    public static async Task<string> GetWorkerRunStatus(
        IWorkerPoolRepository pool,
        IMessageRepository messages,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Optional task id to narrow completion lookup.")] int? task_id = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var assignment = await GetAssignmentInProjectAsync(pool, run_id, project_id).ConfigureAwait(false);
        if (assignment is null)
            return Error($"Worker run {run_id} not found in project {project_id}.");
        var completion = await FindLatestCompletionAsync(messages, project_id, run_id, task_id, assignment.Role).ConfigureAwait(false);
        return Serialize(new
        {
            summary = $"worker {run_id} state={assignment.State} completion={CompletionProjectionState(completion)}",
            worker_run = ToWorkerRunProjection(assignment),
            completion = CompletionProjection(completion),
            reconciliation = ReconcileState(assignment, completion),
        }, verbose: true);
    }

    [McpToolProfile("admin-current", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("worker")]
    [McpServerTool(Name = "cleanup_worker_run"), Description("Idempotently cleanup a terminal tracked Den worker run and report cleanup lifecycle state.")]
    public static async Task<string> CleanupWorkerRun(
        IWorkerPoolRepository pool,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Actor requesting cleanup.")] string requested_by,
        [Description("Optional reason.")] string? reason = null,
        [Description("Optional task id to narrow run lookup.")] int? task_id = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var assignment = await GetAssignmentInProjectAsync(pool, run_id, project_id).ConfigureAwait(false);
        if (assignment is null)
            return Error($"Worker run {run_id} not found in project {project_id}.");
        if (assignment.State is "completed" or "failed" or "expired")
        {
            var cleanupEvidence = JsonSerializer.Serialize(new
            {
                cleanup_by = requested_by,
                reason,
                cleanup_scope = "core_no_runtime_process",
                recorded_by_tool = "cleanup_worker_run",
                recorded_at = DateTime.UtcNow.ToString("o"),
            }, JsonOptions);
            assignment = await pool.RecordCleanupEvidenceAsync(assignment.Id, cleanupEvidence).ConfigureAwait(false) ?? assignment;
            var released = await pool.ReleaseAssignmentAsync(assignment.Id).ConfigureAwait(false);
            if (released is null)
            {
                return Serialize(new
                {
                    worker_run = ToWorkerRunProjection(assignment),
                    cleanup = new { status = "blocked", state = "release_failed", reason = "release refused even after cleanup evidence was recorded" }
                }, verbose: true);
            }
            return Serialize(new
            {
                worker_run = ToWorkerRunProjection(released),
                cleanup = new { status = "cleaned_up", state = "cleaned_up", reason }
            }, verbose: true);
        }
        return Serialize(new
        {
            worker_run = ToWorkerRunProjection(assignment),
            cleanup = new { status = "blocked", state = "not_eligible_active", reason = $"worker is {assignment.State}; terminal state required before cleanup" }
        }, verbose: true);
    }

    [McpToolProfile("admin-current", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("worker")]
    [McpServerTool(Name = "abort_worker_run"), Description("Request cancellation of a tracked Den worker run by marking its durable assignment aborted. Core does not terminate external runtime processes.")]
    public static async Task<string> AbortWorkerRun(
        IWorkerPoolRepository pool,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Actor requesting abort.")] string requested_by,
        [Description("Optional reason.")] string? reason = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var assignment = await GetAssignmentInProjectAsync(pool, run_id, project_id).ConfigureAwait(false);
        if (assignment is null)
            return Error($"Worker run {run_id} not found in project {project_id}.");
        if (assignment.State is "completed" or "failed" or "expired")
        {
            return Serialize(new
            {
                worker_run = ToWorkerRunProjection(assignment),
                control = new { status = "noop", reason = "worker is already terminal" }
            }, verbose: true);
        }
        // Core no longer owns worker runtime processes; abort only expires the durable assignment.
        var terminated = await pool.TransitionAssignmentStateAsync(assignment.Id, "expired",
            JsonSerializer.Serialize(new { aborted_by = requested_by, reason }, JsonOptions)).ConfigureAwait(false);
        return Serialize(new
        {
            worker_run = ToWorkerRunProjection(terminated ?? assignment, statusOverride: "aborted"),
            control = new { status = "aborted", reason }
        }, verbose: true);
    }

    [McpToolProfile("admin-current", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("worker")]
    [McpServerTool(Name = "rerun_worker_run"), Description("Rerun a tracked Den worker using the stored launch profile where available.")]
    public static async Task<string> RerunWorkerRun(
        IWorkerPoolRepository pool,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Actor requesting rerun.")] string requested_by,
        [Description("Optional reason.")] string? reason = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var original = await GetAssignmentInProjectAsync(pool, run_id, project_id).ConfigureAwait(false);
        if (original is null)
            return Error($"Worker run {run_id} not found in project {project_id}.");
        return Error("Rerun requires a den-host runtime substrate to launch a new worker process. Register a new worker run via register_worker_run instead.");
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static async Task<WorkerAssignment?> GetAssignmentInProjectAsync(IWorkerPoolRepository pool, string runId, string projectId)
    {
        var assignment = await pool.GetAssignmentByRunIdAsync(runId).ConfigureAwait(false);
        return assignment is not null && BelongsToProject(assignment, projectId) ? assignment : null;
    }

    private static bool BelongsToProject(WorkerAssignment assignment, string projectId)
        => string.Equals(assignment.ProjectId, projectId, StringComparison.Ordinal);

    private static object ToWorkerRunProjection(
        WorkerAssignment assignment,
        string? roleOverride = null,
        string? substrateOverride = null,
        string? branch = null,
        string? baseBranch = null,
        string? baseCommit = null,
        string? headCommit = null,
        string? profile = null,
        string? toolsets = null,
        string? workdir = null,
        string? host = null,
        int? timeoutSeconds = null,
        string? artifactPath = null,
        string? logPath = null,
        int? promptPacketMessageId = null,
        string? stateFileRef = null,
        string? statusOverride = null)
    {
        var role = roleOverride ?? assignment.Role ?? "coder";
        var substrate = substrateOverride ?? "external";
        var status = statusOverride ?? assignment.State ?? "unknown";
        return new
        {
            run_id = assignment.RunId,
            assignment_id = assignment.Id,
            project_id = assignment.ProjectId,
            task_id = assignment.TaskId,
            substrate,
            role,
            status,
            state = assignment.State,
            worker_identity = assignment.WorkerIdentity,
            assigned_by = assignment.AssignedBy,
            requested_repo = new { branch, base_branch = baseBranch, base_commit = baseCommit, head_commit = headCommit },
            launch_metadata = new { substrate, host, workdir, branch, base_branch = baseBranch, base_commit = baseCommit, head_commit = headCommit, profile, toolsets, timeout_seconds = timeoutSeconds, artifact_path = artifactPath, log_path = logPath, prompt_packet_message_id = promptPacketMessageId, state_file_ref = stateFileRef },
            created_at = assignment.CreatedAt,
            updated_at = assignment.UpdatedAt,
        };
    }

    private static async Task<Message?> FindLatestCompletionAsync(IMessageRepository messages, string projectId, string? runId, int? taskId, string? role)
    {
        var candidates = await messages.GetMessagesAsync(projectId, taskId: taskId, limit: 100).ConfigureAwait(false);
        var normalizedRole = string.IsNullOrWhiteSpace(role) ? null : NormalizeRole(role);
        return candidates.FirstOrDefault(m => IsCompletion(m)
            && (string.IsNullOrWhiteSpace(runId) || string.Equals(MetadataString(m, "run_id"), runId, StringComparison.Ordinal) || string.Equals(MetadataString(m, "session_id"), runId, StringComparison.Ordinal))
            && (normalizedRole is null || string.Equals(MetadataString(m, "role"), normalizedRole, StringComparison.Ordinal)));
    }

    private static object? CompletionProjection(Message? completion)
    {
        if (completion is null) return null;
        return new
        {
            message_id = completion.Id,
            status = MetadataString(completion, "status"),
            packet_type = MetadataString(completion, "type"),
            role = MetadataString(completion, "role"),
            run_id = MetadataString(completion, "run_id"),
            final_repo = new { branch = MetadataString(completion, "branch"), base_commit = MetadataString(completion, "base_commit"), head_commit = MetadataString(completion, "head_commit") },
            tests_reported = MetadataHasValue(completion, "tests_run"),
            created_at = completion.CreatedAt,
        };
    }

    private static string CompletionProjectionState(Message? completion)
        => completion is null ? "missing_or_untracked"
        : string.Equals(MetadataString(completion, "status"), "completed", StringComparison.Ordinal) ? "posted_completed"
        : "posted_non_success";

    private static object ReconcileState(WorkerAssignment assignment, Message? completion)
    {
        var terminal = assignment.State is "completed" or "failed" or "expired";
        var completionPresent = completion is not null;
        var diagnostics = new List<string>();
        if (terminal && !completionPresent)
            diagnostics.Add("Assignment is terminal but no structured completion packet was found.");
        if (!terminal && completionPresent)
            diagnostics.Add("Completion packet is present while assignment still appears active.");
        return new
        {
            assignment_state = assignment.State,
            terminal,
            completion_packet_present = completionPresent,
            completion_packet_message_id = completion?.Id,
            completion_state = CompletionProjectionState(completion),
            diagnostics,
        };
    }

    private static bool IsCompletion(Message message) =>
        MetadataBool(message, "completion_packet") || string.Equals(MetadataString(message, "schema"), "den_worker_completion", StringComparison.Ordinal);

    private static bool MetadataHasValue(Message message, string key)
    {
        if (message.Metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop))
            return prop.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        return false;
    }

    private static string? MetadataString(Message message, string key)
    {
        if (message.Metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }
        return null;
    }

    private static bool MetadataBool(Message message, string key)
    {
        if (message.Metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop))
            return prop.ValueKind == JsonValueKind.True || (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed) && parsed);
        return false;
    }

    private static string NormalizeRole(string? role)
    {
        var normalized = string.IsNullOrWhiteSpace(role) ? "raw" : role.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "drift_sentinel" => "drift_checker",
            "raw" or "coder" or "reviewer" or "validator" or "drift_checker" or "packet_auditor" => normalized,
            _ => throw new ArgumentException($"Unsupported worker role '{role}'."),
        };
    }

    private static string NewRunId() => $"piw_{DateTime.UtcNow:yyyyMMddHHmmss}_{RandomNumberGenerator.GetHexString(8).ToLowerInvariant()}";

    private static string? NormalizeIdentifier(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Serialize(object obj, bool verbose) => JsonSerializer.Serialize(obj, JsonOptions);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, JsonOptions);
}
