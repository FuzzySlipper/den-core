using System.Text.Json;
using DenCore.Data;
using DenCore.Models;

namespace DenCore.Service.Routes;

public static class DiscussionRoutes
{
    public static void MapDiscussionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/discussion-threads");

        // GET /api/discussion-threads/{threadId}
        group.MapGet("/{threadId:int}", async (IDiscussionRepository repo, int threadId) =>
        {
            var thread = await repo.GetThreadByIdAsync(threadId);
            if (thread is null)
                return Results.NotFound(new { error = $"Discussion thread {threadId} not found" });

            var comments = await repo.ListCommentsAsync(threadId);
            return Results.Ok(new DiscussionDetailResponse(thread, comments));
        });

        // GET /api/discussion-threads?targetType=...&targetProjectId=...&targetSlug=...&status=...
        group.MapGet("/", async (IDiscussionRepository repo,
            string? targetType, string? targetProjectId, string? targetSlug, string? status) =>
        {
            if (targetType != "document" || string.IsNullOrWhiteSpace(targetProjectId) || string.IsNullOrWhiteSpace(targetSlug))
                return Results.BadRequest(new { error = "targetType=document, targetProjectId, and targetSlug are required" });

            var threads = await repo.ListDocumentThreadsAsync(targetProjectId, targetSlug, status);
            var responses = threads.Select(t => new DiscussionThreadResponse(t)).ToList();
            return Results.Ok(responses);
        });

