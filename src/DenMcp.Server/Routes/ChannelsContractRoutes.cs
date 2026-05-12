using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Server.Routes;

public static class ChannelsContractRoutes
{
    public static void MapChannelsContractRoutes(this WebApplication app)
    {
        app.MapGet("/api/source-summaries/{sourceKind}/{sourceId}", async (
            string sourceKind,
            string sourceId,
            string? projectId,
            ITaskRepository tasks,
            IMessageRepository messages,
            IReviewRoundRepository reviewRounds,
            IReviewFindingRepository reviewFindings,
            IAgentStreamRepository agentStream) =>
        {
            var summary = await ResolveSourceSummaryAsync(
                sourceKind,
                sourceId,
                projectId,
                tasks,
                messages,
                reviewRounds,
                reviewFindings,
                agentStream);

            return summary is not null
                ? Results.Ok(summary)
                : Results.NotFound(new { error = $"Source '{sourceKind}/{sourceId}' not found" });
        });

        app.MapGet("/api/events/outbox", async (DbConnectionFactory db, long? after, string? projectId, int? limit) =>
        {
            if (after is < 0)
                return Results.BadRequest(new { error = "after must be a non-negative cursor/id" });

            var clampedLimit = Math.Clamp(limit ?? 50, 1, 200);
            var startAfter = after ?? 0;
            var items = await ListOutboxItemsAsync(db, startAfter, projectId, clampedLimit);
            var nextCursor = items.Count > 0 ? items[^1].SourceIdAsLong + 1 : startAfter;

            return Results.Ok(new EventOutboxPage
            {
                Items = items,
                NextCursor = FormatCursor(nextCursor),
                HasMore = items.Count == clampedLimit
            });
        });
    }

    private static async Task<SourceSummary?> ResolveSourceSummaryAsync(
        string sourceKind,
        string sourceId,
        string? projectId,
        ITaskRepository tasks,
        IMessageRepository messages,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        IAgentStreamRepository agentStream)
    {
        sourceKind = NormalizeSourceKind(sourceKind);
        if (!int.TryParse(sourceId, out var id) || id <= 0)
            return null;

        return sourceKind switch
        {
            "task" => await SummarizeTaskAsync(tasks, id, projectId),
            "task_message" or "message" => await SummarizeMessageAsync(messages, id, projectId),
            "review_round" => await SummarizeReviewRoundAsync(tasks, reviewRounds, id, projectId),
            "review_finding" => await SummarizeReviewFindingAsync(tasks, reviewFindings, id, projectId),
            "agent_stream_entry" or "agent_stream" => await SummarizeAgentStreamEntryAsync(agentStream, id, projectId),
            _ => null
        };
    }

    private static async Task<SourceSummary?> SummarizeTaskAsync(ITaskRepository tasks, int taskId, string? projectId)
    {
        var task = await tasks.GetByIdAsync(taskId);
        if (task is null || !ProjectMatches(task.ProjectId, projectId))
            return null;

        var status = task.Status.ToDbValue();
        return new SourceSummary
        {
            SourceKind = "task",
            SourceId = task.Id.ToString(),
            SourceProjectId = task.ProjectId,
            Title = $"Task #{task.Id}: {task.Title}",
            Summary = $"{status} task, priority {task.Priority}" +
                      (string.IsNullOrWhiteSpace(task.AssignedTo) ? string.Empty : $", assigned to {task.AssignedTo}"),
            Actor = task.AssignedTo,
            Severity = task.Status is DenMcp.Core.Models.TaskStatus.Blocked ? "warning" : "normal",
            DeepLink = $"den://project/{task.ProjectId}/task/{task.Id}",
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            Metadata = new Dictionary<string, object?>
            {
                ["status"] = status,
                ["priority"] = task.Priority,
                ["assigned_to"] = task.AssignedTo,
                ["parent_id"] = task.ParentId,
                ["tags"] = task.Tags
            }
        };
    }

    private static async Task<SourceSummary?> SummarizeMessageAsync(IMessageRepository messages, int messageId, string? projectId)
    {
        var message = await messages.GetByIdAsync(messageId);
        if (message is null || !ProjectMatches(message.ProjectId, projectId))
            return null;

        return new SourceSummary
        {
            SourceKind = "task_message",
            SourceId = message.Id.ToString(),
            SourceProjectId = message.ProjectId,
            Title = $"Message #{message.Id} from {message.Sender}",
            Summary = FirstLine(message.Content),
            Actor = message.Sender,
            Severity = "normal",
            DeepLink = $"den://project/{message.ProjectId}/message/{message.Id}",
            CreatedAt = message.CreatedAt,
            UpdatedAt = message.CreatedAt,
            Metadata = new Dictionary<string, object?>
            {
                ["task_id"] = message.TaskId,
                ["thread_id"] = message.ThreadId,
                ["intent"] = message.Intent?.ToDbValue(),
                ["metadata"] = message.Metadata
            }
        };
    }

