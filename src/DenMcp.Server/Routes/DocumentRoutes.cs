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

        // ── Document discussion convenience routes ──

        // GET /api/projects/{projectId}/documents/{slug}/discussion
        group.MapGet("/{slug}/discussion", async (IDiscussionRepository repo, IDocumentRepository docRepo, string projectId, string slug) =>
        {
            var doc = await docRepo.GetAsync(projectId, slug);
            if (doc is null)
                return Results.NotFound(new { error = $"Document '{slug}' not found" });

            // Fetch default thread — do NOT create one on pure GET
            var threads = await repo.ListDocumentThreadsAsync(projectId, slug);
            var defaultThread = threads.FirstOrDefault(t => t.ThreadKey == "default");
            if (defaultThread is null)
                return Results.Ok(new DiscussionDetailResponse
                {
                    Thread = null,
                    Comments = new List<DiscussionCommentResponse>()
                });

            var comments = await repo.ListCommentsAsync(defaultThread.Id);
            return Results.Ok(new DiscussionDetailResponse(defaultThread, comments));
        });

        // POST /api/projects/{projectId}/documents/{slug}/discussion/comments
        group.MapPost("/{slug}/discussion/comments", async (IDiscussionRepository repo, IDocumentRepository docRepo, string projectId, string slug, CreateCommentRequest req) =>
        {
            var doc = await docRepo.GetAsync(projectId, slug);
            if (doc is null)
                return Results.NotFound(new { error = $"Document '{slug}' not found" });

            if (string.IsNullOrWhiteSpace(req.BodyMarkdown))
                return Results.BadRequest(new { error = "body_markdown is required" });
            if (string.IsNullOrWhiteSpace(req.AuthorIdentity))
                return Results.BadRequest(new { error = "author_identity is required" });

            try
            {
                // Ensure the default thread exists
                var thread = await repo.GetOrCreateDefaultDocumentThreadAsync(projectId, slug, req.AuthorIdentity);
                var comment = req.ParentCommentId is int parentCommentId
                    ? await repo.AddReplyAsync(
                        thread.Id,
                        parentCommentId,
                        req.BodyMarkdown,
                        req.AuthorIdentity,
                        req.CommentKind,
                        req.SerializedMentions(),
                        req.SerializedSourceRefs(),
                        req.SerializedMetadata())
                    : await repo.AddCommentAsync(
                        thread.Id,
                        req.BodyMarkdown,
                        req.AuthorIdentity,
                        req.CommentKind,
                        req.SerializedMentions(),
                        req.SerializedSourceRefs(),
                        req.SerializedMetadata());

                return Results.Created(
                    $"/api/projects/{projectId}/documents/{slug}/discussion/threads/{thread.Id}/comments/{comment.Id}",
                    new DiscussionCommentResponse(comment));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Optional: GET /api/projects/{projectId}/documents/{slug}/discussion/threads
        group.MapGet("/{slug}/discussion/threads", async (IDiscussionRepository repo, IDocumentRepository docRepo, string projectId, string slug, string? status) =>
        {
            var doc = await docRepo.GetAsync(projectId, slug);
            if (doc is null)
                return Results.NotFound(new { error = $"Document '{slug}' not found" });

            var threads = await repo.ListDocumentThreadsAsync(projectId, slug, status);
            var responses = threads.Select(t => new DiscussionThreadResponse(t)).ToList();
            return Results.Ok(responses);
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
