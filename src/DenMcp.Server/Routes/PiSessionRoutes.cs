using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class PiSessionRoutes
{
    public static void MapPiSessionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/pi-sessions");

        group.MapPost("/", async (
            IPiSessionService service,
            string projectId,
            PiSessionLaunchRequest req,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await service.LaunchAsync(projectId, req, cancellationToken);
                return Results.Created($"/api/projects/{projectId}/pi-sessions/{detail.Session.SessionId}", detail);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/", async (
            IPiSessionService service,
            string projectId,
            int? taskId,
            string? state,
            string? attentionState,
            bool? needsUserInput,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var sessions = await service.ListAsync(new PiSessionListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = state,
                AttentionState = attentionState,
                NeedsUserInput = needsUserInput,
                Limit = limit ?? 50,
            }, cancellationToken);
            return Results.Ok(sessions);
        });

        group.MapGet("/{sessionId}", async (
            IPiSessionService service,
            string projectId,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            var detail = await service.GetAsync(projectId, sessionId, cancellationToken);
            return detail is null
                ? Results.NotFound(new { error = $"Pi session {sessionId} not found" })
                : Results.Ok(detail);
        });

        group.MapPost("/{sessionId}/attach", async (
            IPiSessionService service,
            string projectId,
            string sessionId,
            PiSessionAttachRequest req,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var attach = await service.GetAttachInfoAsync(projectId, sessionId, req, cancellationToken);
                return attach is null
                    ? Results.NotFound(new { error = $"Pi session {sessionId} not found" })
                    : Results.Ok(attach);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/{sessionId}/terminate", async (
            IPiSessionService service,
            string projectId,
            string sessionId,
            PiSessionControlRequest req,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await service.TerminateAsync(projectId, sessionId, req, cancellationToken);
                return detail is null
                    ? Results.NotFound(new { error = $"Pi session {sessionId} not found" })
                    : Results.Ok(detail);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/{sessionId}/cleanup", async (
            IPiSessionService service,
            string projectId,
            string sessionId,
            PiSessionControlRequest req,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await service.CleanupAsync(projectId, sessionId, req, cancellationToken);
                return detail is null
                    ? Results.NotFound(new { error = $"Pi session {sessionId} not found" })
                    : Results.Ok(detail);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });
    }
}
