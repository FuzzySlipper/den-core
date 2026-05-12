using System.Text.Json;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Server.Tools;

/// <summary>
/// Provides concise JSON response strings for MCP mutation tool results.
/// Returns compact summaries with key identity fields for routine operations.
/// </summary>
public static class ConciseResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(object obj) => JsonSerializer.Serialize(obj, JsonOptions);

    // Task operations

    public static string CreatedTask(ProjectTask task)
    {
        var parts = new List<string> { $"created task #{task.Id} ({task.Status.ToDbValue()}" };
        if (task.ParentId is int parentId)
            parts.Add($", parent #{parentId}");
        parts.Add(")");
        return Serialize(new
        {
            summary = string.Concat(parts),
            id = task.Id,
            status = task.Status.ToDbValue(),
            parent_id = task.ParentId
        });
    }

    public static string UpdatedTask(ProjectTask task, Dictionary<string, object?> changes)
    {
        var changeDescriptions = new List<string>();
        if (changes.ContainsKey("status"))
            changeDescriptions.Add($"status={task.Status.ToDbValue()}");
        if (changes.ContainsKey("title"))
            changeDescriptions.Add($"title updated");
        if (changes.ContainsKey("priority"))
            changeDescriptions.Add($"priority={task.Priority}");
        if (changes.ContainsKey("assigned_to"))
            changeDescriptions.Add($"assigned_to={task.AssignedTo ?? "null"}");
        if (changes.ContainsKey("parent_id"))
            changeDescriptions.Add($"parent_id={task.ParentId?.ToString() ?? "null"}");
        if (changes.ContainsKey("tags"))
            changeDescriptions.Add($"tags updated");
        if (changes.ContainsKey("description"))
            changeDescriptions.Add($"description updated");

        var changesText = changeDescriptions.Count > 0
            ? $" ({string.Join(", ", changeDescriptions)})"
            : "";

        return Serialize(new
        {
            summary = $"updated task #{task.Id}{changesText}",
            id = task.Id,
            status = task.Status.ToDbValue(),
            changes = changes.Keys.ToArray()
        });
    }

    // Review round operations

    public static string CreatedReviewRound(ReviewRound round)
    {
        return Serialize(new
        {
            summary = $"created review round #{round.Id} for task #{round.TaskId} (round {round.RoundNumber})",
            id = round.Id,
            task_id = round.TaskId,
            round_number = round.RoundNumber,
            branch = round.Branch
        });
    }

    public static string SetReviewVerdict(ReviewRound round)
    {
        return Serialize(new
        {
            summary = $"set verdict on round #{round.Id}: {round.Verdict?.ToDbValue() ?? "unknown"}",
            id = round.Id,
            task_id = round.TaskId,
            verdict = round.Verdict?.ToDbValue(),
            decided_by = round.VerdictBy
        });
    }

    // Review finding operations

    public static string CreatedReviewFinding(ReviewFinding finding)
    {
        return Serialize(new
        {
            summary = $"created finding {finding.FindingKey} for round #{finding.ReviewRoundId} ({finding.Category.ToDbValue()})",
            id = finding.Id,
            finding_key = finding.FindingKey,
            task_id = finding.TaskId,
            review_round_id = finding.ReviewRoundId,
            category = finding.Category.ToDbValue()
        });
    }

    public static string RespondedToReviewFinding(ReviewFinding finding)
    {
        var statusText = finding.Status != ReviewFindingStatus.Open
            ? $", status={finding.Status.ToDbValue()}"
            : "";
        return Serialize(new
        {
            summary = $"responded to finding {finding.FindingKey}{statusText}",
            id = finding.Id,
            finding_key = finding.FindingKey,
            task_id = finding.TaskId,
            status = finding.Status.ToDbValue(),
            response_by = finding.ResponseBy
        });
    }

    public static string UpdatedReviewFindingStatus(ReviewFinding finding)
    {
        return Serialize(new
        {
            summary = $"updated finding {finding.FindingKey} status={finding.Status.ToDbValue()}",
            id = finding.Id,
            finding_key = finding.FindingKey,
            task_id = finding.TaskId,
            status = finding.Status.ToDbValue(),
            status_updated_by = finding.StatusUpdatedBy,
            follow_up_task_id = finding.FollowUpTaskId
        });
    }

    // Review finding triage operations

    public static string SplitReviewFindingsToFollowUp(SplitFindingsToFollowUpResult result)
    {
        var skipText = result.SkippedFindingIds.Count > 0
            ? $", {result.SkippedFindingIds.Count} skipped"
            : "";
        return Serialize(new
        {
            summary = $"split {result.UpdatedFindings.Count} findings to follow-up task #{result.FollowUpTask.Id}{skipText}",
            follow_up_task_id = result.FollowUpTask.Id,
            split_count = result.UpdatedFindings.Count,
            skipped_count = result.SkippedFindingIds.Count,
            finding_ids = result.UpdatedFindings.Select(f => f.Id).ToArray()
        });
    }

    // Message operations

    public static string SentMessage(Message message)
    {
        var taskText = message.TaskId is int taskId ? $" on task #{taskId}" : "";
        var threadText = message.ThreadId is int threadId ? $" (reply to thread #{threadId})" : "";
        return Serialize(new
        {
            summary = $"sent message #{message.Id}{taskText}{threadText}",
            id = message.Id,
            project_id = message.ProjectId,
            task_id = message.TaskId,
            thread_id = message.ThreadId,
            sender = message.Sender
        });
    }

    // Document operations

    public static string StoredDocument(Document doc)
    {
        return Serialize(new
        {
            summary = $"stored document '{doc.ProjectId}/{doc.Slug}'",
            project_id = doc.ProjectId,
            slug = doc.Slug,
            title = doc.Title,
            doc_type = doc.DocType.ToDbValue(),
            doc_summary = doc.Summary
        });
    }

    // Agent guidance operations

    public static string AddedAgentGuidanceEntry(AgentGuidanceEntry entry)
    {
        return Serialize(new
        {
            summary = $"added guidance entry #{entry.Id} for doc '{entry.DocumentSlug}' in project '{entry.ProjectId}'",
            id = entry.Id,
            project_id = entry.ProjectId,
            document_slug = entry.DocumentSlug,
            importance = entry.Importance.ToDbValue()
        });
    }

    // Blackboard operations

    public static string StoredBlackboardEntry(BlackboardEntry entry)
    {
        return Serialize(new
        {
            summary = $"stored blackboard entry '{entry.Slug}'",
            slug = entry.Slug,
            title = entry.Title
        });
    }

    // Dispatch operations

    public static string ApprovedDispatch(DispatchEntry entry)
    {
        return Serialize(new
        {
            summary = $"approved dispatch #{entry.Id} for agent '{entry.TargetAgent}'",
            id = entry.Id,
            target_agent = entry.TargetAgent,
            status = entry.Status.ToDbValue()
        });
    }

    public static string RejectedDispatch(DispatchEntry entry)
    {
        return Serialize(new
        {
            summary = $"rejected dispatch #{entry.Id}",
            id = entry.Id,
            status = entry.Status.ToDbValue()
        });
    }

    public static string CompletedDispatch(DispatchEntry entry)
    {
        return Serialize(new
        {
            summary = $"completed dispatch #{entry.Id}",
            id = entry.Id,
            status = entry.Status.ToDbValue(),
            completed_by = entry.CompletedBy
        });
    }

    // Project operations

    public static string CreatedProject(Project project)
    {
        return Serialize(new
        {
            summary = $"created project '{project.Id}'",
            id = project.Id,
            name = project.Name
        });
    }

    // Topic clip queue operations

    public static string AppendedTopicClip(TopicClipAppendResult result)
    {
        return Serialize(new
        {
            summary = $"appended clip #{result.ClipId} with topics [{string.Join(", ", result.CanonicalTopicSlugs ?? new List<string>())}]",
            clip_id = result.ClipId,
            canonical_topic_slugs = result.CanonicalTopicSlugs
        });
    }

    public static string UpdatedTopicClipStatus(TopicClipStatusUpdateResult result, string status)
    {
        var skipText = result.SkippedIds is { Count: > 0 }
            ? $", {result.SkippedIds.Count} skipped"
            : "";
        var notFoundText = result.NotFoundIds is { Count: > 0 }
            ? $", {result.NotFoundIds.Count} not found"
            : "";
        return Serialize(new
        {
            summary = $"marked {result.UpdatedCount} clips as {status}{skipText}{notFoundText}",
            updated_count = result.UpdatedCount,
            updated_ids = result.UpdatedIds,
            skipped_ids = result.SkippedIds,
            not_found_ids = result.NotFoundIds,
            status
        });
    }

    // Topic operations

    public static string CreatedTopic(ConsolidationTopic topic)
    {
        return Serialize(new
        {
            summary = $"created topic '{topic.Slug}' ({topic.Status})",
            id = topic.Id,
            slug = topic.Slug,
            display_name = topic.DisplayName,
            status = topic.Status
        });
    }

    public static string UpdatedTopic(ConsolidationTopic topic)
    {
        return Serialize(new
        {
            summary = $"updated topic '{topic.Slug}' ({topic.Status})",
            id = topic.Id,
            slug = topic.Slug,
            display_name = topic.DisplayName,
            status = topic.Status
        });
    }

    // Space operations

    public static string CreatedSpace(Project project)
    {
        return Serialize(new
        {
            summary = $"created space '{project.Id}' ({project.Kind})",
            id = project.Id,
            name = project.Name,
            kind = project.Kind
        });
    }

    // Agent stream operations

    public static string SentAgentStreamMessage(AgentStreamMessageCreateResult result)
    {
        var entry = result.Entry;
        var recipientText = entry.RecipientAgent is not null
            ? $" to '{entry.RecipientAgent}'"
            : entry.RecipientRole is not null
                ? $" to role '{entry.RecipientRole}'"
                : "";

        var wakeStatus = result.WakeResolution is not null
            ? result.WakeResolution.Status.ToApiValue()
            : (string?)null;

        return Serialize(new
        {
            summary = $"sent agent stream message #{entry.Id} ({entry.EventType}){recipientText}",
            id = entry.Id,
            event_type = entry.EventType,
            recipient_agent = entry.RecipientAgent,
            recipient_role = entry.RecipientRole,
            wake_resolution_status = wakeStatus
        });
    }

    // Review workflow operations

    public static string RequestedReview(ReviewPacketResult result)
    {
        return Serialize(new
        {
            summary = $"requested review for task #{result.ReviewRound!.TaskId}, round #{result.ReviewRound.Id}",
            review_round_id = result.ReviewRound.Id,
            task_id = result.ReviewRound.TaskId,
            round_number = result.ReviewRound.RoundNumber,
            message_id = result.Message.Id
        });
    }

    public static string PostedReviewFindings(ReviewPacketResult result)
    {
        return Serialize(new
        {
            summary = $"posted findings for round #{result.ReviewRound!.Id} on task #{result.ReviewRound.TaskId}",
            review_round_id = result.ReviewRound.Id,
            task_id = result.ReviewRound.TaskId,
            message_id = result.Message.Id
        });
    }
}
