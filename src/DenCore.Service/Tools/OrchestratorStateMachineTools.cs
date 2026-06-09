using System.ComponentModel;
using DenCore.Mcp;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using ModelContextProtocol.Server;
using TaskStatus = DenCore.Models.TaskStatus;

namespace DenCore.Service.Tools;

[McpServerToolType]
public sealed class OrchestratorStateMachineTools
{
    private const string GatePolicyVersion = "gate-policy-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    [McpToolProfile("admin-current", "planner", "runner")]
    [McpToolBundle("orchestrator")]
    [McpServerTool(Name = "determine_orchestrator_next_action"), Description("Evaluate Den task, worker completion packets, active gate assignments/wakes, and real review state to pick the next fail-closed orchestrator action.")]
    public static async Task<string> DetermineOrchestratorNextAction(
        ITaskRepository tasks,
        IMessageRepository messages,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        IWorkerPoolRepository workerPool,
        IAgentStreamRepository agentStream,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Maximum per-role worker retry attempts before escalation. Default 4.")] int max_attempts = 4,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        return await DetermineOrchestratorNextActionCore(
            tasks,
            messages,
            reviewRounds,
            reviewFindings,
            workerPool,
            agentStream,
            project_id,
            task_id,
            max_attempts,
            verbose).ConfigureAwait(false);
    }

    // Backward-compatible direct test/helper overload. The MCP-exposed overload above
    // receives worker-pool and agent-stream repositories from DI so it can be
    // in-flight gate aware; older direct callers still get message/review behavior.
    public static async Task<string> DetermineOrchestratorNextAction(
        ITaskRepository tasks,
        IMessageRepository messages,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        string project_id,
        int task_id,
        int max_attempts = 4,
        bool verbose = false)
    {
        return await DetermineOrchestratorNextActionCore(
            tasks,
            messages,
            reviewRounds,
            reviewFindings,
            workerPool: null,
            agentStream: null,
            project_id,
            task_id,
            max_attempts,
            verbose).ConfigureAwait(false);
    }

    private static async Task<string> DetermineOrchestratorNextActionCore(
        ITaskRepository tasks,
        IMessageRepository messages,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        IWorkerPoolRepository? workerPool,
        IAgentStreamRepository? agentStream,
        string projectId,
        int taskId,
        int maxAttempts,
        bool verbose)
    {
        var detail = await tasks.GetDetailAsync(taskId).ConfigureAwait(false);
        if (!string.Equals(detail.Task.ProjectId, projectId, StringComparison.Ordinal))
            return Error($"Task #{taskId} belongs to project {detail.Task.ProjectId}, not {projectId}.");

        var taskMessages = (await messages.GetMessagesAsync(projectId, taskId: taskId, limit: 100).ConfigureAwait(false))
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .ToList();
        var implementation = LatestCompletion(taskMessages, "implementation_packet", "coder");
        var validationGate = await BuildGateStateAsync(
            taskMessages,
            workerPool,
            agentStream,
            projectId,
            taskId,
            implementation,
            gateName: "validation",
            role: "validator",
            packetType: "validation_packet",
            contextPacketType: "validator_context_packet").ConfigureAwait(false);
        var driftGate = await BuildGateStateAsync(
            taskMessages,
            workerPool,
            agentStream,
            projectId,
            taskId,
            implementation,
            gateName: "drift_check",
            role: "drift_checker",
            packetType: "drift_check_packet",
            contextPacketType: "drift_checker_context_packet").ConfigureAwait(false);
        var auditGate = await BuildGateStateAsync(
            taskMessages,
            workerPool,
            agentStream,
            projectId,
            taskId,
            implementation,
            gateName: "packet_audit",
            role: "packet_auditor",
            packetType: "packet_audit_packet",
            contextPacketType: "packet_auditor_context_packet").ConfigureAwait(false);
        var reviewCompletion = LatestCompletion(taskMessages, "review_findings_packet", "reviewer");
        var latestRound = await reviewRounds.GetLatestByTaskAsync(taskId).ConfigureAwait(false);
        var unresolvedFindings = await reviewFindings.ListByTaskAsync(taskId, new[] { ReviewFindingStatus.Open, ReviewFindingStatus.ClaimedFixed, ReviewFindingStatus.NotFixed }).ConfigureAwait(false);

        var currentValidation = validationGate.SuccessfulPacket ?? validationGate.SameHeadPacket;
        var currentDrift = driftGate.SuccessfulPacket ?? driftGate.SameHeadPacket;
        var currentAudit = auditGate.SuccessfulPacket ?? auditGate.SameHeadPacket;

        var diagnostics = new List<string>();
        var checks = new Dictionary<string, bool>
        {
            ["implementation_packet_present"] = implementation is not null,
            ["current_head_validation_packet_present"] = currentValidation is not null,
            ["current_head_drift_check_packet_present"] = currentDrift is not null,
            ["current_head_packet_audit_packet_present"] = currentAudit is not null,
            ["validation_gate_in_flight"] = validationGate.State is GateStates.InFlight or GateStates.AmbiguousInFlight,
            ["drift_check_gate_in_flight"] = driftGate.State is GateStates.InFlight or GateStates.AmbiguousInFlight,
            ["packet_audit_gate_in_flight"] = auditGate.State is GateStates.InFlight or GateStates.AmbiguousInFlight,
            ["review_round_present"] = latestRound is not null,
            ["review_completion_packet_present"] = reviewCompletion is not null,
            ["review_verdict_present"] = latestRound?.Verdict is not null,
        };

        var attempts = new
        {
            coder = CountCompletions(taskMessages, role: "coder"),
            reviewer = CountCompletions(taskMessages, role: "reviewer"),
            validator = CountCompletions(taskMessages, role: "validator"),
            drift_checker = CountCompletions(taskMessages, role: "drift_checker"),
            packet_auditor = CountCompletions(taskMessages, role: "packet_auditor"),
        };

        Decision decision;
        if (detail.Task.Status is TaskStatus.Blocked or TaskStatus.Cancelled or TaskStatus.Done)
        {
            decision = new Decision("hold", "terminal_or_blocked_task", "Task status is blocked, done, or cancelled; do not launch workers automatically.", "Ask user/planner if more work is expected.");
        }
        else if (implementation is null)
        {
            decision = RetryOrEscalate(attempts.coder, maxAttempts, "launch_coder", "missing_implementation", "No implementation packet is present for this task.", "Launch coder worker with a coder_context_packet, including any failed worker-run diagnostics.");
        }
        else if (!CompletionSucceeded(implementation))
        {
            decision = RetryOrEscalate(attempts.coder, maxAttempts, "launch_coder", "implementation_not_successful", "Latest implementation packet is not completed.", "Relaunch coder with failure/recovery context or escalate after retry cap.");
        }
        else if (!HasRepoIdentity(implementation))
        {
            diagnostics.Add("Implementation packet lacks final branch and/or head_commit.");
            decision = new Decision("escalate", "missing_repo_identity", "Implementation packet is missing branch/head commit; fail closed.", "Require a corrected implementation packet with branch and head_commit before validation/review.");
        }
        else if (!HasTestsOrSkipRationale(implementation))
        {
            diagnostics.Add("Implementation packet lacks tests_run or an explicit recovery/skip rationale.");
            decision = LaunchGateDecision(validationGate, "validator", "tests_not_reported", "Implementation packet does not report tests; validate deterministically before review.", "Launch validator worker for the implementation branch/head.");
        }
        else if (TryGateWaitOrReconcile(validationGate, "validation", out var validationHold))
        {
            decision = validationHold;
        }
        else if (validationGate.State == GateStates.Missing)
        {
            decision = new Decision("launch_validator", "validation_missing_for_current_head", "No validation packet exists for the current implementation head.", "Launch validator worker for the current implementation branch/head.", validationGate.DedupeKey);
        }
        else if (currentValidation is null)
        {
            decision = new Decision("needs_reconcile", "validation_state_unreadable", "Validation gate projection did not find a current-head packet or active run; fail closed.", "Inspect validation packets, context packets, assignments, and wakes before launching another validator.");
        }
        else if (!CompletionSucceeded(currentValidation))
        {
            decision = RetryOrEscalate(attempts.validator, maxAttempts, "launch_coder", "validation_failed", "Validation did not complete successfully for the current implementation head.", "Return to coder with validation diagnostics or escalate after retry cap.");
        }
        else if (!HasTestsOrSkipRationale(currentValidation))
        {
            diagnostics.Add("Validation packet lacks tests_run or an explicit recovery/skip rationale.");
            decision = new Decision("escalate", "validation_evidence_missing", "Validation packet is completed but lacks deterministic command/result evidence; fail closed.", "Require a corrected validation_packet with tests_run or explicit skip rationale before drift/review.");
        }
        else if (TryGateWaitOrReconcile(driftGate, "drift_check", out var driftHold))
        {
            decision = driftHold;
        }
        else if (driftGate.State == GateStates.Missing)
        {
            decision = new Decision("launch_drift_checker", "drift_check_missing_for_current_head", "No drift_check_packet exists for the current implementation head.", "Launch drift_checker worker for the current implementation branch/head.", driftGate.DedupeKey);
        }
        else if (currentDrift is null)
        {
            decision = new Decision("needs_reconcile", "drift_check_state_unreadable", "Drift gate projection did not find a current-head packet or active run; fail closed.", "Inspect drift packets, context packets, assignments, and wakes before launching another drift checker.");
        }
        else if (!CompletionSucceeded(currentDrift))
        {
            decision = RetryOrEscalate(attempts.drift_checker, maxAttempts, "launch_coder", "drift_check_failed", "Drift check reported blocking drift or failed for the current implementation head.", "Return to coder with drift diagnostics or escalate after retry cap.");
        }
        else if (TryGateWaitOrReconcile(auditGate, "packet_audit", out var auditHold))
        {
            decision = auditHold;
        }
        else if (auditGate.State == GateStates.Missing)
        {
            decision = new Decision("launch_packet_auditor", "packet_audit_missing_for_current_head", "No packet_audit_packet exists for the current implementation head.", "Launch packet_auditor worker for the current implementation branch/head.", auditGate.DedupeKey);
        }
        else if (currentAudit is null)
        {
            decision = new Decision("needs_reconcile", "packet_audit_state_unreadable", "Packet-audit gate projection did not find a current-head packet or active run; fail closed.", "Inspect packet-audit packets, context packets, assignments, and wakes before launching another packet auditor.");
        }
        else if (!CompletionSucceeded(currentAudit))
        {
            decision = RetryOrEscalate(attempts.packet_auditor, maxAttempts, "escalate", "packet_audit_failed", "Packet audit did not pass for the current implementation head; packet claims are unsupported or inconsistent.", "Correct unsupported packets or escalate to human/planner.");
        }
        else if (latestRound is null)
        {
            decision = new Decision("request_review", "review_round_missing", "Validation/drift/audit passed for the current implementation head but no Den review round exists.", "Create a Den review round/request_review for the implementation branch/head, then launch reviewer.");
        }
        else if (!string.Equals(latestRound.HeadCommit, MetadataString(implementation, "head_commit"), StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add($"Latest review round head {latestRound.HeadCommit} does not match implementation head {MetadataString(implementation, "head_commit")}.");
            decision = new Decision("request_review", "review_head_mismatch", "Latest Den review round is not for the implementation head; fail closed.", "Request a new review round for the implementation head.");
        }
        else if (reviewCompletion is null)
        {
            decision = new Decision("launch_reviewer", "review_completion_missing", "Review round exists but no reviewer completion packet is present.", "Launch reviewer worker for the Den review round.");
        }
        else if (!ReviewCompletionMatchesRound(reviewCompletion, latestRound))
        {
            diagnostics.Add("Reviewer completion does not reference the latest Den review round/head.");
            decision = new Decision("escalate", "review_completion_mismatch", "Reviewer packet is inconsistent with real Den review state; fail closed.", "Require corrected reviewer packet or a new review round.");
        }
        else if (latestRound.Verdict is null)
        {
            decision = new Decision("escalate", "review_verdict_missing", "Reviewer packet exists but Den review verdict is missing; freeform packet text is insufficient.", "Set a structured Den review verdict before continuing.");
        }
        else if (latestRound.Verdict == ReviewVerdict.ChangesRequested || unresolvedFindings.Any(f => f.Status is ReviewFindingStatus.Open or ReviewFindingStatus.ClaimedFixed or ReviewFindingStatus.NotFixed))
        {
            decision = RetryOrEscalate(attempts.coder, maxAttempts, "launch_coder", "changes_requested", "Real Den review state requests changes or has unresolved blocking findings.", "Launch coder with review findings packet.");
        }
        else if (latestRound.Verdict == ReviewVerdict.BlockedByDependency)
        {
            decision = new Decision("ask_user_or_planner", "blocked_by_dependency", "Review verdict is blocked_by_dependency.", "Ask targeted user/planner question with review context.");
        }
        else if (latestRound.Verdict == ReviewVerdict.FollowUpNeeded)
        {
            decision = new Decision("triage_followups", "follow_up_needed", "Review verdict allows progress only after follow-up triage.", "Split/record follow-ups before marking done or merging.");
        }
        else
        {
            decision = new Decision("ready_for_done_or_merge", "looks_good_validated", "Review verdict is looks_good and validation/drift/audit packets match the current implementation head.", "Mark done or request human merge decision according to project workflow.");
        }

        var currentImplementationHead = implementation is null ? null : MetadataString(implementation, "head_commit");
        var reviewLooksGoodForSupersededHead = latestRound?.Verdict == ReviewVerdict.LooksGood
            && !string.IsNullOrWhiteSpace(currentImplementationHead)
            && !string.Equals(latestRound.HeadCommit, currentImplementationHead, StringComparison.OrdinalIgnoreCase);
        var reviewerPacketForSupersededHead = reviewCompletion is not null
            && !string.IsNullOrWhiteSpace(currentImplementationHead)
            && !MetadataHeadMatches(reviewCompletion, currentImplementationHead);
        var packetAuditNeedsReviewHeadCheck = reviewLooksGoodForSupersededHead || reviewerPacketForSupersededHead;
        var packetAuditHasConsistencyQuestion = validationGate.SameHeadPacket is not null
            || driftGate.SameHeadPacket is not null
            || packetAuditNeedsReviewHeadCheck;
        var packetAuditRunReason = packetAuditNeedsReviewHeadCheck
            ? "Reviewer/review verdict evidence targets a superseded implementation head; packet-auditor should verify cross-packet head consistency."
            : null;

        var result = new
        {
            summary = $"next action: {decision.NextAction} ({decision.Reason})",
            decision,
            diagnostics,
            checks,
            attempts,
            task = new { id = detail.Task.Id, status = detail.Task.Status, title = detail.Task.Title },
            workflow_head = implementation is null ? null : new
            {
                implementation_packet_id = implementation.Id,
                branch = MetadataString(implementation, "branch"),
                head_commit = MetadataString(implementation, "head_commit"),
                run_id = MetadataString(implementation, "run_id"),
            },
            gate_policy = new
            {
                version = GatePolicyVersion,
                dedupe_shape = "gate:{task_id}:{implementation_head}:{gate_role}:{gate_policy_version}",
            },
            gate_projection = new
            {
                validation = GateProjection(validationGate),
                drift_check = GateProjection(driftGate),
                packet_audit = GateProjection(auditGate),
            },
            gate_decision = new
            {
                policy_version = GatePolicyVersion,
                dedupe_key_shape = "gate:{task_id}:{implementation_head}:{gate_role}:gate-policy-v1",
                per_role = new
                {
                    validator = GateRoleDecision(validationGate, hasMultiRolePackets: auditGate.SameHeadPacket is not null || driftGate.SameHeadPacket is not null),
                    drift_checker = GateRoleDecision(driftGate, hasMultiRolePackets: true),
                    packet_auditor = GateRoleDecision(auditGate, hasMultiRolePackets: packetAuditHasConsistencyQuestion, missingRunReason: packetAuditRunReason),
                },
            },
            latest_packets = new
            {
                implementation = PacketSummary(implementation),
                validation = PacketSummary(currentValidation),
                drift_check = PacketSummary(currentDrift),
                packet_audit = PacketSummary(currentAudit),
                reviewer = PacketSummary(reviewCompletion),
            },
            review_state = latestRound is null ? null : new
            {
                latestRound.Id,
                latestRound.RoundNumber,
                latestRound.Branch,
                latestRound.BaseCommit,
                latestRound.HeadCommit,
                verdict = latestRound.Verdict?.ToDbValue(),
                latestRound.VerdictBy,
                unresolved_finding_count = unresolvedFindings.Count,
            },
            fail_closed = decision.NextAction is "escalate" or "hold" or "ask_user_or_planner" or "needs_reconcile" or "wait_in_flight"
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static Decision LaunchGateDecision(GateState gate, string actionRole, string reason, string rationale, string recovery)
    {
        if (gate.State == GateStates.AmbiguousInFlight)
            return new Decision("needs_reconcile", $"{gate.GateName}_ambiguous_in_flight", $"Multiple active {gate.Role} runs are visible for the current implementation head; fail closed.", $"Inspect {gate.Role} assignments, context packets, and wakes before launching another gate.");
        if (gate.State == GateStates.InFlight)
            return new Decision("wait_in_flight", $"{gate.GateName}_in_flight", $"A {gate.Role} run is already active for the current implementation head.", $"Wait for the active {gate.Role} run to post its completion packet.");
        return new Decision($"launch_{actionRole}", reason, rationale, recovery, gate.DedupeKey);
    }

    private static bool TryGateWaitOrReconcile(GateState gate, string reasonPrefix, out Decision decision)
    {
        if (gate.State == GateStates.AmbiguousInFlight)
        {
            decision = new Decision("needs_reconcile", $"{reasonPrefix}_ambiguous_in_flight", $"Multiple active {gate.Role} runs are visible for the current implementation head; fail closed.", $"Inspect {gate.Role} assignments, context packets, and wakes before launching another gate.");
            return true;
        }
        if (gate.State == GateStates.InFlight)
        {
            decision = new Decision("wait_in_flight", $"{reasonPrefix}_in_flight", $"A {gate.Role} run is already active for the current implementation head.", $"Wait for the active {gate.Role} run to post its completion packet.");
            return true;
        }

        decision = default!;
        return false;
    }

    private static Decision RetryOrEscalate(int attempts, int maxAttempts, string retryAction, string reason, string rationale, string recovery)
    {
        return attempts >= Math.Max(1, maxAttempts)
            ? new Decision("escalate", reason + "_retry_cap", rationale + " Retry cap reached.", recovery)
            : new Decision(retryAction, reason, rationale, recovery);
    }

    private static async Task<GateState> BuildGateStateAsync(
        IReadOnlyList<Message> messages,
        IWorkerPoolRepository? workerPool,
        IAgentStreamRepository? agentStream,
        string projectId,
        int taskId,
        Message? implementation,
        string gateName,
        string role,
        string packetType,
        string contextPacketType)
    {
        var currentHead = implementation is null ? null : MetadataString(implementation, "head_commit");
        var currentBranch = implementation is null ? null : MetadataString(implementation, "branch");
        var completions = messages
            .Where(m => IsCompletion(m)
                && string.Equals(MetadataString(m, "type"), packetType, StringComparison.Ordinal)
                && string.Equals(MetadataString(m, "role"), role, StringComparison.Ordinal))
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .ToList();

        Message? sameHeadPacket = null;
        Message? successfulPacket = null;
        var supersededPackets = new List<Message>();
        if (!string.IsNullOrWhiteSpace(currentHead))
        {
            var sameHeadPackets = completions.Where(p => MetadataHeadMatches(p, currentHead)).ToList();
            sameHeadPacket = sameHeadPackets.FirstOrDefault();
            successfulPacket = sameHeadPacket is not null && CompletionSucceeded(sameHeadPacket)
                ? sameHeadPacket
                : null;
            supersededPackets = completions.Where(p => !MetadataHeadMatches(p, currentHead)).ToList();
        }

        var activeRuns = string.IsNullOrWhiteSpace(currentHead)
            ? new List<ActiveGateRun>()
            : await FindActiveGateRunsAsync(
                messages,
                workerPool,
                agentStream,
                projectId,
                taskId,
                role,
                contextPacketType,
                currentHead!,
                currentBranch).ConfigureAwait(false);

        var state = GateStates.Missing;
        if (activeRuns.Count == 1 && activeRuns.All(r => string.Equals(r.CorrelationStatus, "current_head", StringComparison.Ordinal)))
            state = GateStates.InFlight;
        else if (activeRuns.Count > 0)
            state = GateStates.AmbiguousInFlight;
        else if (successfulPacket is not null)
            state = GateStates.Passed;
        else if (sameHeadPacket is not null)
            state = GateStates.FailedCurrentHead;

        return new GateState(
            gateName,
            role,
            packetType,
            contextPacketType,
            state,
            currentHead,
            DedupKey(taskId, currentHead, role),
            sameHeadPacket,
            successfulPacket,
            supersededPackets,
            activeRuns);
    }

    private static async Task<List<ActiveGateRun>> FindActiveGateRunsAsync(
        IReadOnlyList<Message> messages,
        IWorkerPoolRepository? workerPool,
        IAgentStreamRepository? agentStream,
        string projectId,
        int taskId,
        string role,
        string contextPacketType,
        string currentHead,
        string? currentBranch)
    {
        if (workerPool is null)
            return new List<ActiveGateRun>();

        var assignments = await workerPool.ListAssignmentsAsync(new WorkerAssignmentListOptions
        {
            ProjectId = projectId,
            TaskId = taskId,
            Role = role,
            Limit = 50
        }).ConfigureAwait(false);

        var activeAssignments = assignments
            .Where(a => !WorkerPoolStates.IsTerminal(a.State))
            .ToList();
        if (activeAssignments.Count == 0)
            return new List<ActiveGateRun>();

        var contextPackets = messages
            .Where(m => IsPacket(m, contextPacketType, role) && MetadataHeadMatches(m, currentHead))
            .ToList();

        var runs = new List<ActiveGateRun>();
        foreach (var assignment in activeAssignments)
        {
            var context = contextPackets.FirstOrDefault(m => string.Equals(MetadataString(m, "run_id"), assignment.RunId, StringComparison.Ordinal));
            AgentStreamEntry? wake = null;
            if (agentStream is not null)
            {
                var wakeEvents = await agentStream.ListAsync(new AgentStreamListOptions
                {
                    ProjectId = projectId,
                    TaskId = taskId,
                    MetadataRunId = assignment.RunId,
                    IncludeDebug = true,
                    Limit = 20
                }).ConfigureAwait(false);
                wake = wakeEvents.FirstOrDefault(e => IsWakeEvent(e)
                    && AgentStreamRoleMatches(e, role)
                    && (AgentStreamHeadMatches(e, currentHead) || (!AgentStreamHasHead(e) && context is not null)));
            }

            if (context is null && wake is null && activeAssignments.Count == 1 && contextPackets.Count == 1)
                context = contextPackets[0];

            var hasCurrentHeadEvidence = context is not null || wake is not null;
            runs.Add(ActiveGateRun.From(
                assignment,
                context,
                wake,
                currentHead,
                currentBranch,
                correlationStatus: hasCurrentHeadEvidence ? "current_head" : "uncorrelated_active_assignment"));
        }

        return runs;
    }

    private static bool IsPacket(Message message, string packetType, string role)
    {
        return string.Equals(MetadataString(message, "type"), packetType, StringComparison.Ordinal)
            && string.Equals(MetadataString(message, "role"), role, StringComparison.Ordinal);
    }

    private static Message? LatestCompletion(IReadOnlyList<Message> messages, string packetType, string role)
    {
        return messages
            .Where(m => IsCompletion(m)
                && string.Equals(MetadataString(m, "type"), packetType, StringComparison.Ordinal)
                && string.Equals(MetadataString(m, "role"), role, StringComparison.Ordinal))
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .FirstOrDefault();
    }

    private static int CountCompletions(IReadOnlyList<Message> messages, string role)
    {
        return messages.Count(m => IsWorkerAttempt(m) && string.Equals(MetadataString(m, "role"), role, StringComparison.Ordinal));
    }

    private static bool IsWorkerAttempt(Message message)
    {
        if (IsNonRetryBudgetFailure(message))
            return false;

        return IsCompletion(message)
            || string.Equals(MetadataString(message, "type"), "worker_failure_packet", StringComparison.Ordinal);
    }

    private static bool IsNonRetryBudgetFailure(Message message)
    {
        var category = NormalizeMetadataToken(MetadataString(message, "failure_category"));
        if (string.IsNullOrWhiteSpace(category))
            return false;

        return category.Contains("infrastructure", StringComparison.Ordinal)
            || category.Contains("capacity", StringComparison.Ordinal)
            || category.Contains("claim", StringComparison.Ordinal)
            || category.Contains("auth", StringComparison.Ordinal)
            || category.Contains("credential", StringComparison.Ordinal)
            || category.Contains("routing", StringComparison.Ordinal)
            || category.Contains("route", StringComparison.Ordinal)
            || category.Contains("membership", StringComparison.Ordinal)
            || category.Contains("provider", StringComparison.Ordinal)
            || category.Contains("config", StringComparison.Ordinal)
            || category.Contains("spawn", StringComparison.Ordinal)
            || category.Contains("synthetic", StringComparison.Ordinal);
    }

    private static bool CompletionSucceeded(Message packet) =>
        string.Equals(MetadataString(packet, "status"), "completed", StringComparison.Ordinal)
        && !MetadataBool(packet, "malformed");

    private static bool HasRepoIdentity(Message packet) =>
        !string.IsNullOrWhiteSpace(MetadataString(packet, "branch"))
        && !string.IsNullOrWhiteSpace(MetadataString(packet, "head_commit"));

    private static bool HasTestsOrSkipRationale(Message packet) =>
        MetadataHasValue(packet, "tests_run") || !string.IsNullOrWhiteSpace(MetadataString(packet, "recovery_guidance"));

    private static bool ReviewCompletionMatchesRound(Message reviewCompletion, ReviewRound round)
    {
        var roundId = MetadataInt(reviewCompletion, "review_round_id");
        var head = MetadataString(reviewCompletion, "head_commit");
        return roundId == round.Id && string.Equals(head, round.HeadCommit, StringComparison.OrdinalIgnoreCase);
    }

    private static object GateProjection(GateState gate)
    {
        return new
        {
            gate = gate.GateName,
            role = gate.Role,
            packet_type = gate.PacketType,
            context_packet_type = gate.ContextPacketType,
            state = gate.State,
            implementation_head = gate.CurrentHead,
            dedupe_key = gate.DedupeKey,
            terminal_packet = PacketSummary(gate.SuccessfulPacket ?? gate.SameHeadPacket),
            superseded_packets = gate.SupersededPackets.Select(PacketSummary).ToList(),
            active_runs = gate.ActiveRuns,
            coordination_call = new
            {
                kind = "worker_gate",
                gate = gate.GateName,
                role = gate.Role,
                implementation_head = gate.CurrentHead,
                dedupe_key = gate.DedupeKey,
                active_run_count = gate.ActiveRuns.Count,
                state = gate.State,
            },
        };
    }

    private static object GateRoleDecision(GateState gate, bool hasMultiRolePackets, string? missingRunReason = null)
    {
        string action;
        string reason;
        object handles;

        switch (gate.State)
        {
            case "passed":
                action = "fast_confirm";
                reason = $"Terminal successful {gate.Role} packet exists for current implementation head.";
                handles = new
                {
                    terminal_packet_id = gate.SuccessfulPacket?.Id,
                    terminal_packet_head = gate.CurrentHead,
                    gate = gate.GateName,
                    role = gate.Role,
                    dedupe_key = gate.DedupeKey,
                };
                break;
            case "in_flight":
                action = "wait_in_flight";
                reason = $"A {gate.Role} run is already active for the current implementation head.";
                handles = new
                {
                    active_run_id = gate.ActiveRuns.FirstOrDefault()?.RunId,
                    active_assignment_id = gate.ActiveRuns.FirstOrDefault()?.AssignmentId,
                    context_packet_id = gate.ActiveRuns.FirstOrDefault()?.ContextPacketId,
                    wake_event_id = gate.ActiveRuns.FirstOrDefault()?.WakeEventId,
                    gate = gate.GateName,
                    role = gate.Role,
                    active_run_count = gate.ActiveRuns.Count,
                };
                break;
            case "ambiguous_in_flight":
                action = "needs_reconcile";
                reason = $"Multiple active {gate.Role} runs are visible for the current implementation head; fail closed.";
                handles = new
                {
                    active_run_ids = gate.ActiveRuns.Select(r => r.RunId).ToList(),
                    active_assignment_ids = gate.ActiveRuns.Select(r => r.AssignmentId).ToList(),
                    gate = gate.GateName,
                    role = gate.Role,
                    state = gate.State,
                    dedupe_key = gate.DedupeKey,
                };
                break;
            case "failed_current_head":
                action = "run";
                reason = $"Current-head {gate.Role} packet failed; re-run after the upstream failure is addressed.";
                handles = new
                {
                    failed_packet_id = gate.SameHeadPacket?.Id,
                    implementation_head = gate.CurrentHead,
                    gate = gate.GateName,
                    role = gate.Role,
                    dedupe_key = gate.DedupeKey,
                };
                break;
            default: // "missing"
                // Packet-auditor skip: only coder (or coder+reviewer) ran, no cross-packet consistency question
                if (gate.GateName == "packet_audit" && !hasMultiRolePackets)
                {
                    action = "skip";
                    reason = "Only coder (or coder+reviewer) ran — no cross-packet or multi-role consistency question exists.";
                    handles = new
                    {
                        gate = gate.GateName,
                        role = gate.Role,
                        current_head = gate.CurrentHead,
                        has_multi_role_packets = hasMultiRolePackets,
                    };
                }
                else
                {
                    action = "run";
                    reason = missingRunReason ?? $"No {gate.Role} packet exists for the current implementation head.";
                    handles = new
                    {
                        gate = gate.GateName,
                        role = gate.Role,
                        implementation_head = gate.CurrentHead,
                        dedupe_key = gate.DedupeKey,
                    };
                }
                break;
        }

        return new
        {
            recommended_action = action,
            reason,
            handles,
            gate = gate.GateName,
            role = gate.Role,
            state = gate.State,
            dedupe_key = gate.DedupeKey,
        };
    }

    private static object? PacketSummary(Message? message)
    {
        if (message is null)
            return null;
        return new
        {
            message_id = message.Id,
            type = MetadataString(message, "type"),
            role = MetadataString(message, "role"),
            status = MetadataString(message, "status"),
            run_id = MetadataString(message, "run_id"),
            branch = MetadataString(message, "branch"),
            head_commit = MetadataString(message, "head_commit"),
            review_round_id = MetadataInt(message, "review_round_id"),
            malformed = MetadataBool(message, "malformed"),
            created_at = message.CreatedAt,
        };
    }

    private static bool IsCompletion(Message message) =>
        MetadataBool(message, "completion_packet") || string.Equals(MetadataString(message, "schema"), "den_worker_completion", StringComparison.Ordinal);

    private static bool MetadataHeadMatches(Message message, string headCommit) =>
        !string.IsNullOrWhiteSpace(headCommit)
        && string.Equals(MetadataString(message, "head_commit"), headCommit, StringComparison.OrdinalIgnoreCase);

    private static bool AgentStreamHeadMatches(AgentStreamEntry entry, string headCommit) =>
        !string.IsNullOrWhiteSpace(headCommit)
        && string.Equals(MetadataString(entry.Metadata, "head_commit"), headCommit, StringComparison.OrdinalIgnoreCase);

    private static bool AgentStreamHasHead(AgentStreamEntry entry) =>
        !string.IsNullOrWhiteSpace(MetadataString(entry.Metadata, "head_commit"));

    private static bool AgentStreamRoleMatches(AgentStreamEntry entry, string role) =>
        string.Equals(entry.RecipientRole, role, StringComparison.Ordinal)
        || string.Equals(MetadataString(entry.Metadata, "role"), role, StringComparison.Ordinal);

    private static bool IsWakeEvent(AgentStreamEntry entry) =>
        entry.DeliveryMode == AgentStreamDeliveryMode.Wake
        || entry.EventType.Contains("wake", StringComparison.OrdinalIgnoreCase);

    private static bool MetadataHasValue(Message message, string key)
    {
        if (message.Metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop))
            return prop.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        return false;
    }

    private static string? MetadataString(Message message, string key)
    {
        return MetadataString(message.Metadata, key);
    }

    private static string? MetadataString(JsonElement? metadata, string key)
    {
        if (metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop))
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

    private static string? NormalizeMetadataToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant().Replace('-', '_');

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

    private static string? DedupKey(int taskId, string? headCommit, string role)
    {
        return string.IsNullOrWhiteSpace(headCommit)
            ? null
            : $"gate:{taskId}:{headCommit}:{role}:{GatePolicyVersion}";
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, JsonOptions);

    private static class GateStates
    {
        public const string Missing = "missing";
        public const string Passed = "passed";
        public const string FailedCurrentHead = "failed_current_head";
        public const string InFlight = "in_flight";
        public const string AmbiguousInFlight = "ambiguous_in_flight";
    }

    private sealed record Decision(
        string NextAction,
        string Reason,
        string Rationale,
        string RecoveryGuidance,
        string? DedupeKey = null);

    private sealed record GateState(
        string GateName,
        string Role,
        string PacketType,
        string ContextPacketType,
        string State,
        string? CurrentHead,
        string? DedupeKey,
        Message? SameHeadPacket,
        Message? SuccessfulPacket,
        List<Message> SupersededPackets,
        List<ActiveGateRun> ActiveRuns);

    private sealed record ActiveGateRun(
        string RunId,
        int AssignmentId,
        string AssignmentState,
        string WorkerIdentity,
        int? ContextPacketId,
        int? WakeEventId,
        string? WakeEventType,
        string? HeadCommit,
        string? Branch,
        string CorrelationStatus)
    {
        public static ActiveGateRun From(
            WorkerAssignment assignment,
            Message? context,
            AgentStreamEntry? wake,
            string currentHead,
            string? currentBranch,
            string correlationStatus)
        {
            return new ActiveGateRun(
                assignment.RunId,
                assignment.Id,
                assignment.State,
                assignment.WorkerIdentity,
                context?.Id,
                wake?.Id,
                wake?.EventType,
                MetadataString(context?.Metadata, "head_commit") ?? MetadataString(wake?.Metadata, "head_commit") ?? currentHead,
                MetadataString(context?.Metadata, "branch") ?? currentBranch,
                correlationStatus);
        }
    }
}
