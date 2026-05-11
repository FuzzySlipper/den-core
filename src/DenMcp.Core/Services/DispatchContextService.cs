using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;
using Thread = DenMcp.Core.Models.Thread;

namespace DenMcp.Core.Services;

public interface IDispatchContextService
{
    Task<DispatchContextSnapshot> BuildSnapshotAsync(DispatchEvent evt, string targetAgent, string? addressedVia = null);
    Task<DispatchContextEnvelope?> GetContextAsync(int dispatchId);
}

public sealed class DispatchContextService : IDispatchContextService
{
    private readonly IDispatchRepository _dispatches;
    private readonly IMessageRepository _messages;
    private readonly ITaskRepository _tasks;
    private readonly IRoutingService _routing;
    private readonly ILogger<DispatchContextService> _logger;

    public DispatchContextService(
        IDispatchRepository dispatches,
        IMessageRepository messages,
        ITaskRepository tasks,
        IRoutingService routing,
        ILogger<DispatchContextService> logger)
    {
        _dispatches = dispatches;
        _messages = messages;
        _tasks = tasks;
        _routing = routing;
        _logger = logger;
    }

    public Task<DispatchContextSnapshot> BuildSnapshotAsync(DispatchEvent evt, string targetAgent, string? addressedVia = null)
        => BuildSnapshotFromEventAsync(evt, targetAgent, addressedVia);

    public async Task<DispatchContextEnvelope?> GetContextAsync(int dispatchId)
    {
        var dispatch = await _dispatches.GetByIdAsync(dispatchId);
        if (dispatch is null)
            return null;

        var context = await LoadOrBuildSnapshotAsync(dispatch);
        return new DispatchContextEnvelope
        {
            Dispatch = dispatch,
            Context = context
        };
    }

