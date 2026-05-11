namespace DenMcp.Core.Models;

public sealed class AgentGuidanceEntry
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string DocumentProjectId { get; set; }
    public required string DocumentSlug { get; set; }
    public AgentGuidanceImportance Importance { get; set; } = AgentGuidanceImportance.Important;
    public List<string>? Audience { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AgentGuidanceEntryWithDocument
{
    public required AgentGuidanceEntry Entry { get; set; }
    public required Document Document { get; set; }
}

public sealed class ResolvedAgentGuidance
{
    public required string ProjectId { get; set; }
    public DateTime ResolvedAt { get; set; }
    public required string Content { get; set; }
    public required List<ResolvedAgentGuidanceSource> Sources { get; set; }
}

public sealed class ResolvedAgentGuidanceSource
{
    public required string ScopeProjectId { get; set; }
    public required string DocumentProjectId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public DocType DocType { get; set; }
    public List<string>? Tags { get; set; }
    public AgentGuidanceImportance Importance { get; set; }
    public List<string>? Audience { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
}
