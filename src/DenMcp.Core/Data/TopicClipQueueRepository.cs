using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface ITopicClipQueueRepository
{
    Task<TopicClipAppendResult> AppendAsync(
        string sourceAgent,
        List<string> topicTags,
        string rawContent,
        string? owningSpace = null,
        string? sourceSessionId = null,
        string? sourceConversationId = null,
        int? sourceMessageId = null,
        bool allowInactive = false);

    Task<List<TopicClipQueueItem>> ListAsync(
        string? status = null,
        string? owningSpace = null,
        string? claimKey = null,
        int? limit = null);

    Task<TopicClipBatchClaimResult?> ClaimBatchAsync(
        int batchSize,
        TimeSpan claimTtl,
        string? owningSpace = null);

    Task<TopicClipStatusUpdateResult> CompleteAsync(
        List<int> clipIds,
        string decidedBy,
        string? reason = null);

    Task<TopicClipStatusUpdateResult> DiscardAsync(
        List<int> clipIds,
        string decidedBy,
        string? reason = null);

    Task<TopicClipStatusUpdateResult> EscalateAsync(
        List<int> clipIds,
        string decidedBy,
        string? reason = null);

    Task<List<CurationDecision>> ListDecisionsAsync(int? clipId = null, int? limit = null);

    Task<TopicClipCleanupResult> CleanupRawContentAsync(DateTime cutoff);
}

public sealed class TopicClipQueueRepository : ITopicClipQueueRepository
{
    private readonly DbConnectionFactory _db;
    private readonly ITopicRepository _topicRepo;

    public TopicClipQueueRepository(DbConnectionFactory db, ITopicRepository topicRepo)
    {
        _db = db;
        _topicRepo = topicRepo;
    }

