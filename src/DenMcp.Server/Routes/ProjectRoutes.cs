using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class ProjectRoutes
{
    public static void MapProjectRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/", async (IProjectRepository repo, Project project) =>
        {
            var created = await repo.CreateAsync(project);
            return Results.Created($"/api/projects/{created.Id}", created);
        });

        group.MapGet("/", async (IProjectRepository repo) =>
        {
            var projects = await repo.ListAsync(kind: "project", includeHidden: false, includeArchived: false);
            return Results.Ok(projects);
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
                return Results.NotFound(new { error = $"Project '{id}' not found" });
            }
        });
    }
}
