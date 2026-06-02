using DenCore.Data;
using DenCore.Models;

namespace DenCore.Service.Routes;

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

        // PATCH /api/spaces/{id}/visibility - update visibility (normal/hidden/archived)
        group.MapPatch("/{id}/visibility", async (IProjectRepository repo, string id, SpaceVisibilityRequest req) =>
        {
            var validVisibilities = new[] { "normal", "hidden", "archived" };
            if (!validVisibilities.Contains(req.Visibility))
                return Results.BadRequest(new
                {
                    error = $"invalid visibility '{req.Visibility}': must be one of normal, hidden, archived",
                    valid_values = new[] { "normal", "hidden", "archived" }
                });

            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Space '{id}' not found" });

            try
            {
                var updated = await repo.UpdateVisibilityAsync(id, req.Visibility);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Space '{id}' not found" });
            }
        });

        // POST /api/spaces/{id}/archive - convenience endpoint to archive a space
        group.MapPost("/{id}/archive", async (IProjectRepository repo, string id) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Space '{id}' not found" });

            try
            {
                var updated = await repo.UpdateVisibilityAsync(id, "archived");
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Space '{id}' not found" });
            }
        });

        // DELETE /api/spaces/{id} - guarded delete with dependent record reporting
        group.MapDelete("/{id}", async (IProjectRepository repo, string id, bool? force) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Space '{id}' not found" });

            var forceDelete = force == true;

            // Guard: protect system, personal, and core project spaces
            var protectedKinds = new[] { "system", "personal" };
            var protectedIds = new[] { "den", "den-core", "core" };

            if (!forceDelete && protectedKinds.Contains(existing.Kind))
                return Results.BadRequest(new
                {
                    error = $"Cannot delete {existing.Kind} kind space '{id}' (protected). Use archive or hide instead."
                });

            if (!forceDelete && protectedIds.Contains(id))
                return Results.BadRequest(new
                {
                    error = $"Core project space '{id}' is protected from deletion. Use archive or hide instead."
                });

            // Report dependent records
            var dependentCounts = await repo.GetDependentRecordCountsAsync(id);
            if (dependentCounts.Count > 0 && !forceDelete)
                return Results.BadRequest(new
                {
                    error = $"Space '{id}' has {dependentCounts.Values.Sum()} dependent records. Archive or hide instead, or use ?force=true to delete everything.",
                    dependent_counts = dependentCounts
                });

            await repo.DeleteSpaceAsync(id);
            return Results.Ok(new { deleted = true, id });
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

public record SpaceVisibilityRequest(string Visibility);
