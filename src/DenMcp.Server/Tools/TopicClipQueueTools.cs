using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class TopicClipQueueTools
{
    [McpServerTool(Name = "append_topic_clip"), Description("Append a conversation clip to the topic clipping queue for later curation. Topic tags are validated against the registry.")]
    public static async Task<string> AppendTopicClip(
        ITopicClipQueueRepository queueRepo,
        [Description("Agent identity that produced this clip.")] string source_agent,
        [Description("Array of topic tags to assign. Aliases resolve to canonical slugs; unknown tags are rejected by default.")] string[] topic_tags,
        [Description("Raw clip content (e.g. conversation excerpt).")] string raw_content,
        [Description("Optional owning space/project ID.")] string? owning_space = null,
        [Description("Optional source session ID.")] string? source_session_id = null,
        [Description("Optional source conversation ID.")] string? source_conversation_id = null,
        [Description("Optional source message ID.")] int? source_message_id = null,
        [Description("If true, allow inactive/deprecated topics to pass validation.")] bool allow_inactive = false,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var result = await queueRepo.AppendAsync(
            sourceAgent: source_agent,
            topicTags: topic_tags.ToList(),
            rawContent: raw_content,
            owningSpace: owning_space,
            sourceSessionId: source_session_id,
            sourceConversationId: source_conversation_id,
            sourceMessageId: source_message_id,
            allowInactive: allow_inactive);

        if (!result.Success)
            return $"{{\"error\":\"{result.Error}\",\"validation_results\":{JsonSerializer.Serialize(result.ValidationResults, JsonOpts.Default)}}}";

        return verbose
            ? JsonSerializer.Serialize(result, JsonOpts.Default)
            : ConciseResponse.AppendedTopicClip(result);
    }

    [McpServerTool(Name = "list_topic_clips"), Description("List topic clip queue items.")]
    public static async Task<string> ListTopicClips(
        ITopicClipQueueRepository queueRepo,
        [Description("Filter by status: pending, claimed, processed, discarded, escalated.")] string? status = null,
        [Description("Filter by owning space ID.")] string? owning_space = null,
        [Description("Filter by claim key.")] string? claim_key = null,
        [Description("Maximum items to return.")] int? limit = null)
    {
        var items = await queueRepo.ListAsync(
            status: status,
            owningSpace: owning_space,
            claimKey: claim_key,
            limit: limit);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    [McpServerTool(Name = "claim_topic_clip_batch"), Description("Claim a batch of pending topic clips for curation.")]
    public static async Task<string> ClaimTopicClipBatch(
        ITopicClipQueueRepository queueRepo,
        [Description("Number of items to claim.")] int batch_size,
        [Description("Claim TTL in minutes. Defaults to 60.")] int claim_ttl_minutes = 60,
        [Description("Optional owning space filter.")] string? owning_space = null)
    {
        var result = await queueRepo.ClaimBatchAsync(
            batchSize: batch_size,
            claimTtl: TimeSpan.FromMinutes(claim_ttl_minutes),
            owningSpace: owning_space);

        if (result is null)
            return "{\"claim_key\":null,\"count\":0,\"items\":[]}";

        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }

    [McpServerTool(Name = "complete_topic_clips"), Description("Mark topic clips as processed with an audit decision.")]
    public static async Task<string> CompleteTopicClips(
        ITopicClipQueueRepository queueRepo,
        [Description("Array of clip IDs to complete.")] int[] clip_ids,
        [Description("Agent identity making the decision.")] string decided_by,
        [Description("Optional reason or rationale.")] string? reason = null)
    {
        var result = await queueRepo.CompleteAsync(
            clipIds: clip_ids.ToList(),
            decidedBy: decided_by,
            reason: reason);
        return ConciseResponse.UpdatedTopicClipStatus(result, "processed");
    }

    [McpServerTool(Name = "discard_topic_clips"), Description("Mark topic clips as discarded with an audit decision.")]
    public static async Task<string> DiscardTopicClips(
        ITopicClipQueueRepository queueRepo,
        [Description("Array of clip IDs to discard.")] int[] clip_ids,
        [Description("Agent identity making the decision.")] string decided_by,
        [Description("Optional reason or rationale.")] string? reason = null)
    {
        var result = await queueRepo.DiscardAsync(
            clipIds: clip_ids.ToList(),
            decidedBy: decided_by,
            reason: reason);
        return ConciseResponse.UpdatedTopicClipStatus(result, "discarded");
    }

    [McpServerTool(Name = "escalate_topic_clips"), Description("Mark topic clips as escalated with an audit decision.")]
    public static async Task<string> EscalateTopicClips(
        ITopicClipQueueRepository queueRepo,
        [Description("Array of clip IDs to escalate.")] int[] clip_ids,
        [Description("Agent identity making the decision.")] string decided_by,
        [Description("Optional reason or rationale.")] string? reason = null)
    {
        var result = await queueRepo.EscalateAsync(
            clipIds: clip_ids.ToList(),
            decidedBy: decided_by,
            reason: reason);
        return ConciseResponse.UpdatedTopicClipStatus(result, "escalated");
    }

    [McpServerTool(Name = "list_curation_decisions"), Description("List curation decisions (audit trail).")]
    public static async Task<string> ListCurationDecisions(
        ITopicClipQueueRepository queueRepo,
        [Description("Filter by clip ID.")] int? clip_id = null,
        [Description("Maximum items to return.")] int? limit = null)
    {
        var decisions = await queueRepo.ListDecisionsAsync(clipId: clip_id, limit: limit);
        return JsonSerializer.Serialize(decisions, JsonOpts.Default);
    }

    [McpServerTool(Name = "cleanup_topic_clip_raw_content"), Description("Redact raw content for processed clips older than a cutoff. Preserves metadata and audit trail.")]
    public static async Task<string> CleanupTopicClipRawContent(
        ITopicClipQueueRepository queueRepo,
        [Description("Cutoff datetime (ISO 8601). Items updated before this with terminal status are redacted.")] DateTime cutoff)
    {
        var result = await queueRepo.CleanupRawContentAsync(cutoff);
        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }
}
