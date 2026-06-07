using System.ComponentModel;
using System.Text.Json;
using DenCore.Data;
using DenCore.Mcp;
using DenCore.Models;
using ModelContextProtocol.Server;
using TaskStatus = DenCore.Models.TaskStatus;

namespace DenCore.Service.Tools;

/// <summary>
/// Retry-cap calibration report tool for worker-orchestrator loop observability.
/// Queries task messages/completions across a project to detect retry-cap pressure,
/// planner authorizations, and post-cap outcomes — all from structured metadata,
/// not prose parsing.
/// </summary>
[McpServerToolType]
public sealed class RetryCapCalibrationTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    /// <summary>
    /// Canonical worker roles that participate in the retry-cap workflow.
    /// </summary>
    private static readonly string[] WorkerRoles = ["coder", "reviewer", "validator", "drift_checker", "packet_auditor"];

    /// <summary>
    /// Completion packet types mapped to their worker roles for retry counting.
    /// </summary>
    private static readonly Dictionary<string, string> PacketTypeToRole = new(StringComparer.Ordinal)
    {
        ["implementation_packet"] = "coder",
        ["review_findings_packet"] = "reviewer",
        ["validation_packet"] = "validator",
        ["drift_check_packet"] = "drift_checker",
        ["packet_audit_packet"] = "packet_auditor",
    };

    [McpToolProfile("admin-current", "planner", "runner")]
    [McpToolBundle("orchestrator")]
    [McpServerTool(Name = "retry_cap_report"), Description(
        "Generate a retry-cap calibration report over a configurable time window " +
        "and project filter. Reports tasks that reached the retry cap, which role/gate " +
        "hit the cap, attempts observed by role, whether an extra retry was authorized " +
        "by planner, and outcome after the extra retry. Uses structured message metadata " +
        "(completion packets, retry_cap_escalation events, planner_retry_authorization) — " +
        "no prose parsing.")]
    public static async Task<string> RetryCapReport(
        ITaskRepository tasks,
        IMessageRepository messages,
        [Description("Project ID.")] string project_id,
        [Description("ISO datetime — only tasks with messages after this time.")] string? since = null,
        [Description("Configured retry cap for calibration (default 3). Used as baseline for cap-hit detection.")] int max_attempts = 3,
        [Description("If true, include terminal tasks (done, cancelled) in addition to active ones.")] bool include_terminal = false,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var effectiveMax = Math.Max(1, max_attempts);
        var sinceDate = since is not null ? DateTime.Parse(since) : (DateTime?)null;

        // Gather candidate task IDs — filter by project and status
        var taskSummaries = await tasks.ListAsync(project_id,
            statuses: include_terminal ? null : [TaskStatus.Planned, TaskStatus.InProgress, TaskStatus.Review, TaskStatus.Blocked]);
        var candidateTaskIds = taskSummaries.Select(t => t.Id).ToList();

        if (candidateTaskIds.Count == 0)
        {
            var emptyResult = new
            {
                summary = $"No active tasks found in project '{project_id}'.",
                project_id,
                max_attempts = effectiveMax,
                since = since,
                items = Array.Empty<object>(),
                calibration_guidance = "No retry-cap pressure detected; no active tasks to evaluate.",
            };
            return JsonSerializer.Serialize(emptyResult, JsonOptions);
        }

        var reportItems = new List<object>();

        foreach (var taskId in candidateTaskIds)
        {
            var taskMessagesFull = await messages.GetMessagesAsync(project_id, taskId: taskId, limit: 100);
            var taskMessages = taskMessagesFull
                .Where(m => sinceDate is null || m.CreatedAt >= sinceDate)
                .ToList();

            if (taskMessages.Count == 0)
                continue;

            // Count completion/failure attempts by role
            var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var role in WorkerRoles)
                attempts[role] = 0;

            // Track latest completion per role for status/outcome
            var latestByRole = new Dictionary<string, (string status, int messageId, string? failureCategory)>(StringComparer.Ordinal);

            // Detect planner authorization messages
            bool plannerAuthorized = false;
            int? authorizationMessageId = null;

            foreach (var msg in taskMessages)
            {
                // Is this a worker completion or failure packet?
                if (IsWorkerPacket(msg))
                {
                    var role = MetadataString(msg, "role") ?? PacketRoleFromType(MetadataString(msg, "type"));
                    if (role is not null && attempts.ContainsKey(role))
                    {
                        attempts[role]++;
                        var status = MetadataString(msg, "status") ?? "unknown";
                        var failureCategory = MetadataString(msg, "failure_category");
                        latestByRole[role] = (status, msg.Id, failureCategory);
                    }
                }

                // Is this a planner retry authorization?
                if (IsPlannerAuthorization(msg))
                {
                    plannerAuthorized = true;
                    authorizationMessageId = msg.Id;
                }
            }

            // Determine which roles hit the retry cap
            var capHits = new List<object>();
            foreach (var (role, count) in attempts)
            {
                if (count >= effectiveMax && latestByRole.TryGetValue(role, out var latest))
                {
                    capHits.Add(new
                    {
                        role,
                        attempts = count,
                        max_attempts = effectiveMax,
                        latest_status = latest.status,
                        latest_packet_id = latest.messageId,
                        failure_category = latest.failureCategory,
                    });
                }
            }

            if (capHits.Count == 0)
                continue;

            // Get latest task status
            var taskDetail = await tasks.GetDetailAsync(taskId);
            var taskStatus = taskDetail.Task.Status.ToDbValue();

            // Determine overall outcome
            string outcome;
            if (plannerAuthorized)
            {
                // Check if any role succeeded after authorization
                var postAuthSuccess = attempts.Any(kvp =>
                    kvp.Value > effectiveMax &&
                    latestByRole.TryGetValue(kvp.Key, out var latest) &&
                    latest.status == "completed");
                if (postAuthSuccess)
                    outcome = "completed_after_extra_retry";
                else if (taskStatus == "cancelled")
                    outcome = "cancelled";
                else if (taskStatus == "in_progress" || taskStatus == "review" || taskStatus == "planned")
                    outcome = "in_progress";
                else
                    outcome = "blocked_after_extra_retry";
            }
            else
            {
                if (taskStatus == "cancelled")
                    outcome = "cancelled";
                else
                    outcome = "blocked_at_retry_cap";
            }

            // Find latest blocker category across all cap-hit roles
            var blockerCategories = capHits
                .Select(h => (string?)((dynamic)h).failure_category)
                .Where(c => c is not null)
                .Distinct()
                .ToList();

            reportItems.Add(new
            {
                task_id = taskId,
                title = taskDetail.Task.Title,
                status = taskDetail.Task.Status.ToDbValue(),
                outcome,
                cap_hits = capHits,
                attempts = new
                {
                    coder = attempts.GetValueOrDefault("coder"),
                    reviewer = attempts.GetValueOrDefault("reviewer"),
                    validator = attempts.GetValueOrDefault("validator"),
                    drift_checker = attempts.GetValueOrDefault("drift_checker"),
                    packet_auditor = attempts.GetValueOrDefault("packet_auditor"),
                },
                planner_authorized = plannerAuthorized,
                authorization_message_id = authorizationMessageId,
                blocker_categories = blockerCategories,
                latest_packet_ids = latestByRole.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.messageId),
            });
        }

        // Calibration guidance
        var totalTasks = reportItems.Count;
        var completedAfterRetry = reportItems.Count(i => ((string)((dynamic)i).outcome) == "completed_after_extra_retry");
        var blockedAtCap = reportItems.Count(i => ((string)((dynamic)i).outcome) == "blocked_at_retry_cap");
        var blockedAfterRetry = reportItems.Count(i => ((string)((dynamic)i).outcome) == "blocked_after_extra_retry");
        var inProgress = reportItems.Count(i => ((string)((dynamic)i).outcome) == "in_progress");
        var cancelled = reportItems.Count(i => ((string)((dynamic)i).outcome) == "cancelled");

        string guidance;
        if (totalTasks == 0)
        {
            guidance = "No retry-cap pressure detected in the current window. The default cap of " +
                       $"{effectiveMax} appears adequate, but this may reflect low task volume rather than correct calibration.";
        }
        else if (completedAfterRetry > 0 && blockedAfterRetry == 0 && inProgress == 0)
        {
            guidance = $"{completedAfterRetry}/{totalTasks} cap-hit tasks completed after a Planner-authorized extra retry. " +
                       "If Planner intervention is routinely rubber-stamping, consider raising the default cap from " +
                       $"{effectiveMax} to {effectiveMax + 1}. If extra retries frequently widen scope, keep Planner escalation.";
        }
        else if (blockedAfterRetry > 0 && completedAfterRetry == 0)
        {
            guidance = $"{blockedAfterRetry}/{totalTasks} cap-hit tasks remain blocked even after Planner-authorized extra retries. " +
                       $"The default cap of {effectiveMax} appears reasonable — tasks that reach the cap genuinely need Planner attention. " +
                       "Do not raise the default cap; consider splitting or re-scoping cap-hit tasks instead.";
        }
        else if (cancelled > 0 && completedAfterRetry == 0)
        {
            guidance = $"{cancelled}/{totalTasks} cap-hit tasks were cancelled after reaching the retry cap. " +
                       $"The default cap of {effectiveMax} appears reasonable — these tasks typically required re-scoping rather than more retries.";
        }
        else
        {
            guidance = $"{totalTasks} tasks hit retry cap ({completedAfterRetry} resolved after extra retry, " +
                       $"{blockedAfterRetry} still blocked after extra retry, {blockedAtCap} blocked at cap, " +
                       $"{inProgress} in progress). " +
                       "Mixed signal — consider per-role or per-project cap tuning rather than a global change.";
        }

        // Add non-cap blockers detected (tasks blocked but not from retry-cap pressure)
        // This is a placeholder — operational blockers require separate tracking
        var nonCapBlockers = new List<string>();
        foreach (var item in reportItems)
        {
            var cats = (List<string>?)((dynamic)item).blocker_categories;
            if (cats is not null)
            {
                foreach (var cat in cats)
                {
                    if (!nonCapBlockers.Contains(cat))
                        nonCapBlockers.Add(cat);
                }
            }
        }

        var result = new
        {
            summary = $"Retry-cap report for '{project_id}': {totalTasks} task(s) hit retry cap " +
                      $"({completedAfterRetry} resolved, {blockedAtCap} blocked at cap, {blockedAfterRetry} blocked after extra retry, " +
                      $"{inProgress} in progress, {cancelled} cancelled)",
            project_id,
            max_attempts = effectiveMax,
            since,
            total_tasks_evaluated = candidateTaskIds.Count,
            tasks_hitting_cap = totalTasks,
            completed_after_extra_retry = completedAfterRetry,
            blocked_at_cap = blockedAtCap,
            blocked_after_extra_retry = blockedAfterRetry,
            in_progress = inProgress,
            cancelled,
            calibration_guidance = guidance,
            items = verbose ? reportItems : reportItems.Take(20).ToList(),
            non_cap_blocker_categories = nonCapBlockers,
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Message metadata helpers
    // -----------------------------------------------------------------------

    private static bool IsWorkerPacket(Message message) =>
        MetadataBool(message, "completion_packet")
        || string.Equals(MetadataString(message, "schema"), "den_worker_completion", StringComparison.Ordinal);

    private static bool IsPlannerAuthorization(Message message) =>
        string.Equals(MetadataString(message, "type"), "planner_retry_authorization", StringComparison.Ordinal);

    private static string? PacketRoleFromType(string? packetType) =>
        packetType is not null && PacketTypeToRole.TryGetValue(packetType, out var role) ? role : null;

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
}
