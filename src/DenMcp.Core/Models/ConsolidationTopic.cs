namespace DenMcp.Core.Models;

public sealed class ConsolidationTopic
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public List<string>? Aliases { get; set; }
    public string Status { get; set; } = "active";
    public string? OwningSpace { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ConsolidationTopicSummary
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public List<string>? Aliases { get; set; }
    public string Status { get; set; } = "active";
    public string? OwningSpace { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TopicValidationResult
{
    public required bool Valid { get; set; }
    public required string Input { get; set; }
    public string? CanonicalSlug { get; set; }
    public string? Reason { get; set; }
}
