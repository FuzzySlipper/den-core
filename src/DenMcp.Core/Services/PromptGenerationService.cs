using System.Text;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IPromptGenerationService
{
    /// <summary>
    /// Generate a contextual prompt for an agent based on the dispatch event and routing config.
    /// Returns both a short summary (for notifications/UI) and a full context prompt.
    /// </summary>
    Task<PromptResult> GenerateAsync(DispatchEvent evt, RoutingTrigger trigger, RoutingConfig config);
}

public sealed class PromptResult
{
    /// <summary>Short one-line summary for notifications and queue UIs.</summary>
    public required string Summary { get; init; }

    /// <summary>Full contextual prompt for the agent.</summary>
    public required string ContextPrompt { get; init; }
}

public sealed class PromptGenerationService : IPromptGenerationService
{
    private readonly ITaskRepository _tasks;
    private readonly IMessageRepository _messages;
    private readonly IRoutingService _routing;

    public PromptGenerationService(ITaskRepository tasks, IMessageRepository messages, IRoutingService routing)
    {
        _tasks = tasks;
        _messages = messages;
        _routing = routing;
    }

    public async Task<PromptResult> GenerateAsync(DispatchEvent evt, RoutingTrigger trigger, RoutingConfig config)
    {
        // If the trigger has a custom template, use it for the opening line
        string? customOpener = null;
        if (trigger.PromptTemplate is not null)
            customOpener = _routing.InterpolateTemplate(trigger.PromptTemplate, evt);

        return evt.EventKind switch
        {
            DispatchEvent.TaskStatusChanged when evt.ToStatus is "review"
                => await BuildReviewPrompt(evt, config, customOpener),
            DispatchEvent.TaskStatusChanged when evt.FromStatus is "review"
                => await BuildFeedbackPrompt(evt, config, customOpener),
            DispatchEvent.TaskStatusChanged
                => await BuildTaskTransitionPrompt(evt, config, customOpener),
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.ReviewRequest
                => await BuildReviewRequestMessagePrompt(evt, config, customOpener),
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.Handoff
                => await BuildHandoffPrompt(evt, config, customOpener),
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.ReviewFeedback
                => await BuildReviewFeedbackMessagePrompt(evt, config, customOpener),
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.ReviewApproval
                => await BuildMergeRequestPrompt(evt, config, customOpener),
            DispatchEvent.MessageReceived
                => await BuildMessagePrompt(evt, config, customOpener),
            _ => BuildFallbackPrompt(evt, customOpener)
        };
    }

    private async Task<PromptResult> BuildReviewPrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();
        var role = ResolveRoleName(config, "reviewer");
        var branch = evt.Branch ?? $"task/{evt.TaskId}-*";

        sb.AppendLine(customOpener ?? $"Review task #{evt.TaskId} ({evt.TaskTitle}) on branch {branch}.");
        sb.AppendLine();
        sb.AppendLine($"You are the {role} for {evt.ProjectId}.");
        sb.AppendLine();
        sb.AppendLine($"**Task #{evt.TaskId}**: {evt.TaskTitle}");
        sb.AppendLine($"**Branch**: {branch}");
        sb.AppendLine($"**Status**: review");
        sb.AppendLine();
        sb.AppendLine("Review the changes: `git diff main...HEAD`");

        await AppendRecentMessages(sb, evt);

        sb.AppendLine();
        AppendReviewerWorkflowGuidance(sb);
        sb.AppendLine();
        sb.AppendLine("Post your review findings as a message to the task.");
        sb.AppendLine("If changes needed: set task status back to planned.");
        sb.AppendLine("If approved: send approval/merge handoff so the implementer can merge the reviewed head and set the task status to done.");

