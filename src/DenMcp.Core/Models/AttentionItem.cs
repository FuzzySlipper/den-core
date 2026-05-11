namespace DenMcp.Core.Models;

public sealed class AttentionItem
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public int? TaskId { get; init; }
    public string? RunId { get; init; }
    public int? ReviewRoundId { get; init; }
    public int? DispatchId { get; init; }
    public int? MessageId { get; init; }
    public required string Kind { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LatestAt { get; init; }
    public required string SuggestedAction { get; init; }
}

public sealed class AttentionListOptions
{
    public string? ProjectId { get; init; }
    public int? TaskId { get; init; }
    public string? Kind { get; init; }
    public string? Severity { get; init; }
    public int Limit { get; init; } = 50;
}
