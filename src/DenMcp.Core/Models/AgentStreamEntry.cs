using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class AgentStreamEntry
{
    public int Id { get; set; }
    public AgentStreamKind StreamKind { get; set; }
    public required string EventType { get; set; }
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? ThreadId { get; set; }
    public int? DispatchId { get; set; }
    public required string Sender { get; set; }
    public string? SenderInstanceId { get; set; }
    public string? RecipientAgent { get; set; }
    public string? RecipientRole { get; set; }
    public string? RecipientInstanceId { get; set; }
    public AgentStreamDeliveryMode DeliveryMode { get; set; }
    public string? Body { get; set; }
    public JsonElement? Metadata { get; set; }
    public string? DedupKey { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AgentStreamListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? DispatchId { get; set; }
    public AgentStreamKind? StreamKind { get; set; }
    public string? EventType { get; set; }
    public string? Sender { get; set; }
    public string? SenderInstanceId { get; set; }
    public string? RecipientAgent { get; set; }
    public string? RecipientRole { get; set; }
    public string? RecipientInstanceId { get; set; }
    public string? MetadataRunId { get; set; }
    /// <summary>
    /// When false (default), exclude entries whose metadata.event_visibility is "debug"
    /// or whose event_type starts with "subagent_work_". Set to true to include all entries.
    /// </summary>
    public bool IncludeDebug { get; set; }
    public int Limit { get; set; } = 50;
}