        // POST /api/discussion-threads
        group.MapPost("/", async (IDiscussionRepository repo, CreateThreadRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.TargetType) || req.TargetType != "document")
                return Results.BadRequest(new { error = "target_type must be 'document'" });
            if (string.IsNullOrWhiteSpace(req.TargetProjectId))
                return Results.BadRequest(new { error = "target_project_id is required" });
            if (string.IsNullOrWhiteSpace(req.TargetSlug))
                return Results.BadRequest(new { error = "target_slug is required" });
            if (string.IsNullOrWhiteSpace(req.ThreadKey))
                return Results.BadRequest(new { error = "thread_key is required" });
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "title is required" });
            if (string.IsNullOrWhiteSpace(req.CreatedBy))
                return Results.BadRequest(new { error = "created_by is required" });

            try
            {
                var existing = (await repo.ListDocumentThreadsAsync(req.TargetProjectId, req.TargetSlug))
                    .FirstOrDefault(t => t.ThreadKey == req.ThreadKey);
                if (existing is not null)
                    return Results.Ok(new DiscussionThreadResponse(existing));

                var thread = await repo.CreateDocumentThreadAsync(
                    req.TargetProjectId, req.TargetSlug, req.ThreadKey,
                    req.Title, req.CreatedBy, req.Summary, req.SerializedMetadata());

                return Results.Created($"/api/discussion-threads/{thread.Id}", new DiscussionThreadResponse(thread));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // POST /api/discussion-threads/{threadId}/comments
        group.MapPost("/{threadId:int}/comments", async (IDiscussionRepository repo, int threadId, CreateCommentRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.BodyMarkdown))
                return Results.BadRequest(new { error = "body_markdown is required" });
            if (string.IsNullOrWhiteSpace(req.AuthorIdentity))
                return Results.BadRequest(new { error = "author_identity is required" });

            try
            {
                var comment = req.ParentCommentId is int parentCommentId
                    ? await repo.AddReplyAsync(
                        threadId,
                        parentCommentId,
                        req.BodyMarkdown,
                        req.AuthorIdentity,
                        req.CommentKind,
                        req.SerializedMentions(),
                        req.SerializedSourceRefs(),
                        req.SerializedMetadata())
                    : await repo.AddCommentAsync(
                        threadId,
                        req.BodyMarkdown,
                        req.AuthorIdentity,
                        req.CommentKind,
                        req.SerializedMentions(),
                        req.SerializedSourceRefs(),
                        req.SerializedMetadata());

                return Results.Created($"/api/discussion-threads/{threadId}/comments/{comment.Id}", new DiscussionCommentResponse(comment));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // PATCH /api/discussion-threads/{threadId}
        group.MapPatch("/{threadId:int}", async (IDiscussionRepository repo, int threadId, UpdateThreadRequest req) =>
        {
            var thread = await repo.GetThreadByIdAsync(threadId);
            if (thread is null)
                return Results.NotFound(new { error = $"Discussion thread {threadId} not found" });

            // Apply partial updates
            if (req.Status is not null)
                thread.Status = req.Status;
            if (req.Title is not null)
                thread.Title = req.Title;
            if (req.Summary is not null)
                thread.Summary = req.Summary;
            if (req.ResolutionSummary is not null)
                thread.ResolutionSummary = req.ResolutionSummary;

            try
            {
                var updated = await repo.UpdateThreadAsync(thread);
                return Results.Ok(new DiscussionThreadResponse(updated));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

// ── DTOs ─────────────────────────────────────────

/// <summary>
/// Safe response shape for a discussion thread, excluding internal-only fields.
/// </summary>
public sealed class DiscussionThreadResponse
{
    public int Id { get; set; }
    public string TargetType { get; set; } = "";
    public string TargetProjectId { get; set; } = "";
    public string? TargetSlug { get; set; }
    public string ThreadKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string? Summary { get; set; }
    public string? ResolutionSummary { get; set; }
    public string? LastCommentAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public DiscussionThreadResponse() { }

    public DiscussionThreadResponse(DiscussionThread t)
    {
        Id = t.Id;
        TargetType = t.TargetType;
        TargetProjectId = t.TargetProjectId;
        TargetSlug = t.TargetSlug;
        ThreadKey = t.ThreadKey;
        Title = t.Title;
        Status = t.Status;
        CreatedBy = t.CreatedBy;
        Summary = t.Summary;
        ResolutionSummary = t.ResolutionSummary;
        LastCommentAt = t.LastCommentAt;
        CreatedAt = t.CreatedAt;
        UpdatedAt = t.UpdatedAt;
    }
}

/// <summary>
/// Thread detail including its comments.
/// </summary>
public sealed class DiscussionDetailResponse
{
    public DiscussionThreadResponse? Thread { get; set; }
    public List<DiscussionCommentResponse>? Comments { get; set; }

    public DiscussionDetailResponse() { }

    public DiscussionDetailResponse(DiscussionThread t, List<DiscussionCommentSummary> comments)
    {
        Thread = new DiscussionThreadResponse(t);
        Comments = comments.Select(c => new DiscussionCommentResponse(c)).ToList();
    }
}

/// <summary>
/// Safe response shape for a discussion comment.
/// </summary>
public sealed class DiscussionCommentResponse
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public int? ParentCommentId { get; set; }
    public string AuthorIdentity { get; set; } = "";
    public string BodyMarkdown { get; set; } = "";
    public string CommentKind { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? EditedAt { get; set; }

    public DiscussionCommentResponse() { }

    public DiscussionCommentResponse(DiscussionComment c)
    {
        Id = c.Id;
        ThreadId = c.ThreadId;
        ParentCommentId = c.ParentCommentId;
        AuthorIdentity = c.AuthorIdentity;
        BodyMarkdown = c.BodyMarkdown;
        CommentKind = c.CommentKind;
        Status = c.Status;
        CreatedAt = c.CreatedAt;
        EditedAt = c.EditedAt;
    }

    public DiscussionCommentResponse(DiscussionCommentSummary c)
    {
        Id = c.Id;
        ThreadId = c.ThreadId;
        ParentCommentId = c.ParentCommentId;
        AuthorIdentity = c.AuthorIdentity;
        BodyMarkdown = c.BodyMarkdown;
        CommentKind = c.CommentKind;
        Status = c.Status;
        CreatedAt = c.CreatedAt;
        EditedAt = c.EditedAt;
    }
}

// ── Request DTOs ──────────────────────────────────

public sealed class CreateThreadRequest
{
    public string TargetType { get; set; } = "";
    public string TargetProjectId { get; set; } = "";
    public string TargetSlug { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string? Summary { get; set; }
    public JsonElement? Metadata { get; set; }

    public string? SerializedMetadata() => Metadata is null ? null : JsonSerializer.Serialize(Metadata.Value);
}

public sealed class CreateCommentRequest
{
    public string BodyMarkdown { get; set; } = "";
    public string AuthorIdentity { get; set; } = "";
    public int? ParentCommentId { get; set; }
    public string? CommentKind { get; set; }
    public JsonElement? Mentions { get; set; }
    public JsonElement? SourceRefs { get; set; }
    public JsonElement? Metadata { get; set; }

    public string? SerializedMentions() => Mentions is null ? null : JsonSerializer.Serialize(Mentions.Value);
    public string? SerializedSourceRefs() => SourceRefs is null ? null : JsonSerializer.Serialize(SourceRefs.Value);
    public string? SerializedMetadata() => Metadata is null ? null : JsonSerializer.Serialize(Metadata.Value);
}

public sealed class UpdateThreadRequest
{
    public string? Status { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? ResolutionSummary { get; set; }
}