    private static async Task<SourceSummary?> SummarizeReviewRoundAsync(
        ITaskRepository tasks,
        IReviewRoundRepository reviewRounds,
        int reviewRoundId,
        string? projectId)
    {
        var round = await reviewRounds.GetByIdAsync(reviewRoundId);
        if (round is null)
            return null;
        var task = await tasks.GetByIdAsync(round.TaskId);
        if (task is null || !ProjectMatches(task.ProjectId, projectId))
            return null;

        return new SourceSummary
        {
            SourceKind = "review_round",
            SourceId = round.Id.ToString(),
            SourceProjectId = task.ProjectId,
            Title = $"Review round #{round.RoundNumber} for task #{round.TaskId}",
            Summary = $"{round.Branch} @ {round.HeadCommit}" +
                      (round.Verdict is null ? string.Empty : $" — {round.Verdict.Value.ToDbValue()}"),
            Actor = round.RequestedBy,
            Severity = round.Verdict is ReviewVerdict.ChangesRequested or ReviewVerdict.BlockedByDependency ? "warning" : "normal",
            DeepLink = $"den://project/{task.ProjectId}/task/{round.TaskId}/review-round/{round.Id}",
            CreatedAt = round.RequestedAt,
            UpdatedAt = round.VerdictAt ?? round.RequestedAt,
            Metadata = new Dictionary<string, object?>
            {
                ["task_id"] = round.TaskId,
                ["round_number"] = round.RoundNumber,
                ["branch"] = round.Branch,
                ["base_branch"] = round.BaseBranch,
                ["base_commit"] = round.BaseCommit,
                ["head_commit"] = round.HeadCommit,
                ["verdict"] = round.Verdict?.ToDbValue()
            }
        };
    }

    private static async Task<SourceSummary?> SummarizeReviewFindingAsync(
        ITaskRepository tasks,
        IReviewFindingRepository reviewFindings,
        int findingId,
        string? projectId)
    {
        var finding = await reviewFindings.GetByIdAsync(findingId);
        if (finding is null)
            return null;
        var task = await tasks.GetByIdAsync(finding.TaskId);
        if (task is null || !ProjectMatches(task.ProjectId, projectId))
            return null;

        return new SourceSummary
        {
            SourceKind = "review_finding",
            SourceId = finding.Id.ToString(),
            SourceProjectId = task.ProjectId,
            Title = $"Finding {finding.FindingKey}: {finding.Summary}",
            Summary = finding.Status.ToDbValue(),
            Actor = finding.CreatedBy,
            Severity = finding.Status is ReviewFindingStatus.Open or ReviewFindingStatus.NotFixed ? "warning" : "normal",
            DeepLink = $"den://project/{task.ProjectId}/task/{finding.TaskId}/review-finding/{finding.Id}",
            CreatedAt = finding.CreatedAt,
            UpdatedAt = finding.UpdatedAt,
            Metadata = new Dictionary<string, object?>
            {
                ["task_id"] = finding.TaskId,
                ["review_round_id"] = finding.ReviewRoundId,
                ["finding_key"] = finding.FindingKey,
                ["category"] = finding.Category.ToDbValue(),
                ["status"] = finding.Status.ToDbValue()
            }
        };
    }

