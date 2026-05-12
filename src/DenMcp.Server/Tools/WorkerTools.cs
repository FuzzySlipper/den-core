using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class WorkerTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [McpServerTool(Name = "launch_pi_worker"), Description("Launch a raw Pi-backed Den worker run using a bounded prompt reference/state-file contract.")]
    public static async Task<string> LaunchPiWorker(
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Agent/user launching the worker.")] string requested_by,
        [Description("Worker role. Raw lifecycle layer accepts raw, coder, reviewer, validator, drift_checker, packet_auditor, or drift_sentinel alias.")] string role = "raw",
        [Description("Optional Den task id.")] int? task_id = null,
        [Description("Optional Den task-thread prompt packet message id. Prefer this or state_file_ref over large prompt args.")] int? prompt_packet_message_id = null,
        [Description("Optional Den-managed state file reference. Prefer this or prompt_packet_message_id over large prompt args.")] string? state_file_ref = null,
        [Description("Optional explicit worker run id. Omit to allocate one.")] string? run_id = null,
        [Description("Optional explicit Pi session id. Omit to allocate or derive from dedupe_key.")] string? session_id = null,
        [Description("Optional workspace id.")] string? workspace_id = null,
        [Description("Optional requested branch.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional requested head commit.")] string? head_commit = null,
        [Description("Session mode. Currently recorded in the worker contract; raw Pi session launch uses fresh semantics.")] string session_mode = "fresh",
        [Description("Optional model hint.")] string? model_hint = null,
        [Description("Optional provider hint.")] string? provider_hint = null,
        [Description("Optional timeout in seconds, recorded for contract visibility in the MCP result.")] int? timeout_seconds = null,
        [Description("Optional idempotency key. When session_id is omitted, this derives a stable session id.")] string? dedupe_key = null,
        [Description("Optional callback ports JSON array, e.g. [{\"host_port\":21455,\"container_port\":1455}]. Required by the current Pi session renderer unless defaults are supplied elsewhere.")] string? callback_ports = null,
        [Description("Optional dev dir override.")] string? dev_dir = null,
        [Description("Optional Pi state dir override.")] string? pi_state_dir = null,
        [Description("Optional compose file override.")] string? compose_file = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var normalizedRole = NormalizeRole(role);
            if (prompt_packet_message_id is null && string.IsNullOrWhiteSpace(state_file_ref))
                return Error("launch_pi_worker requires prompt_packet_message_id or state_file_ref; do not pass raw prompt bodies in process args.");

            var sessionId = NormalizeIdentifier(session_id) ?? DeriveSessionId(dedupe_key);
            var existing = await TryGetBySessionOrRunAsync(service, project_id, sessionId, run_id, task_id).ConfigureAwait(false);
            if (existing is not null && !string.IsNullOrWhiteSpace(dedupe_key))
                return SerializeLaunchResult(existing, normalizedRole, "existing", prompt_packet_message_id, state_file_ref, session_mode, timeout_seconds, branch, base_branch, base_commit, head_commit, null, verbose);

            var workerRunId = NormalizeIdentifier(run_id) ?? NewRunId();
            var startupPrompt = BuildStartupPrompt(project_id, task_id, normalizedRole, prompt_packet_message_id, state_file_ref);
            var title = $"Den Pi {normalizedRole} worker";
            var detail = await service.LaunchAsync(project_id, new PiSessionLaunchRequest
            {
                SessionId = sessionId,
                TaskId = task_id,
                WorkspaceId = workspace_id,
                RunId = workerRunId,
                Title = title,
                RequestedBy = requested_by,
                ToolProfile = normalizedRole,
                Model = model_hint,
                Provider = provider_hint,
                DevDir = dev_dir,
                PiStateDir = pi_state_dir,
                ComposeFile = compose_file,
                CallbackPorts = ParseCallbackPorts(callback_ports),
                PromptPacketMessageId = prompt_packet_message_id,
                StateFileRef = state_file_ref,
                StartupPrompt = startupPrompt,
                TimeoutSeconds = timeout_seconds,
            }).ConfigureAwait(false);

            return SerializeLaunchResult(detail, normalizedRole, "created", prompt_packet_message_id, state_file_ref, session_mode, timeout_seconds, branch, base_branch, base_commit, head_commit, null, verbose);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "get_worker_run"), Description("Get a raw Den Pi worker run by run id or session id.")]
    public static async Task<string> GetWorkerRun(
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var detail = await FindByRunOrSessionAsync(service, project_id, run_id).ConfigureAwait(false);
        if (detail is null)
            return Error($"Worker run {run_id} not found in project {project_id}.");
        return Serialize(new { worker_run = ToWorkerRun(detail), summary = $"worker {RunId(detail.Session)} is {ToWorkerStatus(detail.Session)}" }, verbose);
    }

    [McpServerTool(Name = "list_worker_runs"), Description("List raw Den Pi worker runs with optional filters.")]
    public static async Task<string> ListWorkerRuns(
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Optional task filter.")] int? task_id = null,
        [Description("Optional worker role filter.")] string? role = null,
        [Description("Optional state/status filter.")] string? state = null,
        [Description("Maximum entries to return. Default 50, max 200.")] int limit = 50,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var sessions = await service.ListAsync(new PiSessionListOptions
        {
            ProjectId = project_id,
            TaskId = task_id,
            State = state,
            Limit = Math.Clamp(limit, 1, 200),
        }).ConfigureAwait(false);
        var normalizedRole = string.IsNullOrWhiteSpace(role) ? null : NormalizeRole(role);
        var workers = sessions
            .Where(s => normalizedRole is null || string.Equals(Role(s), normalizedRole, StringComparison.Ordinal))
            .Select(s => ToWorkerRun(new PiSessionDetail { Session = s }))
            .ToList();
        return Serialize(new { worker_runs = workers, count = workers.Count, summary = $"listed {workers.Count} worker run(s)" }, verbose: true);
    }

    [McpServerTool(Name = "get_worker_run_status"), Description("Get a Den Pi worker run status projection combining runtime state, launch handles, and latest completion-packet state.")]
    public static async Task<string> GetWorkerRunStatus(
        IPiSessionService service,
        IMessageRepository messages,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Optional task id to narrow completion lookup.")] int? task_id = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var detail = await FindByRunOrSessionAsync(service, project_id, run_id, task_id).ConfigureAwait(false);
        if (detail is null)
            return Error($"Worker run {run_id} not found in project {project_id}.");
        var completion = await FindLatestCompletionAsync(messages, project_id, RunId(detail.Session), detail.Session.TaskId, Role(detail.Session)).ConfigureAwait(false);
        return Serialize(new
        {
            summary = $"worker {RunId(detail.Session)} runtime={ToWorkerStatus(detail.Session)} completion={CompletionProjectionState(completion, detail.Session)} cleanup={CleanupState(detail.Session)}",
            worker_run = ToWorkerRun(detail),
            completion = CompletionProjection(completion),
            reconciliation = ReconcileRuntimeAndCompletion(detail.Session, completion),
            operator_guidance = new
            {
                attach = OperatorAttach(detail),
                recent_output_handle = $"pi-session://{detail.Session.SessionId}/recent-output",
                cleanup = CleanupState(detail.Session) == "eligible" ? "cleanup_worker_run may be called idempotently after preserving needed forensics." : "Cleanup is not currently eligible or already completed."
            }
        }, verbose: true);
    }

    [McpServerTool(Name = "cleanup_worker_run"), Description("Idempotently cleanup a terminal Den Pi worker run and report cleanup lifecycle state.")]
    public static async Task<string> CleanupWorkerRun(
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Actor requesting cleanup.")] string requested_by,
        [Description("Optional reason.")] string? reason = null,
        [Description("Optional task id to narrow run lookup.")] int? task_id = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var detail = await FindByRunOrSessionAsync(service, project_id, run_id, task_id).ConfigureAwait(false);
            if (detail is null)
                return Error($"Worker run {run_id} not found in project {project_id}.");
            if (detail.Session.CleanupCompletedAt is not null)
            {
                return Serialize(new
                {
                    worker_run = ToWorkerRun(detail),
                    cleanup = new { status = "noop", state = "cleaned_up", reason = "cleanup was already completed", completed_at = detail.Session.CleanupCompletedAt }
                }, verbose: true);
            }
            if (PiSessionStates.IsActive(detail.Session.State))
            {
                return Serialize(new
                {
                    worker_run = ToWorkerRun(detail),
                    cleanup = new { status = "blocked", state = "not_eligible_active", reason = $"worker is {detail.Session.State}; terminate before cleanup" }
                }, verbose: true);
            }
            var cleaned = await service.CleanupAsync(project_id, detail.Session.SessionId, new PiSessionControlRequest
            {
                RequestedBy = requested_by,
                Reason = reason,
            }).ConfigureAwait(false);
            if (cleaned is null)
                return Error($"Worker run {run_id} disappeared before cleanup completed.");
            return Serialize(new
            {
                worker_run = ToWorkerRun(cleaned),
                cleanup = new { status = "cleaned_up", state = CleanupState(cleaned.Session), reason }
            }, verbose: true);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "abort_worker_run"), Description("Request cancellation of a raw Den Pi worker run. Current substrate maps this to Pi session termination.")]
    public static async Task<string> AbortWorkerRun(
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Actor requesting abort.")] string requested_by,
        [Description("Optional reason.")] string? reason = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var detail = await FindByRunOrSessionAsync(service, project_id, run_id).ConfigureAwait(false);
            if (detail is null)
                return Error($"Worker run {run_id} not found in project {project_id}.");
            if (!PiSessionStates.IsActive(detail.Session.State))
            {
                return Serialize(new
                {
                    worker_run = ToWorkerRun(detail, statusOverride: ToWorkerStatus(detail.Session), failureCategoryOverride: FailureCategory(detail.Session)),
                    control = new { status = "noop", reason = "worker is already terminal" }
                }, verbose: true);
            }

            var terminated = await service.TerminateAsync(project_id, detail.Session.SessionId, new PiSessionControlRequest
            {
                RequestedBy = requested_by,
                Reason = reason,
            }).ConfigureAwait(false);
            if (terminated is null)
                return Error($"Worker run {run_id} disappeared before abort completed.");
            return Serialize(new
            {
                worker_run = ToWorkerRun(terminated, statusOverride: "aborted", failureCategoryOverride: "aborted"),
                control = new { status = "aborted", reason }
            }, verbose: true);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "rerun_worker_run"), Description("Rerun a raw Den Pi worker using the stored Pi launch profile where available.")]
    public static async Task<string> RerunWorkerRun(
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Actor requesting rerun.")] string requested_by,
        [Description("Optional reason.")] string? reason = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var original = await FindByRunOrSessionAsync(service, project_id, run_id).ConfigureAwait(false);
            if (original is null)
                return Error($"Worker run {run_id} not found in project {project_id}.");
            var profile = original.LaunchProfile;
            if (profile is null)
                return Error($"Worker run {run_id} has no durable launch profile; rerun is unavailable.");

            var newRunId = NewRunId();
            var role = Role(original.Session);
            var detail = await service.LaunchAsync(project_id, new PiSessionLaunchRequest
            {
                SessionId = null,
                TaskId = original.Session.TaskId,
                WorkspaceId = original.Session.WorkspaceId,
                RunId = newRunId,
                Title = original.Session.Title,
                RequestedBy = requested_by,
                ToolProfile = role,
                Model = original.Session.Model,
                Provider = original.Session.Provider,
                DevDir = profile.DevDir,
                PiStateDir = profile.PiStateDir,
                ComposeFile = profile.ComposeFile,
                Service = profile.Service,
                Image = profile.Image,
                PiVersion = profile.PiVersion,
                NodeVersion = profile.NodeVersion,
                CallbackPorts = profile.CallbackPorts,
                PromptPacketMessageId = profile.PromptPacketMessageId,
                StateFileRef = profile.StateFileRef,
                StartupPrompt = profile.StartupPrompt,
                TimeoutSeconds = profile.TimeoutSeconds,
            }).ConfigureAwait(false);

            return SerializeLaunchResult(detail, role, "created", null, $"rerun:{RunId(original.Session)}", "fresh", null, null, null, null, null, RunId(original.Session), verbose);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    private static string SerializeLaunchResult(PiSessionDetail detail, string role, string idempotencyStatus, int? promptPacketMessageId, string? stateFileRef, string sessionMode, int? timeoutSeconds, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? rerunOfRunId, bool verbose)
    {
        var worker = ToWorkerRun(detail, roleOverride: role, sessionModeOverride: sessionMode, promptPacketMessageId: promptPacketMessageId, stateFileRef: stateFileRef, timeoutSeconds: timeoutSeconds, branch: branch, baseBranch: baseBranch, baseCommit: baseCommit, headCommit: headCommit, rerunOfRunId: rerunOfRunId);
        return Serialize(new
        {
            summary = $"{idempotencyStatus} worker {worker.run_id} ({worker.status})",
            idempotency = new { status = idempotencyStatus },
            worker_run = worker,
        }, verbose: true);
    }

    private static async Task<PiSessionDetail?> TryGetBySessionOrRunAsync(IPiSessionService service, string projectId, string? sessionId, string? runId, int? taskId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var bySession = await service.GetAsync(projectId, sessionId).ConfigureAwait(false);
            if (bySession is not null)
                return bySession;
        }
        if (!string.IsNullOrWhiteSpace(runId))
            return await FindByRunOrSessionAsync(service, projectId, runId, taskId).ConfigureAwait(false);
        return null;
    }

    private static async Task<PiSessionDetail?> FindByRunOrSessionAsync(IPiSessionService service, string projectId, string runOrSessionId, int? taskId = null)
    {
        var bySession = await service.GetAsync(projectId, runOrSessionId).ConfigureAwait(false);
        if (bySession is not null)
            return bySession;
        var sessions = await service.ListAsync(new PiSessionListOptions
        {
            ProjectId = projectId,
            TaskId = taskId,
            Limit = 200,
        }).ConfigureAwait(false);
        var match = sessions.FirstOrDefault(s => string.Equals(s.RunId, runOrSessionId, StringComparison.Ordinal));
        return match is null ? null : await service.GetAsync(projectId, match.SessionId).ConfigureAwait(false);
    }

    private static dynamic ToWorkerRun(
        PiSessionDetail detail,
        string? roleOverride = null,
        string? statusOverride = null,
        string? failureCategoryOverride = null,
        string? sessionModeOverride = null,
        int? promptPacketMessageId = null,
        string? stateFileRef = null,
        int? timeoutSeconds = null,
        string? branch = null,
        string? baseBranch = null,
        string? baseCommit = null,
        string? headCommit = null,
        string? rerunOfRunId = null)
    {
        var s = detail.Session;
        var role = roleOverride ?? Role(s);
        var status = statusOverride ?? ToWorkerStatus(s);
        var failureCategory = failureCategoryOverride ?? FailureCategory(s);
        return new
        {
            run_id = RunId(s),
            session_id = s.SessionId,
            project_id = s.ProjectId,
            task_id = s.TaskId,
            workspace_id = s.WorkspaceId,
            role,
            status,
            state = s.State,
            failure_category = failureCategory,
            failure_summary = s.StateReason,
            worker_identity = role,
            capability_scope_id = (string?)null,
            session_mode = sessionModeOverride ?? "fresh",
            rerun_of_run_id = rerunOfRunId,
            prompt_ref = promptPacketMessageId is null ? null : new { kind = "task_message", message_id = promptPacketMessageId },
            state_file_ref = stateFileRef ?? detail.LaunchProfile?.StateFileRef,
            timeout_seconds = timeoutSeconds ?? detail.LaunchProfile?.TimeoutSeconds,
            startup_contract = new
            {
                prompt_packet_message_id = promptPacketMessageId ?? detail.LaunchProfile?.PromptPacketMessageId,
                state_file_ref = stateFileRef ?? detail.LaunchProfile?.StateFileRef,
                delivered_via_environment = detail.LaunchProfile?.Environment.ContainsKey("DEN_WORKER_STARTUP_PROMPT") == true,
                environment_keys = detail.LaunchProfile?.Environment.Keys.Where(k => k.StartsWith("DEN_WORKER_", StringComparison.Ordinal)).ToArray(),
            },
            session = new
            {
                host_id = s.HostId,
                pi_session_id = s.SessionId,
                tmux_session = s.TmuxSessionName,
                container_id = s.ContainerId,
                container_name = s.ContainerName,
                compose_project = detail.LaunchProfile?.ComposeProjectName,
            },
            artifact_handles = new[]
            {
                new { name = "status", kind = "json", handle = $"pi-session://{s.SessionId}/status" },
                new { name = "recent_output", kind = "text", handle = $"pi-session://{s.SessionId}/recent-output" },
                new { name = "events", kind = "jsonl", handle = $"pi-session://{s.SessionId}/events" },
            },
            safe_summary = new
            {
                output_tail = s.OutputTail,
                output_tail_truncated = s.OutputTailTruncated,
                output_tail_captured_at = s.OutputTailCapturedAt,
                attention_state = s.AttentionState,
                attention_reason = s.AttentionReason,
                needs_user_input = s.NeedsUserInput,
            },
            lifecycle = new
            {
                runtime_state = s.State,
                completion_packet_state = CompletionPacketState(s),
                cleanup_state = CleanupState(s),
                termination_requested_at = s.TerminationRequestedAt,
                cleanup_requested_at = s.CleanupRequestedAt,
                cleanup_completed_at = s.CleanupCompletedAt,
                stale = s.State == PiSessionStates.Stale,
                diagnostics = LifecycleDiagnostics(s),
            },
            requested_repo = new
            {
                branch,
                base_branch = baseBranch,
                base_commit = baseCommit,
                head_commit = headCommit,
            },
            final_repo = (object?)null,
            created_at = s.CreatedAt,
            started_at = s.StartedAt,
            updated_at = s.UpdatedAt,
            completed_at = s.EndedAt,
        };
    }

    private static string ToWorkerStatus(PiSessionSummary session)
    {
        if (session.TerminationRequestedAt is not null)
            return "aborted";
        return session.State switch
        {
            PiSessionStates.Launching => "launching",
            PiSessionStates.Running => "running",
            PiSessionStates.Terminating => "aborted",
            PiSessionStates.Completed => "completed",
            PiSessionStates.Failed => "failed",
            PiSessionStates.Stale => "failed",
            _ => session.State,
        };
    }

    private static string? FailureCategory(PiSessionSummary session)
    {
        if (session.TerminationRequestedAt is not null)
            return "aborted";
        if (session.State == PiSessionStates.Stale)
            return "infrastructure";
        if (session.State != PiSessionStates.Failed)
            return null;
        var reason = session.StateReason ?? string.Empty;
        if (reason.Contains("quota", StringComparison.OrdinalIgnoreCase) || reason.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "quota";
        if (reason.Contains("extension", StringComparison.OrdinalIgnoreCase) && reason.Contains("load", StringComparison.OrdinalIgnoreCase))
            return "extension_load";
        if (reason.Contains("extension", StringComparison.OrdinalIgnoreCase))
            return "extension_runtime";
        if (reason.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        if (reason.Contains("spawn", StringComparison.OrdinalIgnoreCase) || reason.Contains("tmux", StringComparison.OrdinalIgnoreCase) || reason.Contains("docker", StringComparison.OrdinalIgnoreCase))
            return "spawn_error";
        return "infrastructure";
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
        if (completion is null)
            return null;
        return new
        {
            message_id = completion.Id,
            status = MetadataString(completion, "status"),
            packet_type = MetadataString(completion, "type"),
            role = MetadataString(completion, "role"),
            run_id = MetadataString(completion, "run_id"),
            session_id = MetadataString(completion, "session_id"),
            failure_category = MetadataString(completion, "failure_category"),
            recovery_guidance = MetadataString(completion, "recovery_guidance"),
            final_repo = new
            {
                branch = MetadataString(completion, "branch"),
                base_commit = MetadataString(completion, "base_commit"),
                head_commit = MetadataString(completion, "head_commit"),
            },
            review_round_id = MetadataInt(completion, "review_round_id"),
            tests_reported = MetadataHasValue(completion, "tests_run"),
            created_at = completion.CreatedAt,
        };
    }

    private static string CompletionProjectionState(Message? completion, PiSessionSummary session)
    {
        if (completion is null)
            return CompletionPacketState(session);
        return string.Equals(MetadataString(completion, "status"), "completed", StringComparison.Ordinal)
            ? "posted_completed"
            : "posted_non_success";
    }

    private static object ReconcileRuntimeAndCompletion(PiSessionSummary session, Message? completion)
    {
        var runtimeTerminal = !PiSessionStates.IsActive(session.State);
        var completionPresent = completion is not null;
        var diagnostics = new List<string>();
        if (runtimeTerminal && !completionPresent)
            diagnostics.Add("Runtime is terminal but no structured completion packet was found; do not treat process exit as worker success.");
        if (!runtimeTerminal && completionPresent)
            diagnostics.Add("Completion packet is present while runtime still appears active; verify process/container state before cleanup.");
        if (session.State == PiSessionStates.Stale)
            diagnostics.Add("Runtime state is stale; inspect tmux/container output and consider timeout handling.");
        return new
        {
            runtime_state = session.State,
            runtime_terminal = runtimeTerminal,
            completion_packet_present = completionPresent,
            completion_packet_message_id = completion?.Id,
            completion_state = CompletionProjectionState(completion, session),
            diagnostics,
        };
    }

    private static object OperatorAttach(PiSessionDetail detail)
    {
        var attach = detail.Attach;
        return new
        {
            mode = attach?.Mode ?? "external_attach_info",
            backend = attach?.Backend ?? "tmux",
            tmux_session_name = attach?.TmuxSessionName ?? detail.Session.TmuxSessionName,
            command_executable = attach?.CommandExecutable ?? "tmux",
            command_args = attach?.CommandArgs ?? ["attach-session", "-t", detail.Session.TmuxSessionName],
            warnings = attach?.Warnings ?? ["tmux is observability/break-glass only; Den state and completion packets drive automation."],
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

    private static int? MetadataInt(Message message, string key)
    {
        if (message.Metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value))
            return value;
        return null;
    }

    private static bool MetadataBool(Message message, string key)
    {
        if (message.Metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop))
            return prop.ValueKind == JsonValueKind.True || (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed) && parsed);
        return false;
    }

    private static string BuildStartupPrompt(string projectId, int? taskId, string role, int? promptPacketMessageId, string? stateFileRef)
    {
        var packetLine = promptPacketMessageId is null
            ? "- prompt_packet_message_id: `not provided`"
            : $"- prompt_packet_message_id: `{promptPacketMessageId}`";
        var stateLine = string.IsNullOrWhiteSpace(stateFileRef)
            ? "- state_file_ref: `not provided`"
            : $"- state_file_ref: `{stateFileRef.Trim()}`";
        return $"""
            You are a Den Pi {role} worker.

            Startup contract:
            1. Read the Den packet/state reference below before doing work.
            2. Treat the referenced Den packet/state file as authoritative task context.
            3. This startup prompt is intentionally bounded; do not expect large prompt bodies in process args.
            4. Keep secrets and raw reasoning out of logs and completion packets.
            5. On completion/block/failure, call MCP tool `post_worker_completion_packet` as the tracked worker completion record; do not rely on `send_message` alone.
            6. Use literal runtime environment values for identity: pass `run_id` = value of `DEN_WORKER_RUN_ID`, `project_id` = `DEN_WORKER_PROJECT_ID`, and `role` = `DEN_WORKER_ROLE`; use `DEN_WORKER_TASK_ID` only to verify you are completing the expected task. Never pass placeholder text like `(literal DEN_WORKER_RUN_ID)` or `$DEN_WORKER_RUN_ID`.

            Packet reference:
            - project_id: `{projectId}`
            - task_id: `{taskId?.ToString() ?? "none"}`
            - role: `{role}`
            {packetLine}
            {stateLine}
            """.Trim();
    }

    private static string CompletionPacketState(PiSessionSummary session)
    {
        if (session.State == PiSessionStates.Completed && session.StateReason?.Contains("worker completion packet #", StringComparison.OrdinalIgnoreCase) == true)
            return "posted_completed";
        if (session.State == PiSessionStates.Failed && session.StateReason?.Contains("worker completion packet #", StringComparison.OrdinalIgnoreCase) == true)
            return "posted_non_success";
        if (PiSessionStates.IsActive(session.State))
            return "pending";
        return "missing_or_untracked";
    }

    private static string CleanupState(PiSessionSummary session)
    {
        if (session.CleanupCompletedAt is not null)
            return "cleaned_up";
        if (session.CleanupRequestedAt is not null)
            return "cleanup_pending";
        if (PiSessionStates.IsActive(session.State))
            return "not_eligible_active";
        return "eligible";
    }

    private static IReadOnlyList<string> LifecycleDiagnostics(PiSessionSummary session)
    {
        var diagnostics = new List<string>();
        if (session.State == PiSessionStates.Stale)
            diagnostics.Add("Session is stale; inspect tmux/container state and consider cleanup after preserving needed forensics.");
        if (session.State == PiSessionStates.Completed && CompletionPacketState(session) == "missing_or_untracked")
            diagnostics.Add("Runtime is completed but no worker completion packet marker is present in state_reason; verify Den completion before advancing orchestration.");
        if (session.CleanupRequestedAt is not null && session.CleanupCompletedAt is null)
            diagnostics.Add("Cleanup has been requested but not completed.");
        if (session.NeedsUserInput)
            diagnostics.Add("Recent output/attention state indicates operator input may be needed.");
        return diagnostics;
    }

    private static IReadOnlyList<PiDockerCallbackPort> ParseCallbackPorts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("callback_ports must be a JSON array.");
        var ports = new List<PiDockerCallbackPort>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var hostPort = GetInt(item, "host_port", "hostPort");
            var containerPort = GetInt(item, "container_port", "containerPort");
            var bindAddress = GetString(item, "bind_address", "bindAddress") ?? PiDockerLaunchProfileDefaults.LoopbackAddress;
            ports.Add(new PiDockerCallbackPort { HostPort = hostPort, ContainerPort = containerPort, BindAddress = bindAddress });
        }
        return ports;
    }

    private static int GetInt(JsonElement item, string snake, string camel)
    {
        if (item.TryGetProperty(snake, out var value) || item.TryGetProperty(camel, out value))
            return value.GetInt32();
        throw new JsonException($"callback_ports item is missing {snake}.");
    }

    private static string? GetString(JsonElement item, string snake, string camel)
    {
        if (item.TryGetProperty(snake, out var value) || item.TryGetProperty(camel, out value))
            return value.GetString();
        return null;
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

    private static string Role(PiSessionSummary session) => NormalizeRole(session.ToolProfile);
    private static string RunId(PiSessionSummary session) => string.IsNullOrWhiteSpace(session.RunId) ? session.SessionId : session.RunId!;

    private static string? NormalizeIdentifier(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? DeriveSessionId(string? dedupeKey)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey))
            return null;
        return "worker-" + ShortHash(dedupeKey.Trim());
    }

    private static string NewRunId() => $"piw_{DateTime.UtcNow:yyyyMMddHHmmss}_{RandomNumberGenerator.GetHexString(8).ToLowerInvariant()}";

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string Serialize(object obj, bool verbose) => JsonSerializer.Serialize(obj, JsonOptions);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, JsonOptions);
}
