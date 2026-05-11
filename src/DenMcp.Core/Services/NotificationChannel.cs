using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface INotificationChannel
{
    Task SendDispatchNotificationAsync(
        DispatchEntry dispatch,
        string summary,
        CancellationToken cancellationToken = default);

    Task SendAgentStatusAsync(
        string projectId,
        string agent,
        string status,
        int? taskId = null,
        CancellationToken cancellationToken = default);

    Task StartListeningAsync(CancellationToken cancellationToken);
}

public sealed class NoOpNotificationChannel : INotificationChannel
{
    public Task SendDispatchNotificationAsync(
        DispatchEntry dispatch,
        string summary,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendAgentStatusAsync(
        string projectId,
        string agent,
        string status,
        int? taskId = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StartListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
