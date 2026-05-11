using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class SpaceRoutes
{
    public static void MapSpaceRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/spaces");

        group.MapPost("/", async (IProjectRepository repo, SpaceCreateRequest req) =>
        {
            var project = await repo.CreateAsync(new Project
            {
                Id = req.Id,
                Name = req.Name,
                Kind = req.Kind ?? "project",
                Visibility = req.Visibility ?? "normal",
                Owner = req.Owner,
                RootPath = req.RootPath,
                Description = req.Description,
                SettingsJson = req.SettingsJson
            });
            return Results.Created($"/api/spaces/{project.Id}", project);
        });

        group.MapGet("/", async (IProjectRepository repo, string? kind, bool includeHidden = false, bool includeArchived = false) =>
        {
            var spaces = await repo.ListAsync(kind: kind, includeHidden: includeHidden, includeArchived: includeArchived);
            return Results.Ok(spaces);
        });

        group.MapGet("/{id}", async (IProjectRepository repo, string id, string? agent) =>
        {
            try
            {
                var stats = await repo.GetWithStatsAsync(id, agent);
                return Results.Ok(stats);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Space '{id}' not found" });
            }
        });
    }
}

public record SpaceCreateRequest(
    string Id,
    string Name,
    string? Kind = null,
    string? Visibility = null,
    string? Owner = null,
    string? RootPath = null,
    string? Description = null,
    string? SettingsJson = null);
