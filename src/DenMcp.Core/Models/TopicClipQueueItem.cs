namespace DenMcp.Core.Models;

public sealed class TopicClipQueueItem
{
    public int Id { get; set; }
    public required string SourceAgent { get; set; }
    public string? SourceSessionId { get; set; }
    public string? SourceConversationId { get; set; }
    public int? SourceMessageId { get; set; }
    public string? OwningSpace { get; set; }
    public required List<string> CanonicalTopicSlugs { get; set; }
    public required string RawContent { get; set; }
    public string Status { get; set; } = "pending";
    public string? ClaimKey { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? ClaimExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CurationDecision
{
    public int Id { get; set; }
    public int ClipId { get; set; }
    public required string Decision { get; set; }
    public string? Reason { get; set; }
    public required string DecidedBy { get; set; }
    public DateTime DecidedAt { get; set; }
}

public sealed class TopicClipAppendResult
{
    public required bool Success { get; set; }
    public int? ClipId { get; set; }
    public List<string>? CanonicalTopicSlugs { get; set; }
    public string? Error { get; set; }
    public List<TopicValidationResult>? ValidationResults { get; set; }
}

public sealed class TopicClipBatchClaimResult
{
    public required string ClaimKey { get; set; }
    public required List<TopicClipQueueItem> Items { get; set; }
    public int Count { get; set; }
    public DateTime ClaimExpiresAt { get; set; }
}

public sealed class TopicClipStatusUpdateResult
{
    public required List<int> UpdatedIds { get; set; }
    public List<int>? NotFoundIds { get; set; }
    public List<int>? SkippedIds { get; set; }
    public int UpdatedCount { get; set; }
}

public sealed class TopicClipCleanupResult
{
    public int RedactedCount { get; set; }
    public DateTime Cutoff { get; set; }
}
