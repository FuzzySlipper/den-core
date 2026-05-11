namespace DenMcp.Core.Models;

public sealed class BlackboardEntry
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public List<string>? Tags { get; set; }
    public int? IdleTtlSeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
}

public sealed class BlackboardEntrySummary
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public List<string>? Tags { get; set; }
    public int? IdleTtlSeconds { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
}
