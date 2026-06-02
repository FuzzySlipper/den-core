using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging;

namespace DenCore.Services;

/// <summary>
/// Legacy dispatch detection service. Dispatch creation is retired per
/// den-communication-surfaces-concept-map. All methods are no-ops.
/// </summary>
public interface IDispatchDetectionService
{
    /// <summary>
    /// Legacy hook for message-created events. No-op since dispatch creation is retired.
    /// </summary>
    Task OnMessageCreatedAsync(Message message);

    /// <summary>
    /// Legacy hook for task-status-changed events. No-op since dispatch creation is retired.
    /// </summary>
    Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy);
}

public sealed class DispatchDetectionService : IDispatchDetectionService
{
    private readonly ILogger<DispatchDetectionService> _logger;

    public DispatchDetectionService(
        IRoutingService routing,
        IDispatchRepository dispatches,
        INotificationChannel notifications,
        ILogger<DispatchDetectionService> logger)
    {
        // Dependencies kept in constructor to avoid breaking DI container
        // while dispatch creation is retired. Parameters are intentionally unused.
        _ = routing;
        _ = dispatches;
        _ = notifications;
        _logger = logger;
    }

    public Task OnMessageCreatedAsync(Message message)
    {
        _logger.LogDebug(
            "Dispatch creation is retired; ignoring message {MessageId} for project {ProjectId}",
            message.Id, message.ProjectId);
        return Task.CompletedTask;
    }

    public Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy)
    {
        _logger.LogDebug(
            "Dispatch creation is retired; ignoring task {TaskId} status change {FromStatus} -> {ToStatus} in {ProjectId}",
            task.Id, fromStatus, toStatus, task.ProjectId);
        return Task.CompletedTask;
    }
}
