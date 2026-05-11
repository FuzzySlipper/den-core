namespace DenMcp.Core.Models;

public sealed class Document
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public DocType DocType { get; set; } = DocType.Spec;
    public string? Summary { get; set; }
    public List<string>? Tags { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class DocumentSummary
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public DocType DocType { get; set; }
    public string? Summary { get; set; }
    public List<string>? Tags { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class DocumentSearchResult
{
    public required string ProjectId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public DocType DocType { get; set; }
    public string? Summary { get; set; }
    public required string Snippet { get; set; }
    public double Rank { get; set; }
}
