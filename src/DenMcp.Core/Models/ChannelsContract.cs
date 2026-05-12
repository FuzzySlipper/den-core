using System.Text.Json.Serialization;

namespace DenMcp.Core.Models;

/// <summary>
/// Compact, UI-safe description of a Den source object for notification/channel renderers.
/// </summary>
public sealed class SourceSummary
{
    public required string SourceKind { get; set; }
    public required string SourceId { get; set; }
    public string? SourceProjectId { get; set; }
    public required string Title { get; set; }
    public string? Summary { get; set; }
    public string? Actor { get; set; }
    public required string Severity { get; set; }
    public required string DeepLink { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

/// <summary>
/// Cursor page of durable events that external channel renderers can poll without owning Core state.
/// </summary>
public sealed class EventOutboxPage
{
    public required List<EventOutboxItem> Items { get; set; }
    public required string NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public sealed class EventOutboxItem
{
    public required string Cursor { get; set; }
    [JsonIgnore]
    public long SourceIdAsLong { get; set; }
    public required string EventType { get; set; }
    public required string SourceKind { get; set; }
    public required string SourceId { get; set; }
    public string? SourceProjectId { get; set; }
    public required string Title { get; set; }
    public string? Summary { get; set; }
    public string? Actor { get; set; }
    public required string Severity { get; set; }
    public required string DeepLink { get; set; }
    public required string DedupeKey { get; set; }
    public DateTime OccurredAt { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
}