    public async Task<TopicClipAppendResult> AppendAsync(
        string sourceAgent,
        List<string> topicTags,
        string rawContent,
        string? owningSpace = null,
        string? sourceSessionId = null,
        string? sourceConversationId = null,
        int? sourceMessageId = null,
        bool allowInactive = false)
    {
        var validationResults = await _topicRepo.ValidateManyAsync(topicTags, allowInactive);
        var invalid = validationResults.Where(r => !r.Valid).ToList();
        if (invalid.Count > 0)
        {
            return new TopicClipAppendResult
            {
                Success = false,
                Error = $"Invalid topic tags: {string.Join(", ", invalid.Select(i => $"'{i.Input}' ({i.Reason})"))}",
                ValidationResults = validationResults
            };
        }

        var canonicalSlugs = validationResults
            .Select(r => r.CanonicalSlug!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO topic_clip_queue_items (
                source_agent, source_session_id, source_conversation_id, source_message_id,
                owning_space, canonical_topic_slugs, raw_content, status
            )
            VALUES (
                @sourceAgent, @sourceSessionId, @sourceConversationId, @sourceMessageId,
                @owningSpace, @canonicalTopicSlugs, @rawContent, 'pending'
            )
            RETURNING id, source_agent, source_session_id, source_conversation_id, source_message_id,
                      owning_space, canonical_topic_slugs, raw_content, status, claim_key, claimed_at,
                      claim_expires_at, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@sourceAgent", sourceAgent);
        cmd.Parameters.AddWithValue("@sourceSessionId", (object?)sourceSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceConversationId", (object?)sourceConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceMessageId", (object?)sourceMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@owningSpace", (object?)owningSpace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@canonicalTopicSlugs", JsonSerializer.Serialize(canonicalSlugs));
        cmd.Parameters.AddWithValue("@rawContent", rawContent);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var item = ReadItem(reader);

        return new TopicClipAppendResult
        {
            Success = true,
            ClipId = item.Id,
            CanonicalTopicSlugs = canonicalSlugs,
            ValidationResults = validationResults
        };
    }

    public async Task<List<TopicClipQueueItem>> ListAsync(
        string? status = null,
        string? owningSpace = null,
        string? claimKey = null,
        int? limit = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (status is not null)
        {
            conditions.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", status);
        }
        if (owningSpace is not null)
        {
            conditions.Add("owning_space = @owningSpace");
            cmd.Parameters.AddWithValue("@owningSpace", owningSpace);
        }
        if (claimKey is not null)
        {
            conditions.Add("claim_key = @claimKey");
            cmd.Parameters.AddWithValue("@claimKey", claimKey);
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var limitClause = limit is not null ? "LIMIT @limit" : "";
        if (limit is not null)
            cmd.Parameters.AddWithValue("@limit", limit.Value);

        cmd.CommandText = $"""
            SELECT id, source_agent, source_session_id, source_conversation_id, source_message_id,
                   owning_space, canonical_topic_slugs, raw_content, status, claim_key, claimed_at,
                   claim_expires_at, created_at, updated_at
            FROM topic_clip_queue_items
            {whereClause}
            ORDER BY created_at DESC
            {limitClause}
            """;

        var results = new List<TopicClipQueueItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadItem(reader));
        return results;
    }

    public async Task<TopicClipBatchClaimResult?> ClaimBatchAsync(
        int batchSize,
        TimeSpan claimTtl,
        string? owningSpace = null)
    {
        var claimKey = $"claim-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(claimTtl);

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Find pending items to claim
        await using var selectCmd = conn.CreateCommand();
        selectCmd.Transaction = (SqliteTransaction)tx;
        var conditions = new List<string> { "status = 'pending'" };
        if (owningSpace is not null)
        {
            conditions.Add("owning_space = @owningSpace");
            selectCmd.Parameters.AddWithValue("@owningSpace", owningSpace);
        }
        var whereClause = "WHERE " + string.Join(" AND ", conditions);
        selectCmd.CommandText = $"""
            SELECT id FROM topic_clip_queue_items
            {whereClause}
            ORDER BY created_at ASC
            LIMIT @batchSize
            """;
        selectCmd.Parameters.AddWithValue("@batchSize", batchSize);

        var ids = new List<int>();
        await using (var reader = await selectCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                ids.Add(reader.GetInt32(0));
        }

        if (ids.Count == 0)
        {
            await tx.RollbackAsync();
            return null;
        }

        // Update claimed items
        await using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = (SqliteTransaction)tx;
        var idParams = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        for (var i = 0; i < ids.Count; i++)
            updateCmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        updateCmd.Parameters.AddWithValue("@claimKey", claimKey);
        updateCmd.Parameters.AddWithValue("@claimedAt", now.ToString("O"));
        updateCmd.Parameters.AddWithValue("@claimExpiresAt", expiresAt.ToString("O"));
        updateCmd.CommandText = $"""
            UPDATE topic_clip_queue_items
            SET status = 'claimed',
                claim_key = @claimKey,
                claimed_at = @claimedAt,
                claim_expires_at = @claimExpiresAt,
                updated_at = datetime('now')
            WHERE id IN ({idParams})
              AND status = 'pending'
            """;
        await updateCmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();

        // Fetch the claimed items
        var items = await ListAsync(status: "claimed", claimKey: claimKey);
        return new TopicClipBatchClaimResult
        {
            ClaimKey = claimKey,
            Items = items,
            Count = items.Count,
            ClaimExpiresAt = expiresAt
        };
    }

    public async Task<TopicClipStatusUpdateResult> CompleteAsync(
        List<int> clipIds,
        string decidedBy,
        string? reason = null)
    {
        return await UpdateStatusAsync(clipIds, "processed", decidedBy, reason);
    }

    public async Task<TopicClipStatusUpdateResult> DiscardAsync(
        List<int> clipIds,
        string decidedBy,
        string? reason = null)
    {
        return await UpdateStatusAsync(clipIds, "discarded", decidedBy, reason);
    }

    public async Task<TopicClipStatusUpdateResult> EscalateAsync(
        List<int> clipIds,
        string decidedBy,
        string? reason = null)
    {
        return await UpdateStatusAsync(clipIds, "escalated", decidedBy, reason);
    }

    private async Task<TopicClipStatusUpdateResult> UpdateStatusAsync(
        List<int> clipIds,
        string newStatus,
        string decidedBy,
        string? reason = null)
    {
        if (clipIds.Count == 0)
        {
            return new TopicClipStatusUpdateResult
            {
                UpdatedIds = new List<int>(),
                UpdatedCount = 0
            };
        }

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var updatedIds = new List<int>();
        var skippedIds = new List<int>();
        var notFoundIds = new List<int>();

        foreach (var id in clipIds.Distinct())
        {
            // Check current status and lock the row
            await using var checkCmd = conn.CreateCommand();
            checkCmd.Transaction = (SqliteTransaction)tx;
            checkCmd.CommandText = "SELECT status FROM topic_clip_queue_items WHERE id = @id";
            checkCmd.Parameters.AddWithValue("@id", id);
            var currentStatus = await checkCmd.ExecuteScalarAsync();

            if (currentStatus is null)
            {
                notFoundIds.Add(id);
                continue;
            }

            var statusStr = (string)currentStatus;
            if (statusStr is "processed" or "discarded" or "escalated")
            {
                skippedIds.Add(id);
                continue;
            }

            await using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = (SqliteTransaction)tx;
            updateCmd.CommandText = """
                UPDATE topic_clip_queue_items
                SET status = @newStatus,
                    updated_at = datetime('now')
                WHERE id = @id
                """;
            updateCmd.Parameters.AddWithValue("@newStatus", newStatus);
            updateCmd.Parameters.AddWithValue("@id", id);
            await updateCmd.ExecuteNonQueryAsync();
            updatedIds.Add(id);

            // Record audit decision
            await using var decisionCmd = conn.CreateCommand();
            decisionCmd.Transaction = (SqliteTransaction)tx;
            decisionCmd.CommandText = """
                INSERT INTO curation_decisions (clip_id, decision, reason, decided_by)
                VALUES (@clipId, @decision, @reason, @decidedBy)
                """;
            decisionCmd.Parameters.AddWithValue("@clipId", id);
            decisionCmd.Parameters.AddWithValue("@decision", newStatus);
            decisionCmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
            decisionCmd.Parameters.AddWithValue("@decidedBy", decidedBy);
            await decisionCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        return new TopicClipStatusUpdateResult
        {
            UpdatedIds = updatedIds,
            NotFoundIds = notFoundIds.Count > 0 ? notFoundIds : null,
            SkippedIds = skippedIds.Count > 0 ? skippedIds : null,
            UpdatedCount = updatedIds.Count
        };
    }

    public async Task<List<CurationDecision>> ListDecisionsAsync(int? clipId = null, int? limit = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (clipId is not null)
        {
            conditions.Add("clip_id = @clipId");
            cmd.Parameters.AddWithValue("@clipId", clipId.Value);
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var limitClause = limit is not null ? "LIMIT @limit" : "";
        if (limit is not null)
            cmd.Parameters.AddWithValue("@limit", limit.Value);

        cmd.CommandText = $"""
            SELECT id, clip_id, decision, reason, decided_by, decided_at
            FROM curation_decisions
            {whereClause}
            ORDER BY decided_at DESC, id DESC
            {limitClause}
            """;

        var results = new List<CurationDecision>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new CurationDecision
            {
                Id = reader.GetInt32(0),
                ClipId = reader.GetInt32(1),
                Decision = reader.GetString(2),
                Reason = reader.IsDBNull(3) ? null : reader.GetString(3),
                DecidedBy = reader.GetString(4),
                DecidedAt = DateTime.Parse(reader.GetString(5))
            });
        }
        return results;
    }

    public async Task<TopicClipCleanupResult> CleanupRawContentAsync(DateTime cutoff)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE topic_clip_queue_items
            SET raw_content = '[REDACTED]'
            WHERE status IN ('processed', 'discarded', 'escalated')
              AND updated_at < @cutoff
              AND raw_content != '[REDACTED]'
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        var count = await cmd.ExecuteNonQueryAsync();

        return new TopicClipCleanupResult
        {
            RedactedCount = count,
            Cutoff = cutoff
        };
    }

    private static TopicClipQueueItem ReadItem(SqliteDataReader reader)
    {
        var slugsJson = reader.GetString(6);
        return new TopicClipQueueItem
        {
            Id = reader.GetInt32(0),
            SourceAgent = reader.GetString(1),
            SourceSessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
            SourceConversationId = reader.IsDBNull(3) ? null : reader.GetString(3),
            SourceMessageId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            OwningSpace = reader.IsDBNull(5) ? null : reader.GetString(5),
            CanonicalTopicSlugs = JsonSerializer.Deserialize<List<string>>(slugsJson) ?? new List<string>(),
            RawContent = reader.GetString(7),
            Status = reader.GetString(8),
            ClaimKey = reader.IsDBNull(9) ? null : reader.GetString(9),
            ClaimedAt = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
            ClaimExpiresAt = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
            CreatedAt = DateTime.Parse(reader.GetString(12)),
            UpdatedAt = DateTime.Parse(reader.GetString(13))
        };
    }
}