        return new PromptResult
        {
            Summary = $"{evt.Sender} requested review on #{evt.TaskId} ({evt.TaskTitle})",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private async Task<PromptResult> BuildFeedbackPrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();
        var role = ResolveRoleName(config, "implementer");
        var branch = evt.Branch ?? $"task/{evt.TaskId}-*";

        sb.AppendLine(customOpener ?? $"Task #{evt.TaskId} ({evt.TaskTitle}) has review feedback. Address the findings on branch {branch}.");
        sb.AppendLine();
        sb.AppendLine($"You are the {role} for {evt.ProjectId}.");
        sb.AppendLine();
        sb.AppendLine($"**Task #{evt.TaskId}**: {evt.TaskTitle}");
        sb.AppendLine($"**Branch**: {branch}");
        sb.AppendLine($"**Status**: planned (returned from review)");

        await AppendRecentMessages(sb, evt);

        sb.AppendLine();
        AppendImplementerWorkflowGuidance(sb);
        sb.AppendLine();
        sb.AppendLine("Address the review feedback, then set task status to review when ready.");

        return new PromptResult
        {
            Summary = $"Review feedback on #{evt.TaskId} ({evt.TaskTitle})",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private async Task<PromptResult> BuildReviewRequestMessagePrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();
        var role = ResolveRoleName(config, "reviewer");
        var branch = evt.Branch ?? (evt.TaskId.HasValue ? $"task/{evt.TaskId}-*" : null);

        sb.AppendLine(customOpener ?? $"Review request is ready on {evt.ProjectId} from {evt.Sender}.");
        sb.AppendLine();
        sb.AppendLine($"You are the {role} for {evt.ProjectId}.");
        AppendAddressingDetails(sb, evt);
        if (branch is not null)
            sb.AppendLine($"**Branch**: {branch}");
        sb.AppendLine();

        AppendTriggeringMessage(sb, evt);
        await AppendTaskAndMessages(sb, evt);

        sb.AppendLine();
        AppendReviewerWorkflowGuidance(sb);
        sb.AppendLine();
        if (branch is not null)
            sb.AppendLine($"Review the requested changes on `{branch}` and use `git diff main...HEAD` if needed.");
        else
            sb.AppendLine("Review the requested changes and inspect the task thread for branch/head context if needed.");
        sb.AppendLine("Post your review findings as a message to the task.");
        sb.AppendLine("If changes are needed, send feedback back to the implementer.");
        sb.AppendLine("If approved, send approval/merge handoff so the implementer can merge the reviewed head and set the task status to done.");

        var summaryTask = evt.TaskId.HasValue ? $" on #{evt.TaskId}" : "";
        return new PromptResult
        {
            Summary = $"{evt.Sender} sent review request{summaryTask} on {evt.ProjectId}",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private async Task<PromptResult> BuildTaskTransitionPrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();
        var branch = evt.Branch ?? (evt.TaskId.HasValue ? $"task/{evt.TaskId}-*" : null);

        sb.AppendLine(customOpener ?? $"Task #{evt.TaskId} ({evt.TaskTitle}) moved to {evt.ToStatus}.");
        sb.AppendLine();
        sb.AppendLine($"**Task #{evt.TaskId}**: {evt.TaskTitle}");
        if (branch is not null)
            sb.AppendLine($"**Branch**: {branch}");
        sb.AppendLine($"**Status**: {evt.ToStatus} (was: {evt.FromStatus})");

        await AppendRecentMessages(sb, evt);

        sb.AppendLine();
        AppendImplementerWorkflowGuidance(sb);

        return new PromptResult
        {
            Summary = $"Task #{evt.TaskId} ({evt.TaskTitle}) moved to {evt.ToStatus}",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private async Task<PromptResult> BuildHandoffPrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();
        var handoffLabel = ResolveHandoffLabel(evt);

        sb.AppendLine(customOpener ?? $"You have a {handoffLabel} handoff on {evt.ProjectId} from {evt.Sender}.");
        sb.AppendLine();
        AppendAddressingDetails(sb, evt);

        AppendTriggeringMessage(sb, evt);
        await AppendTaskAndMessages(sb, evt);

        sb.AppendLine();
        AppendImplementerWorkflowGuidance(sb);
        sb.AppendLine();
        sb.AppendLine($"Review the {handoffLabel} context and proceed with the outlined work.");

        var summaryTask = evt.TaskId.HasValue ? $" on #{evt.TaskId}" : "";
        return new PromptResult
        {
            Summary = $"{evt.Sender} sent {handoffLabel} context{summaryTask} on {evt.ProjectId}",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private async Task<PromptResult> BuildReviewFeedbackMessagePrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();
        var role = ResolveRoleName(config, "implementer");

        sb.AppendLine(customOpener ?? $"Review feedback is ready on {evt.ProjectId} from {evt.Sender}.");
        sb.AppendLine();
        sb.AppendLine($"You are the {role} for {evt.ProjectId}.");
        AppendAddressingDetails(sb, evt);
        sb.AppendLine();

        AppendTriggeringMessage(sb, evt);
        await AppendTaskAndMessages(sb, evt);

        sb.AppendLine();
        AppendImplementerWorkflowGuidance(sb);
        sb.AppendLine();
        sb.AppendLine("Review the findings, decide what needs to change, and update the task branch.");
        sb.AppendLine("When you are ready, request review again with the new head commit and tests run.");

        var summaryTask = evt.TaskId.HasValue ? $" on #{evt.TaskId}" : "";
        return new PromptResult
        {
            Summary = $"{evt.Sender} sent review feedback{summaryTask} on {evt.ProjectId}",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private async Task<PromptResult> BuildMergeRequestPrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();
        var role = ResolveRoleName(config, "implementer");

        sb.AppendLine(customOpener ?? $"Review approved work on {evt.ProjectId} from {evt.Sender}.");
        sb.AppendLine();
        sb.AppendLine($"You are the {role} for {evt.ProjectId}.");
        AppendAddressingDetails(sb, evt);
        sb.AppendLine();

        AppendTriggeringMessage(sb, evt);
        await AppendTaskAndMessages(sb, evt);

        sb.AppendLine();
        sb.AppendLine("If the branch still matches the reviewed head commit in the thread, merge it and mark the task done.");
        sb.AppendLine("If the branch has new commits since that review, request review again with the new head SHA and tests run.");
        sb.AppendLine("After merging, request your next task. If nothing is available, send a work-complete update through your configured operator-notification path.");

        var summaryTask = evt.TaskId.HasValue ? $" on #{evt.TaskId}" : "";
        return new PromptResult
        {
            Summary = $"{evt.Sender} sent merge handoff{summaryTask} on {evt.ProjectId}",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private async Task<PromptResult> BuildMessagePrompt(DispatchEvent evt, RoutingConfig config, string? customOpener)
    {
        var sb = new StringBuilder();

        sb.AppendLine(customOpener ?? $"You have a message on {evt.ProjectId} from {evt.Sender}.");
        sb.AppendLine();
        AppendAddressingDetails(sb, evt);

        AppendTriggeringMessage(sb, evt);
        await AppendTaskAndMessages(sb, evt);

        sb.AppendLine();
        sb.AppendLine("Read the messages and take appropriate action.");

        var summaryType = evt.MessageIntent is MessageIntent intent
            ? $" ({intent.ToDbValue()})"
            : evt.MessageType is not null ? $" ({evt.MessageType})" : "";
        var summaryTask = evt.TaskId.HasValue ? $" on #{evt.TaskId}" : "";
        return new PromptResult
        {
            Summary = $"{evt.Sender} sent{summaryType}{summaryTask} on {evt.ProjectId}",
            ContextPrompt = sb.ToString().TrimEnd()
        };
    }

    private static PromptResult BuildFallbackPrompt(DispatchEvent evt, string? customOpener)
    {
        return new PromptResult
        {
            Summary = $"Dispatch on {evt.ProjectId}",
            ContextPrompt = customOpener ?? $"You have pending work on {evt.ProjectId}. Check your tasks and messages."
        };
    }

    /// <summary>
    /// Include the triggering message body when no task-thread messages will cover it.
    /// Used for project-level / taskless messages where AppendRecentMessages would skip.
    /// </summary>
    private static void AppendTriggeringMessage(StringBuilder sb, DispatchEvent evt)
    {
        if (evt.MessageContent is null) return;

        // If task-attached, the recent messages section will include it — skip here to avoid duplication
        if (evt.TaskId.HasValue) return;

        sb.AppendLine($"**Message from {evt.Sender}:**");
        sb.AppendLine("---");
        var content = evt.MessageContent.Length > 1000
            ? evt.MessageContent[..1000] + "\n... (truncated)"
            : evt.MessageContent;
        sb.AppendLine(content);
        sb.AppendLine("---");
        sb.AppendLine();
    }

    /// <summary>
    /// Include task context and recent messages if the event is task-attached.
    /// </summary>
    private async Task AppendTaskAndMessages(StringBuilder sb, DispatchEvent evt)
    {
        if (evt.TaskId.HasValue)
        {
            var task = await _tasks.GetByIdAsync(evt.TaskId.Value);
            if (task is not null)
            {
                sb.AppendLine($"**Task #{task.Id}**: {task.Title} (status: {task.Status.ToDbValue()})");
                sb.AppendLine();
            }
        }

        await AppendRecentMessages(sb, evt);
    }

    private async Task AppendRecentMessages(StringBuilder sb, DispatchEvent evt)
    {
        if (!evt.TaskId.HasValue) return;

        var messages = await _messages.GetMessagesAsync(evt.ProjectId, taskId: evt.TaskId, limit: 5);
        if (messages.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("**Recent messages on this task:**");
        sb.AppendLine("---");
        // Messages come back newest-first; reverse for chronological display
        foreach (var msg in messages.AsEnumerable().Reverse())
        {
            sb.AppendLine($"**{msg.Sender}** ({msg.CreatedAt:yyyy-MM-dd HH:mm}):");
            // Truncate very long messages to keep the prompt focused
            var content = msg.Content.Length > 1000
                ? msg.Content[..1000] + "\n... (truncated)"
                : msg.Content;
            sb.AppendLine(content);
            sb.AppendLine();
        }
        sb.AppendLine("---");
    }

    private static void AppendAddressingDetails(StringBuilder sb, DispatchEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.Recipient) && !string.IsNullOrWhiteSpace(evt.MessageTargetRole))
        {
            sb.AppendLine($"This message explicitly named recipient `{evt.Recipient}` and also carried target role `{evt.MessageTargetRole}`; the explicit recipient took precedence.");
            sb.AppendLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(evt.Recipient))
        {
            sb.AppendLine($"This message explicitly named recipient `{evt.Recipient}`.");
            sb.AppendLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(evt.MessageTargetRole))
        {
            sb.AppendLine($"This message was addressed to role `{evt.MessageTargetRole}` and resolved through the project's routing config.");
            sb.AppendLine();
        }
    }

    private static void AppendImplementerWorkflowGuidance(StringBuilder sb)
    {
        sb.AppendLine("Workflow guardrails:");
        sb.AppendLine("- Default posture: if the current plan still fits reality and the path is straightforward, keep working until the current slice is complete.");
        sb.AppendLine("- Stop and ask for guidance if reality materially conflicts with the plan, the plan is too vague to implement confidently, scope needs to expand in a non-obvious way, repeated failed attempts suggest the assumptions are wrong, or you are inventing a complex workaround mainly to cope with local mess.");
        sb.AppendLine("- Creating or updating Den tasks is cheap; prefer a follow-up task over landing thin interfaces, deceptive scaffolding, or code TODOs that leave the real behavior unwired.");
    }

    private static void AppendReviewerWorkflowGuidance(StringBuilder sb)
    {
        sb.AppendLine("Review for correctness, regressions, scope drift, and workflow hygiene.");
        sb.AppendLine("Call out deceptive completeness such as thin interfaces, unwired scaffolding, or code TODOs standing in for tracked follow-up work.");
        sb.AppendLine("If the implementation drifted into complex local-environment workarounds or other signs that the implementer should have stopped and asked for guidance, say so explicitly.");
    }

    private static string ResolveHandoffLabel(DispatchEvent evt)
    {
        var kind = evt.HandoffKind ?? evt.MessageType;
        return kind is "planning" or "planning_summary" ? "planning" : "handoff";
    }

    /// <summary>
    /// Find the human-readable role name for the target agent, or fall back to a generic label.
    /// </summary>
    private static string ResolveRoleName(RoutingConfig config, string defaultRole)
    {
        return config.Roles.ContainsKey(defaultRole) ? defaultRole : "assigned agent";
    }
}
