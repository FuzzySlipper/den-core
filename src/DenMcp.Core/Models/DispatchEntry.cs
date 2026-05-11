using System.Text.Json.Serialization;

namespace DenMcp.Core.Models;

public sealed class DispatchEntry
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string TargetAgent { get; set; }
    public DispatchStatus Status { get; set; } = DispatchStatus.Pending;
    public DispatchTriggerType TriggerType { get; set; }
    public int TriggerId { get; set; }
    public int? TaskId { get; set; }
    public string? Summary { get; set; }
    public string? ContextPrompt { get; set; }
    [JsonIgnore]
    public string? ContextJson { get; set; }

    /// <summary>
    /// Stable fingerprint for dedup. Formed from trigger_type + trigger_id + target_agent.
    /// Prevents duplicate dispatches for the same event.
    /// </summary>
    public required string DedupKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string? CompletedBy { get; set; }

    /// <summary>
    /// Build a deterministic dedup key from the dispatch trigger.
    /// </summary>
    public static string BuildDedupKey(DispatchTriggerType triggerType, int triggerId, string targetAgent)
        => $"{triggerType.ToDbValue()}:{triggerId}:{targetAgent}";
}
