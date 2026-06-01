using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

/// <summary>
/// Core-owned blocked task escalation policy. When a task is marked blocked,
/// this service ensures the transition is not silent:
///   - Validates structured blocker context is present.
///   - If a planner/conductor binding exists for the project, sends a waking message.
///   - If no planner is reachable, creates a Core-backed user notification for Patch.
///   - Dedupes repeated unresolved blocked writes for the same task.
/// </summary>
public interface IBlockedTaskEscalationService
{
    /// <summary>
    /// Validate the blocker context for a blocked transition.
    /// Returns validation result with errors if context is insufficient.
    /// </summary>
    BlockerContextValidation ValidateBlockerContext(string? blockerSummary, string? reason);

    /// <summary>
    /// Escalate a blocked task transition. If a planner binding exists, sends a wake
    /// message. Otherwise, creates a user notification. Dedupes if an unresolved
    /// escalation already exists for the same task.
    /// </summary>
    Task<BlockedTaskEscalationResult> EscalateBlockedTaskAsync(
        ProjectTask task,
        BlockedTaskEscalation escalation);
}

public sealed class BlockedTaskEscalationService : IBlockedTaskEscalationService
{
    private readonly IAgentInstanceBindingRepository _bindings;
    private readonly IMessageRepository _messages;
    private readonly IAgentStreamRepository _stream;
    private readonly ILogger<BlockedTaskEscalationService> _logger;
    private readonly BlockedTaskEscalationPolicyOptions _policyOptions;

    // Planner/conductor roles that can handle blocker escalations
    private static readonly string[] PlannerRoles = ["planner", "conductor"];

    public BlockedTaskEscalationService(
        IAgentInstanceBindingRepository bindings,
        IMessageRepository messages,
        IAgentStreamRepository stream,
        ILogger<BlockedTaskEscalationService> logger,
        BlockedTaskEscalationPolicyOptions? policyOptions = null)
    {
        _bindings = bindings;
        _messages = messages;
        _stream = stream;
        _logger = logger;
        _policyOptions = policyOptions ?? new BlockedTaskEscalationPolicyOptions();
    }

