using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

public interface IDispatchDetectionService
{
    /// <summary>
    /// Check if a newly created message should trigger a dispatch.
    /// </summary>
    Task OnMessageCreatedAsync(Message message);

    /// <summary>
    /// Check if a task status change should trigger a dispatch.
    /// </summary>
    Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy);
}

public sealed class DispatchDetectionService : IDispatchDetectionService
{
    private readonly IRoutingService _routing;
    private readonly IDispatchRepository _dispatches;
    private readonly IPromptGenerationService _prompts;
    private readonly IDispatchContextService _dispatchContexts;
    private readonly INotificationChannel _notifications;
    private readonly ILogger<DispatchDetectionService> _logger;

    public DispatchDetectionService(
        IRoutingService routing,
        IDispatchRepository dispatches,
        IPromptGenerationService prompts,
        IDispatchContextService dispatchContexts,
        INotificationChannel notifications,
        ILogger<DispatchDetectionService> logger)
    {
        _routing = routing;
        _dispatches = dispatches;
        _prompts = prompts;
        _dispatchContexts = dispatchContexts;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task OnMessageCreatedAsync(Message message)
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

        // If task-attached, enrich with task title
        if (message.TaskId.HasValue)
        {
            // We don't load the task here to avoid circular deps — the prompt service does that.
            // TaskTitle will be populated by the caller or left null (prompt generation handles it).
        }

        await TryCreateDispatchAsync(evt, DispatchTriggerType.Message, message.Id);
    }

    public async Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy)
    {
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = task.ProjectId,
            TaskId = task.Id,
            TaskTitle = task.Title,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Sender = changedBy
        };

        var dispatch = await TryCreateDispatchAsync(evt, DispatchTriggerType.TaskStatus, task.Id);
        if (IsTerminalTaskStatus(toStatus))
        {
            var expired = await _dispatches.ExpireOpenForTaskAsync(task.ProjectId, task.Id, dispatch?.Id);
            if (expired > 0)
            {
                _logger.LogInformation(
                    "Expired {ExpiredCount} open dispatch(es) for terminal task #{TaskId} in {ProjectId}",
                    expired,
                    task.Id,
                    task.ProjectId);
            }
        }
    }

    private async Task<DispatchEntry?> TryCreateDispatchAsync(DispatchEvent evt, DispatchTriggerType triggerType, int triggerId)
    {
        var configResult = await _routing.GetRoutingConfigAsync(evt.ProjectId);
        if (!configResult.IsValid)
        {
            _logger.LogWarning("Skipping dispatch detection for {ProjectId}: {Error}",
                evt.ProjectId, configResult.ValidationError);
            return null;
        }

        if (!configResult.Config.Defaults.LegacyDispatchEnabled)
        {
            _logger.LogDebug(
                "Skipping automatic dispatch creation for {ProjectId} event {EventKind}; legacy dispatch routing is disabled.",
                evt.ProjectId,
                evt.EventKind);
            return null;
        }

        var trigger = _routing.MatchTrigger(configResult.Config, evt);
        if (trigger is null)
            return null; // No trigger matched — not a dispatchable event

        var targetAgent = _routing.ResolveAgent(configResult.Config, trigger, evt);
        if (targetAgent is null)
        {
            _logger.LogWarning("Trigger matched but target agent resolved to null for {ProjectId} event {EventKind}",
                evt.ProjectId, evt.EventKind);
            return null;
        }

        // Don't dispatch to the same agent that caused the event
        if (string.Equals(targetAgent, evt.Sender, StringComparison.OrdinalIgnoreCase))
            return null;

        // Generate prompt
        var promptResult = await _prompts.GenerateAsync(evt, trigger, configResult.Config);
        var addressedVia = trigger.DispatchTo switch
        {
            "{recipient}" => "recipient",
            "{target_role}" => "target_role",
            _ => null
        };
        var contextSnapshot = await _dispatchContexts.BuildSnapshotAsync(evt, targetAgent, addressedVia);

        var expiryMinutes = configResult.Config.Defaults.ExpiryMinutes;
        var dedupKey = DispatchEntry.BuildDedupKey(triggerType, triggerId, targetAgent);

        var entry = new DispatchEntry
        {
            ProjectId = evt.ProjectId,
            TargetAgent = targetAgent,
            TriggerType = triggerType,
            TriggerId = triggerId,
            TaskId = evt.TaskId,
            Summary = promptResult.Summary,
            ContextPrompt = promptResult.ContextPrompt,
            ContextJson = JsonSerializer.Serialize(contextSnapshot),
            DedupKey = dedupKey,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes)
        };

        var (dispatch, created) = await _dispatches.CreateIfAbsentAsync(entry);
        if (evt.TaskId is int taskId)
        {
            // Legacy dispatches are a queue/cache over the task thread, so newer task-attached
            // work for the same target should retire older open entries.
            var expired = await _dispatches.ExpireSupersededForTaskTargetAsync(
                evt.ProjectId,
                taskId,
                targetAgent,
                dispatch.Id);
            if (expired > 0)
            {
                _logger.LogInformation(
                    "Expired {ExpiredCount} superseded dispatch(es) for task #{TaskId} targeting {TargetAgent}",
                    expired,
                    taskId,
                    targetAgent);
            }
        }

        if (created)
        {
            _logger.LogInformation("Created dispatch #{DispatchId}: {Summary}",
                dispatch.Id, dispatch.Summary);

            try
            {
                await _notifications.SendDispatchNotificationAsync(
                    dispatch,
                    promptResult.Summary ?? dispatch.Summary ?? "Pending dispatch awaiting approval.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send notification for dispatch {DispatchId}", dispatch.Id);
            }
        }

        return dispatch;
    }

    private static bool IsTerminalTaskStatus(string toStatus) =>
        string.Equals(toStatus, "done", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toStatus, "cancelled", StringComparison.OrdinalIgnoreCase);
}
