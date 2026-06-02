using DenCore.Data;
using DenCore.Models;

namespace DenCore.Service.Routes;

/// <summary>
/// Legacy dispatch archive routes. Dispatch is retired per
/// den-communication-surfaces-concept-map. Only read-only list/get
/// remain for historical inspection.
/// </summary>
public static class DispatchRoutes
{
    public static void MapDispatchRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/dispatch");

        // Archive list — read-only
        group.MapGet("/", async (IDispatchRepository repo,
            string? projectId, string? targetAgent, string? status) =>
        {
            DispatchStatus[]? statuses;
            try
            {
                statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(EnumExtensions.ParseDispatchStatus).ToArray();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var entries = await repo.ListAsync(projectId, targetAgent, statuses);
            return Results.Ok(entries);
        });

        // Archive detail — read-only
        group.MapGet("/{id:int}", async (IDispatchRepository repo, int id) =>
        {
            var entry = await repo.GetByIdAsync(id);
            return entry is not null
                ? Results.Ok(entry)
                : Results.NotFound(new { error = $"Dispatch {id} not found" });
        });
    }
}
