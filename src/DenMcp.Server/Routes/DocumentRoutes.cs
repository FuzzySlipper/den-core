using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class DocumentRoutes
{
    public static void MapDocumentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/documents");

        group.MapPost("/", async (IDocumentRepository repo, string projectId, StoreDocumentRequest req) =>
        {
            var doc = await repo.UpsertAsync(new Document
            {
                ProjectId = projectId,
                Slug = req.Slug,
                Title = req.Title,
                Content = req.Content,
                DocType = req.DocType is not null ? EnumExtensions.ParseDocType(req.DocType) : DocType.Spec,
                Tags = req.Tags,
                Summary = req.Summary
            });
            return Results.Ok(doc);
        });

        group.MapGet("/{slug}", async (IDocumentRepository repo, string projectId, string slug) =>
        {
            var doc = await repo.GetAsync(projectId, slug);
            return doc is not null
                ? Results.Ok(doc)
                : Results.NotFound(new { error = $"Document '{slug}' not found" });
        });

        group.MapGet("/", async (IDocumentRepository repo, string projectId, string? doc_type, string? tags) =>
        {
            var parsedType = doc_type is not null ? EnumExtensions.ParseDocType(doc_type) : (DocType?)null;
            var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var docs = await repo.ListAsync(projectId, parsedType, tagList);
            return Results.Ok(docs);
        });

        group.MapGet("/search", async (IDocumentRepository repo, string projectId, string query) =>
        {
            var results = await repo.SearchAsync(query, projectId);
            return Results.Ok(results);
        });

        group.MapDelete("/{slug}", async (IDocumentRepository repo, string projectId, string slug) =>
        {
            var deleted = await repo.DeleteAsync(projectId, slug);
            return deleted
                ? Results.Ok(new { message = $"Document '{slug}' deleted." })
                : Results.NotFound(new { error = $"Document '{slug}' not found" });
        });

        // Cross-project document search
        app.MapGet("/api/documents/search", async (IDocumentRepository repo, string query, string? projectId) =>
        {
            var results = await repo.SearchAsync(query, projectId);
            return Results.Ok(results);
        });

        // Cross-project document listing
        app.MapGet("/api/documents", async (IDocumentRepository repo, string? projectId, string? doc_type, string? tags) =>
        {
            var parsedType = doc_type is not null ? EnumExtensions.ParseDocType(doc_type) : (DocType?)null;
            var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var docs = await repo.ListAsync(projectId, parsedType, tagList);
            return Results.Ok(docs);
        });
    }
}

public record StoreDocumentRequest(
    string Slug,
    string Title,
    string Content,
    string? DocType = null,
    List<string>? Tags = null,
    string? Summary = null);