    private static async Task<SourceSummary?> SummarizeAgentStreamEntryAsync(
        IAgentStreamRepository agentStream,
        int entryId,
        string? projectId)
    {
        var entry = await agentStream.GetByIdAsync(entryId);
        if (entry is null || !ProjectMatches(entry.ProjectId, projectId))
            return null;

        return new SourceSummary
        {
            SourceKind = "agent_stream_entry",
            SourceId = entry.Id.ToString(),
            SourceProjectId = entry.ProjectId,
            Title = $"Agent stream {entry.EventType} from {entry.Sender}",
            Summary = FirstLine(entry.Body),
            Actor = entry.Sender,
            Severity = entry.DeliveryMode is AgentStreamDeliveryMode.Wake ? "attention" : "normal",
            DeepLink = entry.ProjectId is null
                ? $"den://agent-stream/{entry.Id}"
                : $"den://project/{entry.ProjectId}/agent-stream/{entry.Id}",
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.CreatedAt,
            Metadata = new Dictionary<string, object?>
            {
                ["stream_kind"] = entry.StreamKind.ToDbValue(),
                ["event_type"] = entry.EventType,
                ["task_id"] = entry.TaskId,
                ["thread_id"] = entry.ThreadId,
                ["dispatch_id"] = entry.DispatchId,
                ["delivery_mode"] = entry.DeliveryMode.ToDbValue(),
                ["recipient_agent"] = entry.RecipientAgent,
                ["recipient_role"] = entry.RecipientRole,
                ["recipient_instance_id"] = entry.RecipientInstanceId,
                ["dedup_key"] = entry.DedupKey,
                ["metadata"] = entry.Metadata
            }
        };
    }

    private static async Task<List<EventOutboxItem>> ListOutboxItemsAsync(
        DbConnectionFactory db,
        long after,
        string? projectId,
        int limit)
    {
        await using var conn = await db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>
        {
            "id > @after",
            "(COALESCE(json_extract(metadata, '$.event_visibility'), '') <> 'debug' AND event_type NOT LIKE 'subagent_work_%')"
        };
        cmd.Parameters.AddWithValue("@after", after);
        cmd.Parameters.AddWithValue("@limit", limit);

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", projectId);
        }

        cmd.CommandText = $"""
            SELECT id, stream_kind, event_type, project_id, task_id, thread_id, dispatch_id,
                   sender, sender_instance_id, recipient_agent, recipient_role, recipient_instance_id,
                   delivery_mode, body, metadata, dedup_key, created_at
            FROM agent_stream_entries
            WHERE {string.Join(" AND ", where)}
            ORDER BY id ASC
            LIMIT @limit
            """;

        var items = new List<EventOutboxItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt64(0);
            var streamKind = reader.GetString(1);
            var eventType = reader.GetString(2);
            var sourceProjectId = reader.IsDBNull(3) ? null : reader.GetString(3);
            var taskId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
            var threadId = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
            var dispatchId = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
            var sender = reader.GetString(7);
            var deliveryMode = reader.GetString(12);
            var body = reader.IsDBNull(13) ? null : reader.GetString(13);
            var metadataJson = reader.IsDBNull(14) ? null : reader.GetString(14);
            var dedupKey = reader.IsDBNull(15) ? null : reader.GetString(15);
            var createdAt = DateTime.Parse(reader.GetString(16));

            items.Add(new EventOutboxItem
            {
                Cursor = FormatCursor(id),
                SourceIdAsLong = id,
                EventType = $"agent_stream.{eventType}",
                SourceKind = "agent_stream_entry",
                SourceId = id.ToString(),
                SourceProjectId = sourceProjectId,
                Title = $"{eventType} from {sender}",
                Summary = FirstLine(body),
                Actor = sender,
                Severity = string.Equals(deliveryMode, "wake", StringComparison.Ordinal) ? "attention" : "normal",
                DeepLink = sourceProjectId is null
                    ? $"den://agent-stream/{id}"
                    : $"den://project/{sourceProjectId}/agent-stream/{id}",
                DedupeKey = dedupKey ?? $"agent_stream_entry:{id}",
                OccurredAt = createdAt,
                Metadata = new Dictionary<string, object?>
                {
                    ["stream_kind"] = streamKind,
                    ["task_id"] = taskId,
                    ["thread_id"] = threadId,
                    ["dispatch_id"] = dispatchId,
                    ["delivery_mode"] = deliveryMode,
                    ["metadata"] = metadataJson is null ? null : JsonSerializer.Deserialize<JsonElement>(metadataJson)
                }
            });
        }

        return items;
    }

    private static string NormalizeSourceKind(string sourceKind) => sourceKind.Trim().ToLowerInvariant().Replace('-', '_');

    private static bool ProjectMatches(string? actualProjectId, string? requestedProjectId) =>
        string.IsNullOrWhiteSpace(requestedProjectId) ||
        string.Equals(actualProjectId, requestedProjectId, StringComparison.Ordinal);

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var line = value.Replace("\r\n", "\n").Split('\n', 2)[0].Trim();
        return line.Length <= 240 ? line : line[..237] + "...";
    }

    private static string FormatCursor(long id) => id.ToString("D12");
}
