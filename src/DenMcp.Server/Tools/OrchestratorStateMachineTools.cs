using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class OrchestratorStateMachineTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    [McpServerTool(Name = "determine_orchestrator_next_action"), Description("Evaluate Den task, worker completion packets, and real review state to pick the next fail-closed orchestrator action.")]
    public static async Task<string> DetermineOrchestratorNextAction(
        ITaskRepository tasks,
        IMessageRepository messages,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Maximum coder/reviewer retry attempts before escalation. Default 3.")] int max_attempts = 3,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var detail = await tasks.GetDetailAsync(task_id).ConfigureAwait(false);
        if (!string.Equals(detail.Task.ProjectId, project_id, StringComparison.Ordinal))
            return Error($"Task #{task_id} belongs to project {detail.Task.ProjectId}, not {project_id}.");

        var taskMessages = await messages.GetMessagesAsync(project_id, taskId: task_id, limit: 100).ConfigureAwait(false);
        var implementation = LatestCompletion(taskMessages, "implementation_packet", "coder");
        var validation = LatestCompletion(taskMessages, "validation_packet", "validator");
        var drift = LatestCompletion(taskMessages, "drift_check_packet", "drift_checker");
        var audit = LatestCompletion(taskMessages, "packet_audit_packet", "packet_auditor");
        var reviewCompletion = LatestCompletion(taskMessages, "review_findings_packet", "reviewer");
        var latestRound = await reviewRounds.GetLatestByTaskAsync(task_id).ConfigureAwait(false);
        var unresolvedFindings = await reviewFindings.ListByTaskAsync(task_id, new[] { ReviewFindingStatus.Open, ReviewFindingStatus.ClaimedFixed, ReviewFindingStatus.NotFixed }).ConfigureAwait(false);

        var diagnostics = new List<string>();
        var checks = new Dictionary<string, bool>
        {
            ["implementation_packet_present"] = implementation is not null,
            ["validation_packet_present"] = validation is not null,
            ["drift_check_packet_present"] = drift is not null,
            ["packet_audit_packet_present"] = audit is not null,
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
            decision = RetryOrEscalate(attempts.coder, max_attempts, "launch_coder", "missing_implementation", "No implementation packet is present for this task.", "Launch coder worker with a coder_context_packet, including any failed worker-run diagnostics.");
        }
        else if (!CompletionSucceeded(implementation))
        {
            decision = RetryOrEscalate(attempts.coder, max_attempts, "launch_coder", "implementation_not_successful", "Latest implementation packet is not completed.", "Relaunch coder with failure/recovery context or escalate after retry cap.");
        }
        else if (!HasRepoIdentity(implementation))
        {
            diagnostics.Add("Implementation packet lacks final branch and/or head_commit.");
            decision = new Decision("escalate", "missing_repo_identity", "Implementation packet is missing branch/head commit; fail closed.", "Require a corrected implementation packet with branch and head_commit before validation/review.");
        }
        else if (!HasTestsOrSkipRationale(implementation))
        {
            diagnostics.Add("Implementation packet lacks tests_run or an explicit recovery/skip rationale.");
            decision = new Decision("launch_validator", "tests_not_reported", "Implementation packet does not report tests; validate deterministically before review.", "Launch validator worker for the implementation branch/head.");
        }
        else if (validation is null)
        {
            decision = new Decision("launch_validator", "validation_missing", "No validation packet exists for the implementation branch/head.", "Launch validator worker.");
        }
        else if (!CompletionSucceeded(validation))
        {
            decision = RetryOrEscalate(attempts.validator, max_attempts, "launch_coder", "validation_failed", "Validation did not complete successfully.", "Return to coder with validation diagnostics or escalate after retry cap.");
        }
        else if (!HasTestsOrSkipRationale(validation))
        {
            diagnostics.Add("Validation packet lacks tests_run or an explicit recovery/skip rationale.");
            decision = new Decision("escalate", "validation_evidence_missing", "Validation packet is completed but lacks deterministic command/result evidence; fail closed.", "Require a corrected validation_packet with tests_run or explicit skip rationale before drift/review.");
        }
        else if (!CompletionHeadMatches(implementation, validation))
        {
            diagnostics.Add("Validation packet head_commit does not match implementation head_commit.");
            decision = new Decision("escalate", "validation_head_mismatch", "Validation was not recorded for the implementation head; fail closed.", "Rerun validator on the implementation branch/head.");
        }
        else if (drift is null)
        {
            decision = new Decision("launch_drift_checker", "drift_check_missing", "No drift_check_packet exists for the implementation branch/head.", "Launch drift_checker worker.");
        }
        else if (!CompletionSucceeded(drift))
        {
            decision = RetryOrEscalate(attempts.drift_checker, max_attempts, "launch_coder", "drift_check_failed", "Drift check reported blocking drift or failed.", "Return to coder with drift diagnostics or escalate after retry cap.");
        }
        else if (!CompletionHeadMatches(implementation, drift))
        {
            diagnostics.Add("Drift check packet head_commit does not match implementation head_commit.");
            decision = new Decision("escalate", "drift_head_mismatch", "Drift check was not recorded for the implementation head; fail closed.", "Rerun drift_checker on the implementation branch/head.");
        }
        else if (audit is null)
        {
            decision = new Decision("launch_packet_auditor", "packet_audit_missing", "No packet_audit_packet exists to verify worker claims against Den/repo state.", "Launch packet_auditor worker.");
        }
        else if (!CompletionSucceeded(audit))
        {
            decision = RetryOrEscalate(attempts.packet_auditor, max_attempts, "escalate", "packet_audit_failed", "Packet audit did not pass; packet claims are unsupported or inconsistent.", "Correct unsupported packets or escalate to human/planner.");
        }
        else if (!CompletionHeadMatches(implementation, audit))
        {
            diagnostics.Add("Packet audit head_commit does not match implementation head_commit.");
            decision = new Decision("escalate", "packet_audit_head_mismatch", "Packet audit was not recorded for the implementation head; fail closed.", "Rerun packet_auditor on the implementation branch/head.");
        }
        else if (latestRound is null)
        {
            decision = new Decision("request_review", "review_round_missing", "Validation/drift/audit passed but no Den review round exists.", "Create a Den review round/request_review for the implementation branch/head, then launch reviewer.");
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
            decision = RetryOrEscalate(attempts.coder, max_attempts, "launch_coder", "changes_requested", "Real Den review state requests changes or has unresolved blocking findings.", "Launch coder with review findings packet.");
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
            decision = new Decision("ready_for_done_or_merge", "looks_good_validated", "Review verdict is looks_good and validation/drift/audit packets match the implementation head.", "Mark done or request human merge decision according to project workflow.");
        }

        var result = new
        {
            summary = $"next action: {decision.NextAction} ({decision.Reason})",
            decision,
            diagnostics,
            checks,
            attempts,
            task = new { id = detail.Task.Id, status = detail.Task.Status, title = detail.Task.Title },
            latest_packets = new
            {
                implementation = PacketSummary(implementation),
                validation = PacketSummary(validation),
                drift_check = PacketSummary(drift),
                packet_audit = PacketSummary(audit),
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
            fail_closed = decision.NextAction is "escalate" or "hold" or "ask_user_or_planner"
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static Decision RetryOrEscalate(int attempts, int maxAttempts, string retryAction, string reason, string rationale, string recovery)
    {
        return attempts >= Math.Max(1, maxAttempts)
            ? new Decision("escalate", reason + "_retry_cap", rationale + " Retry cap reached.", recovery)
            : new Decision(retryAction, reason, rationale, recovery);
    }

    private static Message? LatestCompletion(IReadOnlyList<Message> messages, string packetType, string role)
    {
        return messages.FirstOrDefault(m => IsCompletion(m)
            && string.Equals(MetadataString(m, "type"), packetType, StringComparison.Ordinal)
            && string.Equals(MetadataString(m, "role"), role, StringComparison.Ordinal));
    }

    private static int CountCompletions(IReadOnlyList<Message> messages, string role)
    {
        return messages.Count(m => IsWorkerAttempt(m) && string.Equals(MetadataString(m, "role"), role, StringComparison.Ordinal));
    }

    private static bool IsWorkerAttempt(Message message) =>
        IsCompletion(message) || string.Equals(MetadataString(message, "type"), "worker_failure_packet", StringComparison.Ordinal);

    private static bool CompletionSucceeded(Message packet) =>
        string.Equals(MetadataString(packet, "status"), "completed", StringComparison.Ordinal)
        && !MetadataBool(packet, "malformed");

    private static bool HasRepoIdentity(Message packet) =>
        !string.IsNullOrWhiteSpace(MetadataString(packet, "branch"))
        && !string.IsNullOrWhiteSpace(MetadataString(packet, "head_commit"));

    private static bool HasTestsOrSkipRationale(Message packet) =>
        MetadataHasValue(packet, "tests_run") || !string.IsNullOrWhiteSpace(MetadataString(packet, "recovery_guidance"));

    private static bool CompletionHeadMatches(Message implementation, Message check)
    {
        var head = MetadataString(implementation, "head_commit");
        var checkHead = MetadataString(check, "head_commit");
        return !string.IsNullOrWhiteSpace(head) && string.Equals(head, checkHead, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReviewCompletionMatchesRound(Message reviewCompletion, ReviewRound round)
    {
        var roundId = MetadataInt(reviewCompletion, "review_round_id");
        var head = MetadataString(reviewCompletion, "head_commit");
        return roundId == round.Id && string.Equals(head, round.HeadCommit, StringComparison.OrdinalIgnoreCase);
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

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, JsonOptions);

    private sealed record Decision(string NextAction, string Reason, string Rationale, string RecoveryGuidance);
}