    public BlockerContextValidation ValidateBlockerContext(string? blockerSummary, string? reason)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(blockerSummary))
            errors.Add("Blocker summary is required when marking a task blocked.");

        if (string.IsNullOrWhiteSpace(reason))
            errors.Add("Reason (why the agent cannot proceed) is required when marking a task blocked.");

        return errors.Count > 0
            ? BlockerContextValidation.Invalid([..errors])
            : BlockerContextValidation.Valid();
    }

    public async Task<BlockedTaskEscalationResult> EscalateBlockedTaskAsync(
        ProjectTask task,
        BlockedTaskEscalation escalation)
    {
        var blockerSignature = ComputeBlockerSignature(escalation);

        // 1. Check for duplicate unresolved escalation for this blocker signature.
        if (await HasUnresolvedEscalationAsync(task.Id, task.ProjectId, blockerSignature))
        {
            _logger.LogInformation(
                "Skipping duplicate blocked escalation for task {TaskId} in {ProjectId} with signature {Signature}",
                task.Id, task.ProjectId, blockerSignature);

            return new BlockedTaskEscalationResult
            {
                WasNew = false,
                SkipReason = "Unresolved blocked escalation already exists for this task and blocker signature."
            };
        }

        // 2. Try to find a planner/conductor binding for the project
        var plannerBinding = await FindPlannerBindingAsync(task.ProjectId);

        var result = new BlockedTaskEscalationResult { WasNew = true };

        if (plannerBinding is not null)
        {
            // 3a. Send planner wake message via agent stream
            var plannerMessage = await SendPlannerWakeMessageAsync(task, escalation, plannerBinding, blockerSignature);
            result.PlannerWakeAttempted = true;
            result.PlannerWakeMessageId = plannerMessage?.Id;

            // If a planner binding exists but the wake path fails, fall back to a user
            // notification so the blocked transition still has an attention route.
            if (plannerMessage is null)
            {
                var notification = await CreateUserNotificationAsync(task, escalation, blockerSignature);
                result.UserNotificationCreated = true;
                result.UserNotificationMessageId = notification?.Id;
            }
        }

        // 3b. If no planner reachable, create user notification for Patch
        if (plannerBinding is null)
        {
            var notification = await CreateUserNotificationAsync(task, escalation, blockerSignature);
            result.UserNotificationCreated = true;
            result.UserNotificationMessageId = notification?.Id;
        }

        _logger.LogInformation(
            "Blocked escalation processed for task {TaskId} in {ProjectId}: PlannerWake={PlannerWake}, Notification={Notification}",
            task.Id, task.ProjectId, result.PlannerWakeAttempted, result.UserNotificationCreated);

        return result;
    }

    /// <summary>
    /// Check whether this exact blocker signature was escalated recently. Both routing paths
    /// matter: no-planner fallback notifications and planner/conductor wake stream entries.
    /// Dedup window is configurable; a zero or negative window disables dedupe.
    /// </summary>
    private async Task<bool> HasUnresolvedEscalationAsync(int taskId, string projectId, string blockerSignature)
    {
        var dedupWindow = _policyOptions.DedupWindow;
        if (dedupWindow <= TimeSpan.Zero)
            return false;

        var cutoff = DateTime.UtcNow.Subtract(dedupWindow);

        var recentNotifications = await _messages.GetMessagesAsync(
            projectId: projectId,
            taskId: taskId,
            intent: MessageIntent.Notification,
            limit: 25);

        foreach (var msg in recentNotifications)
        {
            if (msg.CreatedAt <= cutoff)
                continue;

            if (IsMatchingEscalationMetadata(msg.Metadata, "blocker_attention_required", blockerSignature))
                return true;
        }

        var recentStreamEntries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = projectId,
            TaskId = taskId,
            EventType = "task_blocked_escalation",
            IncludeDebug = true,
            Limit = 25
        });

        foreach (var entry in recentStreamEntries)
        {
            if (entry.CreatedAt <= cutoff)
                continue;

            if (IsMatchingEscalationMetadata(entry.Metadata, "task_blocked_escalation", blockerSignature))
                return true;
        }

        return false;
    }

    private async Task<AgentInstanceBinding?> FindPlannerBindingAsync(string projectId)
    {
        foreach (var role in PlannerRoles)
        {
            var candidates = await _bindings.ListAsync(new AgentInstanceBindingListOptions
            {
                ProjectId = projectId,
                Role = role,
                Statuses = [AgentInstanceBindingStatus.Active, AgentInstanceBindingStatus.Degraded]
            });

            if (candidates.Count > 0)
                return candidates[0]; // Return first active planner binding
        }

        return null;
    }

    private async Task<AgentStreamEntry?> SendPlannerWakeMessageAsync(
        ProjectTask task,
        BlockedTaskEscalation escalation,
        AgentInstanceBinding plannerBinding,
        string blockerSignature)
    {
        var body = FormatPlannerWakeBody(task, escalation);
        var metadata = JsonSerializer.SerializeToElement(new
        {
            type = "task_blocked_escalation",
            blocker_signature = blockerSignature,
            task_id = task.Id,
            project_id = task.ProjectId,
            target_role = plannerBinding.Role,
            escalation_context = new
            {
                blocker_summary = escalation.BlockerSummary,
                reason = escalation.Reason,
                attempted_remedies = escalation.AttemptedRemedies,
                suggested_next_step = escalation.SuggestedNextStep,
                requires_human_input = escalation.RequiresHumanInput,
                changed_by = escalation.ChangedBy
            }
        });

        try
        {
            var entry = await _stream.AppendAsync(new AgentStreamEntry
            {
                StreamKind = AgentStreamKind.Message,
                EventType = "task_blocked_escalation",
                ProjectId = task.ProjectId,
                TaskId = task.Id,
                Sender = "den-core",
                RecipientAgent = plannerBinding.AgentIdentity,
                RecipientRole = plannerBinding.Role,
                RecipientInstanceId = plannerBinding.InstanceId,
                DeliveryMode = AgentStreamDeliveryMode.Wake,
                Body = body,
                Metadata = metadata,
                DedupKey = $"blocked-escalation:{task.Id}:{blockerSignature}"
            });

            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send planner wake for blocked task {TaskId} in {ProjectId}",
                task.Id, task.ProjectId);
            return null;
        }
    }

    private async Task<Message?> CreateUserNotificationAsync(
        ProjectTask task,
        BlockedTaskEscalation escalation,
        string blockerSignature)
    {
        var content = FormatUserNotificationContent(task, escalation);
        var metadata = JsonSerializer.SerializeToElement(new
        {
            type = "blocker_attention_required",
            subtype = "blocker_attention_required",
            blocker_signature = blockerSignature,
            urgency = "high",
            task_id = task.Id,
            project_id = task.ProjectId,
            blocker_summary = escalation.BlockerSummary,
            reason = escalation.Reason,
            attempted_remedies = escalation.AttemptedRemedies,
            suggested_next_step = escalation.SuggestedNextStep,
            requires_human_input = escalation.RequiresHumanInput,
            changed_by = escalation.ChangedBy
        });

        try
        {
            var message = await _messages.CreateAsync(new Message
            {
                ProjectId = task.ProjectId,
                TaskId = task.Id,
                Sender = "den-core",
                Content = content,
                Intent = MessageIntent.Notification,
                Metadata = metadata
            });

            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create user notification for blocked task {TaskId} in {ProjectId}",
                task.Id, task.ProjectId);
            return null;
        }
    }

    private static string FormatPlannerWakeBody(ProjectTask task, BlockedTaskEscalation escalation)
    {
        return $"""
            ## Task Blocked: #{task.Id} — {task.Title}

            **Blocker:** {escalation.BlockerSummary}

            **Why blocked:** {escalation.Reason}

            {(string.IsNullOrEmpty(escalation.AttemptedRemedies) ? "" : $"**Attempted remedies:** {escalation.AttemptedRemedies}\n\n")}
            {(string.IsNullOrEmpty(escalation.SuggestedNextStep) ? "" : $"**Suggested next step:** {escalation.SuggestedNextStep}\n\n")}
            **Requires human input:** {(escalation.RequiresHumanInput ? "Yes" : "No")}

            **Changed by:** {escalation.ChangedBy}

            Please review and decide whether to split, replan, or escalate to Patch.
            """;
    }

    private static string FormatUserNotificationContent(ProjectTask task, BlockedTaskEscalation escalation)
    {
        return $"""
            ## Blocked Task Requires Attention

            Task **#{task.Id} — {task.Title}** in project `{task.ProjectId}` has been blocked and no planner is available to handle the escalation.

            **Blocker:** {escalation.BlockerSummary}

            **Why blocked:** {escalation.Reason}

            {(string.IsNullOrEmpty(escalation.AttemptedRemedies) ? "" : $"**Attempted remedies:** {escalation.AttemptedRemedies}\n\n")}
            {(string.IsNullOrEmpty(escalation.SuggestedNextStep) ? "" : $"**Suggested next step:** {escalation.SuggestedNextStep}\n\n")}
            **Requires human input:** {(escalation.RequiresHumanInput ? "Yes" : "No")}

            **Changed by:** {escalation.ChangedBy}
            """;
    }

    private static string ComputeBlockerSignature(BlockedTaskEscalation escalation)
    {
        var normalized = string.Join("\n", new[]
        {
            escalation.BlockerSummary.Trim(),
            escalation.Reason.Trim(),
            escalation.AttemptedRemedies?.Trim() ?? string.Empty,
            escalation.SuggestedNextStep?.Trim() ?? string.Empty,
            escalation.RequiresHumanInput ? "human" : "planner"
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    private static bool IsMatchingEscalationMetadata(JsonElement? metadata, string type, string blockerSignature)
    {
        if (metadata is not JsonElement meta ||
            !meta.TryGetProperty("type", out var typeEl) ||
            typeEl.GetString() != type ||
            !meta.TryGetProperty("blocker_signature", out var signatureEl))
        {
            return false;
        }

        return signatureEl.GetString() == blockerSignature;
    }
}
