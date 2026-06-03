namespace DenCore.Models;

public sealed class Document
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public DocType DocType { get; set; } = DocType.Spec;
    public DocumentVisibility Visibility { get; set; } = DocumentVisibility.Normal;
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
    public DocumentVisibility Visibility { get; set; } = DocumentVisibility.Normal;
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
    public DocumentVisibility Visibility { get; set; } = DocumentVisibility.Normal;
    public string? Summary { get; set; }
    public required string Snippet { get; set; }
    public double Rank { get; set; }
}

/// <summary>
/// Result of a preflight check before archiving a document.
/// Lists active references that would be broken or misleading if the document is archived.
/// </summary>
public sealed class DocumentArchivePreflightResult
{
    public required string ProjectId { get; set; }
    public required string Slug { get; set; }
    public required bool CanArchive { get; set; }
    public required List<DocumentReference> ReferencedBy { get; set; } = [];
}

/// <summary>
/// A single reference to a document from another entity (guidance entry, linked doc, etc.).
/// </summary>
public sealed class DocumentReference
{
    public required string RefKind { get; set; }
    public required string Description { get; set; }
    public string? ScopeProjectId { get; set; }
}
