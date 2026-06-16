using System.Text.Json.Serialization;

namespace DenCore.Models;

/// <summary>
/// Global knowledge entry — not project-scoped. Retrievable only through
/// explicit knowledge APIs, never through normal document list/search paths.
/// </summary>
public sealed class KnowledgeEntry
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string? Summary { get; set; }
    public required string BodyMarkdown { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public required string CurationState { get; set; }
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

public sealed record KnowledgeSourceRef(
    string SourceKind,
    string SourceId,
    string? ProjectId = null,
    int? TaskId = null,
    int? MessageId = null,
    string? Url = null,
    string? Note = null);

/// <summary>
/// Lightweight list result — no body_markdown.
/// </summary>
public sealed class KnowledgeEntrySummary
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string? Summary { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public required string CurationState { get; set; }
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

/// <summary>
/// Full-text search result — includes snippet but no body_markdown.
/// </summary>
public sealed class KnowledgeSearchResult
{
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string? Summary { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public required string CurationState { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> Audience { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public List<KnowledgeSourceRef> SourceRefs { get; set; } = [];
    public required string Snippet { get; set; }
    public double Rank { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastReviewedAt { get; set; }
}

public sealed class KnowledgeRevisionSummary
{
    public int Id { get; set; }
    public int EntryId { get; set; }
    public int RevisionNumber { get; set; }
    public required string Title { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public required string CurationState { get; set; }
    public string? ChangeNote { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class KnowledgeEntryLink
{
    public int Id { get; set; }
    public int FromEntryId { get; set; }
    public required string ToEntrySlug { get; set; }
    public required string LinkKind { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ── Query / request DTOs ──

public sealed class KnowledgeListQuery
{
    public string? Kind { get; set; }
    public string? Status { get; set; }
    public string[]? RequiredTags { get; set; }
    public string[]? AnyTags { get; set; }
    public string[]? Audience { get; set; }
    public bool IncludeDeprecated { get; set; }
    public bool IncludeUnreviewed { get; set; }
    public bool IncludeArchived { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }
}

public sealed class KnowledgeSearchQuery
{
    public required string Query { get; set; }
    public string[]? RequiredTags { get; set; }
    public string[]? AnyTags { get; set; }
    public string? Kind { get; set; }
    public string[]? Audience { get; set; }
    public string? Status { get; set; }
    public bool IncludeDeprecated { get; set; }
    public bool IncludeUnreviewed { get; set; }
    public bool IncludeArchived { get; set; }
    public int Limit { get; set; } = 10;
}

public sealed class KnowledgeGuideQuery
{
    public required string Question { get; set; }
    public string[]? RequiredTags { get; set; }
    public string[]? AnyTags { get; set; }
    public string[]? Audience { get; set; }
    public int? ContextBudget { get; set; }
    public bool IncludeFollowUps { get; set; } = true;
    public bool IncludeDeprecated { get; set; }
    public bool IncludeUnreviewed { get; set; }
}

// ── Guide response ──

public sealed class KnowledgeGuideResponse
{
    public required string Answer { get; set; }
    public List<KnowledgeGuideCitation> Citations { get; set; } = [];
    public List<KnowledgeNextRead> WhatToReadNext { get; set; } = [];
    public List<string> Uncertainty { get; set; } = [];
    public int BudgetUsed { get; set; }
}

public sealed class KnowledgeGuideCitation
{
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public required string Excerpt { get; set; }
    public List<KnowledgeSourceRef>? SourceRefs { get; set; }
}

public sealed class KnowledgeNextRead
{
    public required string Slug { get; set; }
    public required string Reason { get; set; }
}

// ── Constants ──

public static class KnowledgeEntryKinds
{
    public const string Concept = "concept";
    public const string Reference = "reference";
    public const string Glossary = "glossary";
    public const string Convention = "convention";
    public const string ServiceMap = "service_map";
    public const string ToolNotes = "tool_notes";
    public const string Gotcha = "gotcha";
    public const string ArchitectureNote = "architecture_note";
    public const string MigrationNote = "migration_note";

    public static readonly string[] All =
        [Concept, Reference, Glossary, Convention, ServiceMap, ToolNotes, Gotcha, ArchitectureNote, MigrationNote];
}

public static class KnowledgeEntryStatuses
{
    public const string Draft = "draft";
    public const string Reviewed = "reviewed";
    public const string NeedsReview = "needs_review";
    public const string Deprecated = "deprecated";
    public const string Archived = "archived";

    public static readonly string[] All = [Draft, Reviewed, NeedsReview, Deprecated, Archived];
}

public static class KnowledgeCurationStates
{
    public const string UnreviewedImport = "unreviewed_import";
    public const string HumanCurated = "human_curated";
    public const string AgentCurated = "agent_curated";
    public const string NeedsRecheck = "needs_recheck";

    public static readonly string[] All = [UnreviewedImport, HumanCurated, AgentCurated, NeedsRecheck];
}

public static class KnowledgeLinkKinds
{
    public const string Related = "related";
    public const string Supersedes = "supersedes";
    public const string SupersededBy = "superseded_by";
    public const string SeeAlso = "see_also";
    public const string DependsOn = "depends_on";

    public static readonly string[] All = [Related, Supersedes, SupersededBy, SeeAlso, DependsOn];
}
