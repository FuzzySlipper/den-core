using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class TopicRoutes
{
    public static void MapTopicRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/topics");

        // List active topics (default) or all topics
        group.MapGet("/", async (ITopicRepository repo, string? owning_space, bool include_inactive = false) =>
        {
            var topics = await repo.ListAsync(owningSpace: owning_space, includeInactive: include_inactive);
            return Results.Ok(topics);
        });

        // Get a single topic by id
        group.MapGet("/{id:int}", async (ITopicRepository repo, int id) =>
        {
            var topic = await repo.GetByIdAsync(id);
            return topic is not null ? Results.Ok(topic) : Results.NotFound(new { error = $"Topic {id} not found" });
        });

        // Get a single topic by slug
        group.MapGet("/by-slug/{slug}", async (ITopicRepository repo, string slug) =>
        {
            var topic = await repo.GetBySlugAsync(slug);
            return topic is not null ? Results.Ok(topic) : Results.NotFound(new { error = $"Topic '{slug}' not found" });
        });

        // Create a topic
        group.MapPost("/", async (ITopicRepository repo, TopicCreateRequest req) =>
        {
            try
            {
                var topic = await repo.CreateAsync(new ConsolidationTopic
                {
                    Slug = req.Slug,
                    DisplayName = req.DisplayName,
                    Description = req.Description,
                    Aliases = req.Aliases?.ToList(),
                    Status = req.Status ?? "active",
                    OwningSpace = req.OwningSpace
                });
                return Results.Created($"/api/topics/{topic.Id}", topic);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Update a topic
        group.MapPut("/{id:int}", async (ITopicRepository repo, int id, TopicUpdateRequest req) =>
        {
            try
            {
                var topic = await repo.UpdateAsync(id, new ConsolidationTopic
                {
                    Slug = req.Slug,
                    DisplayName = req.DisplayName,
                    Description = req.Description,
                    Aliases = req.Aliases?.ToList(),
                    Status = req.Status ?? "active",
                    OwningSpace = req.OwningSpace
                });
                return Results.Ok(topic);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Topic {id} not found" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Delete a topic
        group.MapDelete("/{id:int}", async (ITopicRepository repo, int id) =>
        {
            var deleted = await repo.DeleteAsync(id);
            return deleted ? Results.NoContent() : Results.NotFound(new { error = $"Topic {id} not found" });
        });

        // Validate topic tags
        group.MapPost("/validate", async (ITopicRepository repo, TopicValidateRequest req) =>
        {
            var results = await repo.ValidateManyAsync(req.Tags, req.AllowInactive ?? false);
            return Results.Ok(results);
        });
    }
}

public record TopicCreateRequest(
    string Slug,
    string DisplayName,
    string? Description = null,
    string[]? Aliases = null,
    string? Status = null,
    string? OwningSpace = null);

public record TopicUpdateRequest(
    string Slug,
    string DisplayName,
    string? Description = null,
    string[]? Aliases = null,
    string? Status = null,
    string? OwningSpace = null);

public record TopicValidateRequest(
    string[] Tags,
    bool? AllowInactive = null);
