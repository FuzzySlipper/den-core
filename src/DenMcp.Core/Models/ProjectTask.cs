namespace DenMcp.Core.Models;

public sealed class ProjectTask
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Planned;
    public int Priority { get; set; } = 3;
    public string? AssignedTo { get; set; }
    public int? ParentId { get; set; }
    public List<string>? Tags { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TaskSummary
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Title { get; set; }
    public TaskStatus Status { get; set; }
    public int Priority { get; set; }
    public string? AssignedTo { get; set; }
    public int? ParentId { get; set; }
    public List<string>? Tags { get; set; }
    public int DependencyCount { get; set; }
    public int SubtaskCount { get; set; }
}

public sealed class TaskDetail
{
    public required ProjectTask Task { get; set; }
    public required List<TaskDependencyInfo> Dependencies { get; set; }
    public required List<TaskSummary> Subtasks { get; set; }
    public required List<Message> RecentMessages { get; set; }
    public required List<ReviewRound> ReviewRounds { get; set; }
    public required List<ReviewFinding> OpenReviewFindings { get; set; }
    public required List<ReviewFinding> ResolvedReviewFindings { get; set; }
    public required ReviewWorkflowSummary ReviewWorkflow { get; set; }
}

public sealed class TaskDependencyInfo
{
    public int TaskId { get; set; }
    public required string Title { get; set; }
    public TaskStatus Status { get; set; }
}

/// <summary>
/// Compact workflow summary for orchestrator startup/drain.
/// Omits full message bodies, finding notes, and detailed review-round fields.
/// Provides headers, counts, and pointers so the caller can deep-read as needed.
/// </summary>
public sealed class TaskWorkflowSummary
{
    // Task identity
    public required int Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Title { get; set; }
    public required string Status { get; set; }
    public int Priority { get; set; }
    public string? AssignedTo { get; set; }
    public int? ParentId { get; set; }
    public List<string>? Tags { get; set; }

    // Dependencies & subtasks (compact)
    public required List<TaskDependencyInfo> Dependencies { get; set; }
    public required List<CompactSubtaskEntry> Subtasks { get; set; }

    // Review workflow (compact)
    public required CompactReviewWorkflow ReviewWorkflow { get; set; }

    // Recent message headers (no body)
    public required List<CompactMessageHeader> RecentMessages { get; set; }

    // Unresolved findings (summary only, no notes)
    public required List<CompactFindingEntry> UnresolvedFindings { get; set; }

    // Hint for deep read
    public required string DeepReadHint { get; set; }
}

public sealed class CompactSubtaskEntry
{
    public required int Id { get; set; }
    public required string Title { get; set; }
    public required string Status { get; set; }
    public int Priority { get; set; }
}

public sealed class CompactReviewWorkflow
{
    public int ReviewRoundCount { get; set; }
    public string? CurrentVerdict { get; set; }
    public CompactReviewRoundRef? CurrentRound { get; set; }
    public int UnresolvedFindingCount { get; set; }
    public int ResolvedFindingCount { get; set; }
    public int AddressedFindingCount { get; set; }
    public required List<CompactReviewRoundRef> Timeline { get; set; }
}

public sealed class CompactReviewRoundRef
{
    public required int ReviewRoundId { get; set; }
    public required int ReviewRoundNumber { get; set; }
    public required string Branch { get; set; }
    public string? HeadCommit { get; set; }
    public string? Verdict { get; set; }
    public int TotalFindingCount { get; set; }
    public int OpenFindingCount { get; set; }
    public int ResolvedFindingCount { get; set; }
}

/// <summary>
/// Message header without the body content.
/// Extracts structured metadata.type and first line for context.
/// </summary>
public sealed class CompactMessageHeader
{
    public required int Id { get; set; }
    public required string Sender { get; set; }
    public string? Intent { get; set; }
    public string? MetadataType { get; set; }
    public string? MetadataBranch { get; set; }
    public string? MetadataHeadCommit { get; set; }
    public string? MetadataReviewRoundId { get; set; }
    public string? FirstLine { get; set; }
    public int? ThreadId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Finding summary without detailed notes.
/// </summary>
public sealed class CompactFindingEntry
{
    public required int Id { get; set; }
    public required string FindingKey { get; set; }
    public required string Category { get; set; }
    public required string Summary { get; set; }
    public required string Status { get; set; }
    public int ReviewRoundId { get; set; }
    public int ReviewRoundNumber { get; set; }
}
