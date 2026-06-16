using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;

namespace DenCore.Service.Routes;

public static class KnowledgeRoutes
{
    public static void MapKnowledgeRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/knowledge");

        // ── POST /api/knowledge/entries (upsert) ──
        group.MapPost("/entries", async (IKnowledgeRepository repo, UpsertKnowledgeRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Slug))
                return Results.BadRequest(new { error = "slug is required" });
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "title is required" });
            if (string.IsNullOrWhiteSpace(req.BodyMarkdown))
                return Results.BadRequest(new { error = "body_markdown is required" });

            var entry = new KnowledgeEntry
            {
                Slug = req.Slug,
                Title = req.Title,
                Summary = req.Summary,
                BodyMarkdown = req.BodyMarkdown,
                Kind = req.Kind ?? KnowledgeEntryKinds.Reference,
                Status = req.Status ?? KnowledgeEntryStatuses.Draft,
                CurationState = req.CurationState ?? KnowledgeCurationStates.UnreviewedImport,
                Tags = req.Tags ?? [],
                Audience = req.Audience ?? [],
                Aliases = req.Aliases ?? [],
                SourceRefs = (req.SourceRefs ?? [])
                    .Select(s => new KnowledgeSourceRef(
                        s.SourceKind,
                        s.SourceId,
                        s.ProjectId,
                        s.TaskId,
                        s.MessageId,
                        s.Url,
                        s.Note))
                    .ToList(),
                AccuracyNotes = req.AccuracyNotes,
                ReplacementSlug = req.ReplacementSlug,
                ReviewDueAt = req.ReviewDueAt,
                CreatedBy = req.ChangedBy,
                UpdatedBy = req.ChangedBy
            };

            var result = await repo.UpsertAsync(entry, req.ChangeNote);
            return Results.Ok(result);
        });

        // ── GET /api/knowledge/entries/{slug} (get by slug) ──
        group.MapGet("/entries/{slug}", async (IKnowledgeRepository repo, string slug,
            bool include_archived = false) =>
        {
            var entry = await repo.GetBySlugAsync(slug, include_archived);
            return entry is not null
                ? Results.Ok(entry)
                : Results.NotFound(new { error = $"Knowledge entry '{slug}' not found" });
        });

        // ── GET /api/knowledge/entries (list) ──
        group.MapGet("/entries", async (IKnowledgeRepository repo,
            string? kind = null,
            string? status = null,
            string? required_tags = null,
            string? any_tags = null,
            string? audience = null,
            bool include_deprecated = false,
            bool include_unreviewed = false,
            bool include_archived = false,
            int limit = 50,
            int offset = 0) =>
        {
            var query = new KnowledgeListQuery
            {
                Kind = kind,
                Status = status,
                RequiredTags = required_tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                AnyTags = any_tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Audience = audience?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                IncludeDeprecated = include_deprecated,
                IncludeUnreviewed = include_unreviewed,
                IncludeArchived = include_archived,
                Limit = Math.Clamp(limit, 1, 200),
                Offset = Math.Max(0, offset)
            };

            var results = await repo.ListAsync(query);
            return Results.Ok(new { items = results, count = results.Count });
        });

        // ── POST /api/knowledge/search (deterministic FTS + tag gating) ──
        group.MapPost("/search", async (IKnowledgeRepository repo, SearchKnowledgeRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new { error = "query is required" });

            var query = new KnowledgeSearchQuery
            {
                Query = req.Query,
                RequiredTags = req.RequiredTags,
                AnyTags = req.AnyTags,
                Kind = req.Kind,
                Audience = req.Audience,
                Status = req.Status,
                IncludeDeprecated = req.IncludeDeprecated,
                IncludeUnreviewed = req.IncludeUnreviewed,
                IncludeArchived = req.IncludeArchived,
                Limit = Math.Clamp(req.Limit ?? 10, 1, 200)
            };

            var results = await repo.SearchAsync(query);
            return Results.Ok(new { results, count = results.Count });
        });

        // ── POST /api/knowledge/guide (guided retrieval) ──
        group.MapPost("/guide", async (KnowledgeGuideService guide, GuideKnowledgeRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Question))
                return Results.BadRequest(new { error = "question is required" });

            var query = new KnowledgeGuideQuery
            {
                Question = req.Question,
                RequiredTags = req.RequiredTags,
                AnyTags = req.AnyTags,
                Audience = req.Audience,
                ContextBudget = req.ContextBudget,
                IncludeFollowUps = req.IncludeFollowUps ?? true,
                IncludeDeprecated = req.IncludeDeprecated,
                IncludeUnreviewed = req.IncludeUnreviewed
            };

            var result = await guide.GuideAsync(query);
            return Results.Ok(result);
        });

        // ── GET /api/knowledge/entries/{slug}/revisions ──
        group.MapGet("/entries/{slug}/revisions", async (IKnowledgeRepository repo, string slug) =>
        {
            var revisions = await repo.ListRevisionsAsync(slug);
            return Results.Ok(new { revisions, count = revisions.Count });
        });
    }
}

// ── Request DTOs ──

public record UpsertKnowledgeRequest(
    string Slug,
    string Title,
    string BodyMarkdown,
    string? Summary = null,
    string? Kind = null,
    string? Status = null,
    string? CurationState = null,
    List<string>? Tags = null,
    List<string>? Audience = null,
    List<string>? Aliases = null,
    List<SourceRefDto>? SourceRefs = null,
    string? AccuracyNotes = null,
    string? ReplacementSlug = null,
    DateTime? ReviewDueAt = null,
    string? ChangedBy = null,
    string? ChangeNote = null);

public record SourceRefDto(
    string SourceKind,
    string SourceId,
    string? ProjectId = null,
    int? TaskId = null,
    int? MessageId = null,
    string? Url = null,
    string? Note = null);

public record SearchKnowledgeRequest(
    string Query,
    string[]? RequiredTags = null,
    string[]? AnyTags = null,
    string? Kind = null,
    string[]? Audience = null,
    string? Status = null,
    bool IncludeDeprecated = false,
    bool IncludeUnreviewed = false,
    bool IncludeArchived = false,
    int? Limit = 10);

public record GuideKnowledgeRequest(
    string Question,
    string[]? RequiredTags = null,
    string[]? AnyTags = null,
    string[]? Audience = null,
    int? ContextBudget = null,
    bool? IncludeFollowUps = null,
    bool IncludeDeprecated = false,
    bool IncludeUnreviewed = false);

// ── Response DTO: full entry with body + revisions summary ──

public sealed class KnowledgeEntryFullResponse
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public string BodyMarkdown { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public string CurationState { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> Audience { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public List<KnowledgeSourceRef> SourceRefs { get; set; } = [];
    public string? AccuracyNotes { get; set; }
    public string? ReplacementSlug { get; set; }
    public DateTime? LastReviewedAt { get; set; }
    public DateTime? ReviewDueAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
