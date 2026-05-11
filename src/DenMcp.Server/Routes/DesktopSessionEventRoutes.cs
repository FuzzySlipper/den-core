using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class DesktopSessionEventRoutes
{
    public static void MapDesktopSessionEventRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/desktop");

        group.MapPost("/session-events", async (
            IDesktopSessionEventRepository repo,
            string projectId,
            AppendDesktopSessionEventRequest req) =>
        {
            try
            {
                var evt = BuildEvent(projectId, req);
                var saved = await repo.AppendAsync(evt);
                return Results.Created($"/api/projects/{projectId}/desktop/session-events/{saved.Id}", saved);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.BadRequest(new { error = "Desktop session event references an unknown project, task, or workspace." });
            }
        });

        group.MapGet("/session-events", async (
            IDesktopSessionEventRepository repo,
            string projectId,
            int? taskId,
            string? workspaceId,
            string? sourceInstanceId,
            string? sessionId,
            string? eventTypes,
            int? limit) =>
        {
            var events = await repo.ListAsync(new DesktopSessionEventListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                WorkspaceId = workspaceId,
                SourceInstanceId = sourceInstanceId,
                SessionId = sessionId,
                EventTypes = eventTypes,
                Limit = limit ?? 50
            });
            return Results.Ok(events);
        });
    }

    private static DesktopSessionEvent BuildEvent(string projectId, AppendDesktopSessionEventRequest req) => new()
    {
        ProjectId = projectId.Trim(),
        TaskId = req.TaskId,
        WorkspaceId = TrimToNull(req.WorkspaceId),
        SourceInstanceId = req.SourceInstanceId?.Trim() ?? string.Empty,
        SessionId = req.SessionId?.Trim() ?? string.Empty,
        EventType = req.EventType?.Trim() ?? string.Empty,
        Payload = TrimToNull(req.Payload),
        RequestedBy = TrimToNull(req.RequestedBy),
        Reason = TrimToNull(req.Reason),
        ObservedAt = req.ObservedAt ?? DateTime.UtcNow
    };

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AppendDesktopSessionEventRequest
{
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SourceInstanceId { get; init; }
    public string? SessionId { get; init; }
    public string? EventType { get; init; }
    public string? Payload { get; init; }
    public string? RequestedBy { get; init; }
    public string? Reason { get; init; }
    public DateTime? ObservedAt { get; init; }
}
