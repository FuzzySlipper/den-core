namespace DenMcp.Core.Models;

public enum LibrarianConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public sealed class LibrarianResponse
{
    public required List<RelevantItem> RelevantItems { get; set; }
    public required List<string> Recommendations { get; set; }
    public LibrarianConfidence Confidence { get; set; }

    public static LibrarianResponse Empty => new()
    {
        RelevantItems = [],
        Recommendations = [],
        Confidence = LibrarianConfidence.Low
    };
}

public sealed class RelevantItem
{
    /// <summary>task, document, or message</summary>
    public required string Type { get; set; }

    /// <summary>Stable source identity as it appears in the gathered context (e.g., "#47", "den-mcp/fts-design", "msg#123")</summary>
    public required string SourceId { get; set; }

    /// <summary>Project ID — distinguishes project-local documents from _global</summary>
    public string? ProjectId { get; set; }

    /// <summary>What this item contains</summary>
    public required string Summary { get; set; }

    /// <summary>Why it matters for the agent's query</summary>
    public required string WhyRelevant { get; set; }

    /// <summary>The specific passage the LLM relied on</summary>
    public string? Snippet { get; set; }
}
