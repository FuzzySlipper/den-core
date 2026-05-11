using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class TopicClipQueueRoutes
{
    public static void MapTopicClipQueueRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/topic-clips");

        // Append a clip
        group.MapPost("/", async (
            ITopicClipQueueRepository queueRepo,
            TopicClipAppendRequest req) =>
        {
            var result = await queueRepo.AppendAsync(
                sourceAgent: req.SourceAgent,
                topicTags: req.TopicTags.ToList(),
                rawContent: req.RawContent,
                owningSpace: req.OwningSpace,
                sourceSessionId: req.SourceSessionId,
                sourceConversationId: req.SourceConversationId,
                sourceMessageId: req.SourceMessageId,
                allowInactive: req.AllowInactive ?? false);

            if (!result.Success)
                return Results.BadRequest(new { error = result.Error, validation_results = result.ValidationResults });

            return Results.Created($"/api/topic-clips/{result.ClipId}", result);
        });

        // List clips
        group.MapGet("/", async (
            ITopicClipQueueRepository queueRepo,
            string? status,
            string? owning_space,
            string? claim_key,
            int? limit) =>
        {
            var items = await queueRepo.ListAsync(
                status: status,
                owningSpace: owning_space,
                claimKey: claim_key,
                limit: limit);
            return Results.Ok(items);
        });

        // Claim a batch
        group.MapPost("/claim", async (
            ITopicClipQueueRepository queueRepo,
            TopicClipClaimRequest req) =>
        {
            var result = await queueRepo.ClaimBatchAsync(
                batchSize: req.BatchSize,
                claimTtl: TimeSpan.FromMinutes(req.ClaimTtlMinutes),
                owningSpace: req.OwningSpace);

            if (result is null)
                return Results.Ok(new { claim_key = (string?)null, count = 0, items = Array.Empty<object>() });

            return Results.Ok(result);
        });

        // Complete clips
        group.MapPost("/complete", async (
            ITopicClipQueueRepository queueRepo,
            TopicClipStatusUpdateRequest req) =>
        {
            var result = await queueRepo.CompleteAsync(
                clipIds: req.ClipIds.ToList(),
                decidedBy: req.DecidedBy,
                reason: req.Reason);
            return Results.Ok(result);
        });

        // Discard clips
        group.MapPost("/discard", async (
            ITopicClipQueueRepository queueRepo,
            TopicClipStatusUpdateRequest req) =>
        {
            var result = await queueRepo.DiscardAsync(
                clipIds: req.ClipIds.ToList(),
                decidedBy: req.DecidedBy,
                reason: req.Reason);
            return Results.Ok(result);
        });

        // Escalate clips
        group.MapPost("/escalate", async (
            ITopicClipQueueRepository queueRepo,
            TopicClipStatusUpdateRequest req) =>
        {
            var result = await queueRepo.EscalateAsync(
                clipIds: req.ClipIds.ToList(),
                decidedBy: req.DecidedBy,
                reason: req.Reason);
            return Results.Ok(result);
        });

        // List curation decisions
        group.MapGet("/decisions", async (
            ITopicClipQueueRepository queueRepo,
            int? clip_id,
            int? limit) =>
        {
            var decisions = await queueRepo.ListDecisionsAsync(clipId: clip_id, limit: limit);
            return Results.Ok(decisions);
        });

        // Cleanup raw content
        group.MapPost("/cleanup", async (
            ITopicClipQueueRepository queueRepo,
            TopicClipCleanupRequest req) =>
        {
            var result = await queueRepo.CleanupRawContentAsync(req.Cutoff);
            return Results.Ok(result);
        });
    }
}

public record TopicClipAppendRequest(
    string SourceAgent,
    string[] TopicTags,
    string RawContent,
    string? OwningSpace = null,
    string? SourceSessionId = null,
    string? SourceConversationId = null,
    int? SourceMessageId = null,
    bool? AllowInactive = null);

public record TopicClipClaimRequest(
    int BatchSize,
    int ClaimTtlMinutes,
    string? OwningSpace = null);

public record TopicClipStatusUpdateRequest(
    int[] ClipIds,
    string DecidedBy,
    string? Reason = null);

public record TopicClipCleanupRequest(DateTime Cutoff);