    private async Task<DispatchContextSnapshot> LoadOrBuildSnapshotAsync(DispatchEntry dispatch)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ContextJson))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<DispatchContextSnapshot>(dispatch.ContextJson);
                if (stored is not null)
                    return stored;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deserialize stored context for dispatch #{DispatchId}; rebuilding a best-effort snapshot.",
                    dispatch.Id);
            }
        }

        return await BuildFallbackSnapshotAsync(dispatch);
    }

    private async Task<DispatchContextSnapshot> BuildSnapshotFromEventAsync(DispatchEvent evt, string targetAgent, string? addressedVia)
    {
        var triggeringMessage = evt.MessageId.HasValue
            ? await _messages.GetByIdAsync(evt.MessageId.Value)
            : null;
        var triggerThread = triggeringMessage is not null
            ? await TryGetThreadAsync(triggeringMessage.ThreadId ?? triggeringMessage.Id)
            : null;
        var taskDetail = evt.TaskId.HasValue
            ? await TryGetTaskDetailAsync(evt.TaskId.Value)
            : null;
        var contextKind = ResolveContextKind(evt);
        var targetRole = ResolveTargetRole(contextKind);
        var activityHint = ResolveActivityHint(contextKind, targetRole);

        return new DispatchContextSnapshot
        {
            ContextKind = contextKind,
            ProjectId = evt.ProjectId,
            TargetAgent = targetAgent,
            TargetRole = targetRole,
            AddressedVia = addressedVia,
            ActivityHint = activityHint,
            TaskId = evt.TaskId,
            Sender = evt.Sender,
            Recipient = evt.Recipient,
            MessageTargetRole = evt.MessageTargetRole,
            MessageIntent = evt.MessageIntent,
            MessageType = evt.MessageType,
            PacketKind = evt.PacketKind,
            HandoffKind = evt.HandoffKind,
            Branch = evt.Branch,
            FromStatus = evt.FromStatus,
            ToStatus = evt.ToStatus,
            TriggeringMessage = triggeringMessage,
            TriggerThread = triggerThread,
            TaskDetail = taskDetail,
            WorkflowGuardrails = BuildWorkflowGuardrails(contextKind),
            NextActions = BuildNextActions(contextKind, evt.Branch)
        };
    }

    private async Task<DispatchContextSnapshot> BuildFallbackSnapshotAsync(DispatchEntry dispatch)
    {
        if (dispatch.TriggerType == DispatchTriggerType.Message)
        {
            var message = await _messages.GetByIdAsync(dispatch.TriggerId);
            if (message is not null)
            {
                var metadata = MessageRoutingMetadata.From(message);
                var evt = new DispatchEvent
                {
                    EventKind = DispatchEvent.MessageReceived,
                    ProjectId = message.ProjectId,
                    MessageId = message.Id,
                    MessageIntent = message.Intent ?? MessageIntentCompatibility.ResolveWriteIntent(null, message.Metadata),
                    MessageType = metadata.MessageType,
                    PacketKind = metadata.PacketKind,
                    HandoffKind = metadata.HandoffKind,
                    Recipient = metadata.Recipient,
                    MessageTargetRole = metadata.TargetRole,
                    Sender = message.Sender,
                    TaskId = message.TaskId,
                    Branch = metadata.Branch,
                    MessageContent = message.Content
                };
                var addressedVia = await ResolveAddressedViaAsync(dispatch.ProjectId, evt, dispatch.TargetAgent);
                return await BuildSnapshotFromEventAsync(evt, dispatch.TargetAgent, addressedVia);
            }
        }

        var taskId = dispatch.TaskId ?? (dispatch.TriggerType == DispatchTriggerType.TaskStatus ? dispatch.TriggerId : null);
        var taskDetail = taskId.HasValue
            ? await TryGetTaskDetailAsync(taskId.Value)
            : null;
        var contextKind = ResolveFallbackContextKind(dispatch, taskDetail);
        var targetRole = ResolveTargetRole(contextKind);

        return new DispatchContextSnapshot
        {
            ContextKind = contextKind,
            ProjectId = dispatch.ProjectId,
            TargetAgent = dispatch.TargetAgent,
            TargetRole = targetRole,
            ActivityHint = ResolveActivityHint(contextKind, targetRole),
            TaskId = taskId,
            TaskDetail = taskDetail,
            WorkflowGuardrails = BuildWorkflowGuardrails(contextKind),
            NextActions = BuildNextActions(contextKind, branch: null)
        };
    }

    private async Task<string?> ResolveAddressedViaAsync(string projectId, DispatchEvent evt, string targetAgent)
    {
        var configResult = await _routing.GetRoutingConfigAsync(projectId);
        if (configResult.IsValid)
        {
            var trigger = _routing.MatchTrigger(configResult.Config, evt);
            if (trigger is not null)
            {
                var resolvedAgent = _routing.ResolveAgent(configResult.Config, trigger, evt);
                if (string.Equals(resolvedAgent, targetAgent, StringComparison.OrdinalIgnoreCase))
                {
                    return trigger.DispatchTo switch
                    {
                        "{recipient}" => "recipient",
                        "{target_role}" => "target_role",
                        _ => null
                    };
                }
            }
        }

        var hasRecipient = !string.IsNullOrWhiteSpace(evt.Recipient);
        var hasTargetRole = !string.IsNullOrWhiteSpace(evt.MessageTargetRole);

        return (hasRecipient, hasTargetRole) switch
        {
            (true, false) => "recipient",
            (false, true) => "target_role",
            _ => null
        };
    }

    private async Task<Thread?> TryGetThreadAsync(int threadId)
    {
        try
        {
            return await _messages.GetThreadAsync(threadId);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<TaskDetail?> TryGetTaskDetailAsync(int taskId)
    {
        try
        {
            return await _tasks.GetDetailAsync(taskId);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static string ResolveContextKind(DispatchEvent evt) =>
        evt.EventKind switch
        {
            DispatchEvent.TaskStatusChanged when evt.ToStatus is "review" => "review_request",
            DispatchEvent.TaskStatusChanged when evt.FromStatus is "review" => "review_feedback",
            DispatchEvent.TaskStatusChanged => "task_status_transition",
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.ReviewRequest => "review_request",
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.Handoff &&
                (evt.HandoffKind is "planning" or "planning_summary" || evt.MessageType is "planning" or "planning_summary")
                => "planning_handoff",
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.Handoff => "handoff",
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.ReviewFeedback => "review_feedback",
            DispatchEvent.MessageReceived when evt.MessageIntent is MessageIntent.ReviewApproval => "merge_request",
            DispatchEvent.MessageReceived => "message",
            _ => "message"
        };

    private static string ResolveFallbackContextKind(DispatchEntry dispatch, TaskDetail? taskDetail)
    {
        if (dispatch.TriggerType == DispatchTriggerType.TaskStatus &&
            string.Equals(taskDetail?.Task.Status.ToDbValue(), "review", StringComparison.Ordinal))
        {
            return "review_request";
        }

        return dispatch.TriggerType == DispatchTriggerType.TaskStatus
            ? "task_status_transition"
            : "message";
    }

    private static string ResolveTargetRole(string contextKind) => contextKind switch
    {
        "review_request" => "reviewer",
        "review_feedback" => "implementer",
        "merge_request" => "implementer",
        "handoff" => "implementer",
        "planning_handoff" => "implementer",
        _ => "assigned_agent"
    };

    private static string ResolveActivityHint(string contextKind, string? targetRole) =>
        contextKind == "review_request" || string.Equals(targetRole, "reviewer", StringComparison.Ordinal)
            ? "reviewing"
            : "working";

    private static List<string> BuildWorkflowGuardrails(string contextKind)
    {
        var guardrails = new List<string>();

        if (contextKind == "review_request")
        {
            guardrails.Add("Review for correctness, regressions, scope drift, and workflow hygiene.");
            guardrails.Add("Call out deceptive completeness such as thin interfaces, unwired scaffolding, or code TODOs standing in for tracked follow-up work.");
            guardrails.Add("If the implementation drifted into complex local-environment workarounds or other signs that the implementer should have stopped and asked for guidance, say so explicitly.");
            return guardrails;
        }

        guardrails.Add("Default posture: if the current plan still fits reality and the path is straightforward, keep working until the current slice is complete.");
        guardrails.Add("Stop and ask for guidance if reality materially conflicts with the plan, the plan is too vague to implement confidently, scope needs to expand in a non-obvious way, repeated failed attempts suggest the assumptions are wrong, or you are inventing a complex workaround mainly to cope with local mess.");
        guardrails.Add("Creating or updating Den tasks is cheap; prefer a follow-up task over landing thin interfaces, deceptive scaffolding, or code TODOs that leave the real behavior unwired.");
        return guardrails;
    }

    private static List<string> BuildNextActions(string contextKind, string? branch)
    {
        var actions = new List<string>();

        switch (contextKind)
        {
            case "review_request":
                actions.Add(branch is not null
                    ? $"Review the requested changes on branch {branch}."
                    : "Review the requested changes and inspect the task thread for branch/head context if needed.");
                actions.Add("Post review findings back to the task thread.");
                actions.Add("If approved, send approval or merge handoff so the implementer can merge the reviewed head and mark the task done.");
                break;
            case "review_feedback":
                actions.Add("Review the findings and decide what needs to change on the task branch.");
                actions.Add("Update the implementation and request review again with the new head commit and tests run when ready.");
                break;
            case "merge_request":
                actions.Add("If the branch still matches the reviewed head commit in the thread, merge it and mark the task done.");
                actions.Add("If the branch has new commits since that review, request review again with the new head commit and tests run.");
                break;
            case "planning_handoff":
            case "handoff":
                actions.Add("Review the handoff context from Den and proceed with the outlined work.");
                break;
            case "task_status_transition":
                actions.Add("Inspect the task detail and recent messages to determine the next implementation step.");
                break;
            default:
                actions.Add("Read the task or thread context and take the appropriate next action.");
                break;
        }

        return actions;
    }
}
