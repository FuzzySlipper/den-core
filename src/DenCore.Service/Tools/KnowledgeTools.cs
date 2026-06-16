using System.ComponentModel;
using System.Text.Json;
using DenCore.Data;
using DenCore.Mcp;
using DenCore.Models;
using DenCore.Services;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

/// <summary>
/// MCP tools for the global Knowledge Library — separate from document APIs.
/// Knowledge entries have no project scoping and are review-gated by default.
/// </summary>
[McpServerToolType]
public sealed class KnowledgeTools
{
    // ── Read tools (wide profile access) ──

    [McpToolProfile("planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("knowledge")]
    [McpServerTool(Name = "den_knowledge_search"), Description(
        "Search the global knowledge library by FTS query with tag gating. " +
        "Returns only reviewed entries by default; use include_unreviewed/include_deprecated to override. " +
        "Results include snippets but not full body_markdown — use den_knowledge_get for full content. " +
        "This is the green path when an agent needs to look up reference material, conventions, gotchas, or architecture notes. " +
        "You do NOT need to call this before den_knowledge_guide — that tool handles its own search.")]
    public static async Task<string> Search(
        IKnowledgeRepository repo,
        [Description("FTS search query (natural language OK).")] string query,
        [Description("Required tags (AND).")] string[]? required_tags = null,
        [Description("Optional tags (OR).")] string[]? any_tags = null,
        [Description("Limit results.")] int limit = 10,
        [Description("Include deprecated entries.")] bool include_deprecated = false,
        [Description("Include unreviewed (draft/needs_review) entries.")] bool include_unreviewed = false)
    {
        var searchQuery = new KnowledgeSearchQuery
        {
            Query = query,
            RequiredTags = required_tags,
            AnyTags = any_tags,
            Limit = Math.Clamp(limit, 1, 200),
            IncludeDeprecated = include_deprecated,
            IncludeUnreviewed = include_unreviewed
        };

        var results = await repo.SearchAsync(searchQuery);
        return JsonSerializer.Serialize(new { results, count = results.Count }, JsonOpts.Default);
    }

    [McpToolProfile("planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("knowledge")]
    [McpServerTool(Name = "den_knowledge_guide"), Description(
        "Guided retrieval: given a natural-language question or confusion, returns a compact answer " +
        "card with cited excerpts from the knowledge library. No LLM calls — every claim is backed by " +
        "a citation excerpt. Reports gaps/uncertainty when the library lacks reliable coverage. " +
        "Use this when you want an answer synthesized across multiple knowledge entries, rather than raw search results.")]
    public static async Task<string> Guide(
        KnowledgeGuideService guideService,
        [Description("Natural-language question or confusion statement.")] string question,
        [Description("Required tags (AND).")] string[]? required_tags = null,
        [Description("Optional tags (OR).")] string[]? any_tags = null,
        [Description("Max characters for the answer (default 1600).")] int? context_budget = null,
        [Description("Include follow-up reading suggestions.")] bool include_follow_ups = true,
        [Description("Include deprecated entries.")] bool include_deprecated = false,
        [Description("Include unreviewed entries.")] bool include_unreviewed = false)
    {
        var query = new KnowledgeGuideQuery
        {
            Question = question,
            RequiredTags = required_tags,
            AnyTags = any_tags,
            ContextBudget = context_budget,
            IncludeFollowUps = include_follow_ups,
            IncludeDeprecated = include_deprecated,
            IncludeUnreviewed = include_unreviewed
        };

        var result = await guideService.GuideAsync(query);
        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }

    [McpToolProfile("planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("knowledge")]
    [McpServerTool(Name = "den_knowledge_get"), Description(
        "Get a full knowledge entry by slug, including body_markdown. " +
        "Use this after den_knowledge_search or den_knowledge_guide to read full content. " +
        "Returns 404-style error if slug not found.")]
    public static async Task<string> Get(
        IKnowledgeRepository repo,
        [Description("Knowledge entry slug.")] string slug)
    {
        var entry = await repo.GetBySlugAsync(slug);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Knowledge entry '{slug}' not found." }, JsonOpts.Default);
        return JsonSerializer.Serialize(entry, JsonOpts.Default);
    }

    // ── Write tool (narrow profile: planner + runner only) ──

    [McpToolProfile("planner", "runner")]
    [McpToolBundle("knowledge")]
    [McpServerTool(Name = "den_knowledge_store"), Description(
        "Create or update a knowledge entry. Only planner and runner profiles can upsert knowledge. " +
        "Global scope — no project_id required. Use curation_state='agent_curated' with status='reviewed' " +
        "for verified content, or 'unreviewed_import' with status='draft' for imported candidates awaiting review. " +
        "Tags are managed as a separate table — pass the full tag set on each upsert (replace semantics). " +
        "Revisions are automatically created on updates.")]
    public static async Task<string> Store(
        IKnowledgeRepository repo,
        [Description("Unique slug, e.g. 'den-service-topology'.")] string slug,
        [Description("Entry title.")] string title,
        [Description("Full body in markdown.")] string body_markdown,
        [Description("Kind: concept, reference, glossary, convention, service_map, tool_notes, gotcha, architecture_note, migration_note.")] string kind = "reference",
        [Description("Status: draft, reviewed, needs_review, deprecated, archived.")] string status = "draft",
        [Description("Curation state: unreviewed_import, human_curated, agent_curated, needs_recheck.")] string curation_state = "unreviewed_import",
        [Description("Optional short summary for listing.")] string? summary = null,
        [Description("JSON array of tags.")] string[]? tags = null,
        [Description("JSON array of audience labels.")] string[]? audience = null,
        [Description("Who created/updated this entry.")] string? changed_by = null,
        [Description("Optional change note for revision history.")] string? change_note = null)
    {
        var entry = new KnowledgeEntry
        {
            Slug = slug,
            Title = title,
            Summary = summary,
            BodyMarkdown = body_markdown,
            Kind = kind,
            Status = status,
            CurationState = curation_state,
            Tags = tags?.ToList() ?? [],
            Audience = audience?.ToList() ?? [],
            Aliases = [],
            SourceRefs = [],
            CreatedBy = changed_by,
            UpdatedBy = changed_by
        };

        var result = await repo.UpsertAsync(entry, change_note);
        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }
}
