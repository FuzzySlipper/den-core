using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class AttentionRoutes
{
    public static void MapAttentionRoutes(this WebApplication app)
    {
        app.MapGet("/api/attention", async (
            IAttentionService service,
            string? projectId,
            int? taskId,
            string? kind,
            string? severity,
            int? limit) =>
        {
            var items = await service.ListAsync(new AttentionListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                Kind = kind,
                Severity = severity,
                Limit = limit ?? 50
            });

            return Results.Ok(items);
        });

        app.MapGet("/api/projects/{projectId}/attention", async (
            IAttentionService service,
            string projectId,
            int? taskId,
            string? kind,
            string? severity,
            int? limit) =>
        {
            var items = await service.ListAsync(new AttentionListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                Kind = kind,
                Severity = severity,
                Limit = limit ?? 50
            });

            return Results.Ok(items);
        });
    }
}
