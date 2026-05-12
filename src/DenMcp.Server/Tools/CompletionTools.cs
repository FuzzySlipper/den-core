using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class CompletionTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.Ordinal)
    {
        "completed", "blocked", "failed", "needs_input", "incomplete"
    };

    private static readonly HashSet<string> ValidPacketTypes = new(StringComparer.Ordinal)
    {
        "implementation_packet", "review_findings_packet", "validation_packet", "drift_check_packet", "packet_audit_packet", "worker_failure_packet"
    };

    private static readonly Regex ShellSyntaxPattern = new(@"(\$\(|`|\$\{|\bdate\b|\burandom\b|\bxxd\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [McpServerTool(Name = "post_worker_completion_packet"), Description("Post an idempotent structured Den Pi worker completion packet and update the durable worker/session status.")]
    public static async Task<string> PostWorkerCompletionPacket(
        IPiSessionService service,
        IPiSessionRepository sessions,
        IMessageRepository messages,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Agent/user posting completion.")] string requested_by,
        [Description("Completion status: completed, blocked, failed, needs_input, or incomplete. Invalid values are recorded as malformed.")] string status,
        [Description("Worker role.")] string role,
        [Description("Packet type: implementation_packet, review_findings_packet, validation_packet, drift_check_packet, packet_audit_packet, or worker_failure_packet.")] string packet_type,
        [Description("Safe human-readable summary. Must not contain secrets.")] string summary,
        [Description("Optional branch reported by worker.")] string? branch = null,
        [Description("Optional head commit reported by worker.")] string? head_commit = null,
        [Description("Optional base commit expected by orchestrator.")] string? base_commit = null,
        [Description("Optional JSON array of test commands/results.")] string? tests_run = null,
        [Description("Optional review round id.")] int? review_round_id = null,
        [Description("Optional JSON array of review finding ids.")] string? finding_ids = null,
        [Description("Optional failure category for non-success statuses.")] string? failure_category = null,
        [Description("Optional recovery guidance for failed/blocked/incomplete work.")] string? recovery_guidance = null,
        [Description("Optional idempotency key for retry-safe completion posting.")] string? dedupe_key = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var identityDiagnostics = ValidateSubmittedRunId(run_id);
        if (identityDiagnostics.Count > 0)
            return SerializeRejectedCompletion("malformed", "malformed_completion", identityDiagnostics);

        var detail = await FindByRunOrSessionAsync(service, project_id, run_id).ConfigureAwait(false);
        if (detail is null)
            return SerializeRejectedCompletion("missing_run", "missing_worker_run", [$"Worker run/session '{run_id}' was not found in project '{project_id}'."]);

        var normalizedRole = NormalizeRole(role);
        var durableRole = DurableRole(detail);
        var roleDiagnostics = new List<string>();
        if (durableRole is not null && !string.Equals(normalizedRole, durableRole, StringComparison.Ordinal))
        {
            roleDiagnostics.Add($"Worker completion role mismatch: supplied role '{normalizedRole}' does not match durable worker role '{durableRole}'.");
            normalizedRole = durableRole;
        }
        var normalizedStatus = NormalizeStatus(status);
        var normalizedPacketType = NormalizePacketType(packet_type);
        var packetDiagnostics = ValidatePacketClaims(normalizedStatus, normalizedPacketType, branch, head_commit, tests_run);
        packetDiagnostics.AddRange(roleDiagnostics);
        var isMalformed = normalizedStatus is null || normalizedPacketType is null || packetDiagnostics.Count > 0;
        normalizedStatus = isMalformed ? "malformed" : normalizedStatus;
        normalizedPacketType ??= "worker_failure_packet";
        var resolvedFailure = ResolveFailureCategory(normalizedStatus!, failure_category, isMalformed);
        if (packetDiagnostics.Count > 0)
            recovery_guidance = AppendRecoveryGuidance(recovery_guidance, string.Join(" ", packetDiagnostics));

        var existing = !string.IsNullOrWhiteSpace(dedupe_key)
            ? await FindExistingCompletionAsync(messages, project_id, detail.Session.TaskId, dedupe_key: dedupe_key).ConfigureAwait(false)
            : null;
        if (existing is not null)
            return SerializeCompletionResult(existing, "existing", isMalformed ? "malformed" : "present", verbose);

        var content = BuildCompletionContent(detail, normalizedRole, normalizedStatus!, normalizedPacketType, summary, branch, head_commit, base_commit, tests_run, review_round_id, finding_ids, resolvedFailure, recovery_guidance, isMalformed);
        var metadata = BuildMetadata(detail, normalizedRole, normalizedStatus!, normalizedPacketType, run_id, role, branch, head_commit, base_commit, tests_run, review_round_id, finding_ids, resolvedFailure, recovery_guidance, dedupe_key, isMalformed);
        var message = await messages.CreateAsync(new Message
        {
            ProjectId = project_id,
            TaskId = detail.Session.TaskId,
            Sender = requested_by,
            Content = content,
            Intent = CompletionIntent(normalizedStatus!, normalizedPacketType),
            Metadata = metadata,
        }).ConfigureAwait(false);

        var stateReason = normalizedStatus == "completed"
            ? $"worker completion packet #{message.Id}: completed"
            : $"worker completion packet #{message.Id}: {normalizedStatus}{(resolvedFailure is null ? string.Empty : $" ({resolvedFailure})")}";
        await sessions.MarkCompletionObservedAsync(
            project_id,
            detail.Session.SessionId,
            stateReason,
            lastActivityAt: DateTime.UtcNow).ConfigureAwait(false);

        return SerializeCompletionResult(message, "created", isMalformed ? "malformed" : "present", verbose);
    }

    [McpServerTool(Name = "get_latest_worker_completion"), Description("Get the latest structured completion packet for a worker run/task/role, or report missing_packet when none exists.")]
    public static async Task<string> GetLatestWorkerCompletion(
        IMessageRepository messages,
        [Description("Project ID.")] string project_id,
        [Description("Optional worker run id.")] string? run_id = null,
        [Description("Optional task id.")] int? task_id = null,
        [Description("Optional role filter.")] string? role = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var candidates = await messages.GetMessagesAsync(project_id, taskId: task_id, limit: 100).ConfigureAwait(false);
        var found = FindLatestCompletion(candidates, run_id, role);
        if (found is null)
        {
            return JsonSerializer.Serialize(new
            {
                completion_state = "missing_packet",
                summary = "no matching worker completion packet found",
                query = new { project_id, run_id, task_id, role },
                diagnostics = BuildLookupDiagnostics(candidates, run_id, role)
            }, JsonOptions);
        }
        var state = MetadataBool(found, "malformed") ? "malformed" : "present";
        return SerializeCompletionResult(found, "found", state, verbose);
    }

    private static async Task<PiSessionDetail?> FindByRunOrSessionAsync(IPiSessionService service, string projectId, string runOrSessionId)
    {
        var bySession = await service.GetAsync(projectId, runOrSessionId).ConfigureAwait(false);
        if (bySession is not null)
            return bySession;
        var sessions = await service.ListAsync(new PiSessionListOptions
        {
            ProjectId = projectId,
            Limit = 200,
        }).ConfigureAwait(false);
        var match = sessions.FirstOrDefault(s => string.Equals(s.RunId, runOrSessionId, StringComparison.Ordinal));
        return match is null ? null : await service.GetAsync(projectId, match.SessionId).ConfigureAwait(false);
    }

    private static async Task<Message?> FindExistingCompletionAsync(IMessageRepository messages, string projectId, int? taskId, string dedupe_key)
    {
        var candidates = await messages.GetMessagesAsync(projectId, taskId: taskId, limit: 100).ConfigureAwait(false);
        return candidates.FirstOrDefault(m => IsCompletion(m) && string.Equals(MetadataString(m, "dedupe_key"), dedupe_key, StringComparison.Ordinal));
    }

    private static Message? FindLatestCompletion(IEnumerable<Message> candidates, string? runId, string? role)
    {
        var normalizedRole = string.IsNullOrWhiteSpace(role) ? null : NormalizeRole(role);
        return candidates.FirstOrDefault(m =>
            IsCompletion(m)
            && (string.IsNullOrWhiteSpace(runId) || string.Equals(MetadataString(m, "run_id"), runId, StringComparison.Ordinal) || string.Equals(MetadataString(m, "session_id"), runId, StringComparison.Ordinal))
            && (normalizedRole is null || string.Equals(MetadataString(m, "role"), normalizedRole, StringComparison.Ordinal)));
    }

    private static List<string> BuildLookupDiagnostics(IEnumerable<Message> candidates, string? runId, string? role)
    {
        var diagnostics = new List<string>();
        var normalizedRole = string.IsNullOrWhiteSpace(role) ? null : NormalizeRole(role);
        foreach (var candidate in candidates.Where(IsCompletion).Take(10))
        {
            var candidateRun = MetadataString(candidate, "run_id");
            var candidateSession = MetadataString(candidate, "session_id");
            var candidateRole = MetadataString(candidate, "role");
            if (MetadataBool(candidate, "malformed") || IsSuspiciousRunId(candidateRun) || IsSuspiciousRunId(candidateSession))
                diagnostics.Add($"malformed same-task completion candidate message #{candidate.Id}: run_id='{candidateRun ?? "<missing>"}', role='{candidateRole ?? "<missing>"}'.");
            if (!string.IsNullOrWhiteSpace(runId) &&
                !string.Equals(candidateRun, runId, StringComparison.Ordinal) &&
                !string.Equals(candidateSession, runId, StringComparison.Ordinal))
                diagnostics.Add($"run mismatch for same-task completion candidate message #{candidate.Id}: expected '{runId}', found run_id='{candidateRun ?? "<missing>"}', session_id='{candidateSession ?? "<missing>"}'.");
            if (normalizedRole is not null && !string.Equals(candidateRole, normalizedRole, StringComparison.Ordinal))
                diagnostics.Add($"role mismatch for same-task completion candidate message #{candidate.Id}: expected '{normalizedRole}', found '{candidateRole ?? "<missing>"}'.");
        }

        if (diagnostics.Count == 0)
            diagnostics.Add("No completion packets were found for the supplied task/query scope.");
        return diagnostics;
    }

    private static string BuildCompletionContent(
        PiSessionDetail detail,
        string role,
        string status,
        string packetType,
        string summary,
        string? branch,
        string? headCommit,
        string? baseCommit,
        string? testsRun,
        int? reviewRoundId,
        string? findingIds,
        string? failureCategory,
        string? recoveryGuidance,
        bool malformed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {ToTitle(packetType)}");
        sb.AppendLine();
        sb.AppendLine("## Worker completion");
        sb.AppendLine($"- Project: `{detail.Session.ProjectId}`");
        sb.AppendLine($"- Task: `{detail.Session.TaskId?.ToString() ?? "none"}`");
        sb.AppendLine($"- Run: `{RunId(detail.Session)}`");
        sb.AppendLine($"- Session: `{detail.Session.SessionId}`");
        sb.AppendLine($"- Role: `{role}`");
        sb.AppendLine($"- Status: `{status}`");
        sb.AppendLine($"- Packet type: `{packetType}`");
        if (malformed)
            sb.AppendLine("- Completion state: `malformed`");
        if (!string.IsNullOrWhiteSpace(failureCategory))
            sb.AppendLine($"- Failure category: `{failureCategory}`");
        if (reviewRoundId is not null)
            sb.AppendLine($"- Review round: `#{reviewRoundId}`");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine(summary.Trim());
        sb.AppendLine();
        sb.AppendLine("## Repo metadata");
        sb.AppendLine($"- Branch: `{branch ?? "not reported"}`");
        sb.AppendLine($"- Base commit: `{baseCommit ?? "not reported"}`");
        sb.AppendLine($"- Head commit: `{headCommit ?? "not reported"}`");
        sb.AppendLine();
        sb.AppendLine("## Tests / verification");
        sb.AppendLine(string.IsNullOrWhiteSpace(testsRun) ? "- Not reported." : $"```json\n{testsRun.Trim()}\n```");
        if (!string.IsNullOrWhiteSpace(findingIds))
        {
            sb.AppendLine();
            sb.AppendLine("## Finding IDs");
            sb.AppendLine($"```json\n{findingIds.Trim()}\n```");
        }
        if (!string.IsNullOrWhiteSpace(recoveryGuidance))
        {
            sb.AppendLine();
            sb.AppendLine("## Recovery guidance");
            sb.AppendLine(recoveryGuidance.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    private static JsonElement BuildMetadata(
        PiSessionDetail detail,
        string role,
        string status,
        string packetType,
        string suppliedRunOrSessionId,
        string suppliedRole,
        string? branch,
        string? headCommit,
        string? baseCommit,
        string? testsRun,
        int? reviewRoundId,
        string? findingIds,
        string? failureCategory,
        string? recoveryGuidance,
        string? dedupeKey,
        bool malformed)
    {
        var obj = new Dictionary<string, object?>
        {
            ["type"] = packetType,
            ["packet_kind"] = packetType,
            ["schema"] = "den_worker_completion",
            ["schema_version"] = 1,
            ["completion_packet"] = true,
            ["malformed"] = malformed,
            ["status"] = status,
            ["role"] = role,
            ["supplied_role"] = NormalizeRole(suppliedRole),
            ["project_id"] = detail.Session.ProjectId,
            ["task_id"] = detail.Session.TaskId,
            ["run_id"] = RunId(detail.Session),
            ["session_id"] = detail.Session.SessionId,
            ["branch"] = NullIfWhiteSpace(branch),
            ["head_commit"] = NullIfWhiteSpace(headCommit),
            ["base_commit"] = NullIfWhiteSpace(baseCommit),
            ["tests_run"] = ParseJsonOrString(testsRun),
            ["review_round_id"] = reviewRoundId,
            ["finding_ids"] = ParseJsonOrString(findingIds),
            ["failure_category"] = NullIfWhiteSpace(failureCategory),
            ["recovery_guidance"] = NullIfWhiteSpace(recoveryGuidance),
            ["dedupe_key"] = NullIfWhiteSpace(dedupeKey),
            ["identity_provenance"] = "server_derived_from_worker_run",
            ["identity_validation"] = "matched_worker_run_record",
            ["provided_run_or_session_id"] = suppliedRunOrSessionId,
        };
        return JsonSerializer.SerializeToElement(obj, JsonOptions);
    }

    private static string SerializeCompletionResult(Message message, string idempotencyStatus, string completionState, bool verbose)
    {
        var result = new
        {
            summary = $"{idempotencyStatus} worker completion message #{message.Id} ({MetadataString(message, "status") ?? "unknown"})",
            idempotency = new { status = idempotencyStatus },
            completion_state = completionState,
            completion = new
            {
                message_id = message.Id,
                project_id = message.ProjectId,
                task_id = message.TaskId,
                run_id = MetadataString(message, "run_id"),
                session_id = MetadataString(message, "session_id"),
                role = MetadataString(message, "role"),
                supplied_role = MetadataString(message, "supplied_role"),
                status = MetadataString(message, "status"),
                packet_type = MetadataString(message, "type"),
                failure_category = MetadataString(message, "failure_category"),
                recovery_guidance = MetadataString(message, "recovery_guidance"),
                review_round_id = MetadataInt(message, "review_round_id"),
                metadata = message.Metadata,
                content = message.Content,
                final_repo = new
                {
                    branch = MetadataString(message, "branch"),
                    base_commit = MetadataString(message, "base_commit"),
                    head_commit = MetadataString(message, "head_commit"),
                },
                created_at = message.CreatedAt,
            }
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static MessageIntent CompletionIntent(string status, string packetType) => packetType switch
    {
        "review_findings_packet" => MessageIntent.ReviewFeedback,
        _ when status == "completed" => MessageIntent.StatusUpdate,
        _ => MessageIntent.TaskBlocked,
    };

    private static bool IsCompletion(Message message) =>
        MetadataBool(message, "completion_packet") || string.Equals(MetadataString(message, "schema"), "den_worker_completion", StringComparison.Ordinal);

    private static string? NormalizeStatus(string? value)
    {
        var normalized = NormalizeToken(value);
        return normalized is not null && ValidStatuses.Contains(normalized) ? normalized : null;
    }

    private static string? NormalizePacketType(string? value)
    {
        var normalized = NormalizeToken(value);
        return normalized is not null && ValidPacketTypes.Contains(normalized) ? normalized : null;
    }

    private static string NormalizeRole(string? value) => NormalizeToken(value) ?? "worker";

    private static string? DurableRole(PiSessionDetail detail)
    {
        var role = NormalizeToken(detail.Session.ToolProfile)
            ?? NormalizeToken(detail.LaunchProfile?.WorkerRole);
        return role;
    }

    private static string? NormalizeToken(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant().Replace('-', '_');
    private static string? ResolveFailureCategory(string status, string? provided, bool malformed)
    {
        if (malformed)
            return "malformed_completion";
        if (!string.IsNullOrWhiteSpace(provided))
            return NormalizeToken(provided);
        return status switch
        {
            "completed" => null,
            "blocked" => "blocked",
            "needs_input" => "needs_input",
            "incomplete" => "incomplete",
            "failed" => "worker_failed",
            _ => "malformed_packet",
        };
    }

    private static List<string> ValidateSubmittedRunId(string? runId)
    {
        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(runId))
        {
            diagnostics.Add("run_id/session_id is required; workers must pass DEN_WORKER_RUN_ID exactly when available.");
            return diagnostics;
        }

        if (IsSuspiciousRunId(runId))
            diagnostics.Add("run_id/session_id contains shell syntax or placeholder text; read DEN_WORKER_RUN_ID literally and do not invent or template run ids.");
        return diagnostics;
    }

    private static List<string> ValidatePacketClaims(string? status, string? packetType, string? branch, string? headCommit, string? testsRun)
    {
        var diagnostics = new List<string>();
        if (status == "completed" && packetType == "implementation_packet")
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(branch)) missing.Add("branch");
            if (string.IsNullOrWhiteSpace(headCommit)) missing.Add("head_commit");
            if (string.IsNullOrWhiteSpace(testsRun)) missing.Add("tests_run");
            if (missing.Count > 0)
                diagnostics.Add($"Completed implementation packets are missing {string.Join(", ", missing)} metadata; report exact branch, head commit, and test/validation results.");
        }
        return diagnostics;
    }

    private static bool IsSuspiciousRunId(string? runId) => !string.IsNullOrWhiteSpace(runId) && ShellSyntaxPattern.IsMatch(runId);

    private static string AppendRecoveryGuidance(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing.Trim()} {addition}";

    private static string SerializeRejectedCompletion(string completionState, string failureCategory, IReadOnlyList<string> diagnostics) =>
        JsonSerializer.Serialize(new
        {
            summary = diagnostics.FirstOrDefault() ?? "worker completion rejected",
            completion_state = completionState,
            failure_category = failureCategory,
            diagnostics
        }, JsonOptions);

    private static object? ParseJsonOrString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(value);
        }
        catch (JsonException)
        {
            return value.Trim();
        }
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

    private static string RunId(PiSessionSummary session) => string.IsNullOrWhiteSpace(session.RunId) ? session.SessionId : session.RunId!;
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ToTitle(string value) => string.Join(' ', value.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, JsonOptions);
}
