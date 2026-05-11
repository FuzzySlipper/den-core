using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class AgentGuidanceRoutes
{
    public static void MapAgentGuidanceRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/agent-guidance");

        group.MapGet("/", async (IAgentGuidanceRepository repo, string projectId) =>
        {
            var guidance = await repo.ResolveAsync(projectId);
            return Results.Ok(guidance);
        });

        group.MapGet("/entries", async (IAgentGuidanceRepository repo, string projectId, bool includeGlobal = false) =>
        {
            var entries = await repo.ListAsync(projectId, includeGlobal);
            return Results.Ok(entries);
        });

        group.MapPost("/entries", async (IAgentGuidanceRepository repo, IDocumentRepository documents, string projectId, StoreAgentGuidanceEntryRequest req) =>
        {
            var documentProjectId = req.DocumentProjectId ?? projectId;
            var doc = await documents.GetAsync(documentProjectId, req.DocumentSlug);
            if (doc is null)
                return Results.NotFound(new { error = $"Document '{documentProjectId}/{req.DocumentSlug}' not found." });

            var entry = await repo.UpsertAsync(new AgentGuidanceEntry
            {
                ProjectId = projectId,
                DocumentProjectId = documentProjectId,
                DocumentSlug = req.DocumentSlug,
                Importance = req.Importance is not null
                    ? EnumExtensions.ParseAgentGuidanceImportance(req.Importance)
                    : AgentGuidanceImportance.Important,
                Audience = req.Audience,
                SortOrder = req.SortOrder ?? 0,
                Notes = req.Notes
            });
            return Results.Ok(entry);
        });

        group.MapDelete("/entries/{entryId:int}", async (IAgentGuidanceRepository repo, string projectId, int entryId) =>
        {
            var deleted = await repo.DeleteAsync(entryId, projectId);
            return deleted
                ? Results.Ok(new { message = $"Agent guidance entry {entryId} deleted." })
                : Results.NotFound(new { error = $"Agent guidance entry {entryId} not found." });
        });
    }
}

public record StoreAgentGuidanceEntryRequest(
    string DocumentSlug,
    string? DocumentProjectId = null,
    string? Importance = null,
    List<string>? Audience = null,
    int? SortOrder = null,
    string? Notes = null);
