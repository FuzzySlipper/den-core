using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class BlackboardRoutes
{
    public static void MapBlackboardRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/blackboard");

        group.MapPost("/", async (IBlackboardRepository repo, StoreBlackboardEntryRequest req) =>
        {
            var entry = await repo.UpsertAsync(new BlackboardEntry
            {
                Slug = req.Slug,
                Title = req.Title,
                Content = req.Content,
                Tags = req.Tags,
                IdleTtlSeconds = req.IdleTtlSeconds
            });
            return Results.Ok(entry);
        });

        group.MapGet("/{slug}", async (IBlackboardRepository repo, string slug) =>
        {
            var entry = await repo.GetAsync(slug);
            return entry is not null
                ? Results.Ok(entry)
                : Results.NotFound(new { error = $"Blackboard entry '{slug}' not found" });
        });

        group.MapGet("/", async (IBlackboardRepository repo, string? tags) =>
        {
            var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var entries = await repo.ListAsync(tagList);
            return Results.Ok(entries);
        });

        group.MapDelete("/{slug}", async (IBlackboardRepository repo, string slug) =>
        {
            var deleted = await repo.DeleteAsync(slug);
            return deleted
                ? Results.Ok(new { message = $"Blackboard entry '{slug}' deleted." })
                : Results.NotFound(new { error = $"Blackboard entry '{slug}' not found" });
        });

        group.MapPost("/cleanup", async (IBlackboardRepository repo) =>
        {
            var deleted = await repo.DeleteExpiredAsync();
            return Results.Ok(new { deleted });
        });
    }
}

public record StoreBlackboardEntryRequest(
    string Slug,
    string Title,
    string Content,
    List<string>? Tags = null,
    int? IdleTtlSeconds = null);
