using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class BlockedTaskEscalationPolicyOptions
{
    /// <summary>
    /// Time window used to suppress duplicate blocked-task escalations with the same
    /// task and blocker signature. Defaults to one hour for backward compatibility.
    /// Set to zero or a negative value to disable deduplication entirely, which is
    /// useful when an environment prefers every re-block transition to produce a fresh
    /// escalation until richer resolution state is modeled.
    /// </summary>
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Structured blocker context required when a task transitions to blocked status.
/// Core owns this model because task status transitions are Core state.
/// </summary>
public sealed class BlockedTaskEscalation
{
    /// <summary>Task ID that was blocked.</summary>
    public required int TaskId { get; set; }

    /// <summary>Project ID the task belongs to.</summary>
    public required string ProjectId { get; set; }

    /// <summary>Short blocker summary.</summary>
    public required string BlockerSummary { get; set; }

    /// <summary>Why the agent cannot proceed.</summary>
    public required string Reason { get; set; }

    /// <summary>Remedies or evidence of what was attempted.</summary>
    public string? AttemptedRemedies { get; set; }

    /// <summary>Suggested next decision or unblock path.</summary>
    public string? SuggestedNextStep { get; set; }

    /// <summary>Whether human input is required (vs planner can replan).</summary>
    public bool RequiresHumanInput { get; set; }

    /// <summary>Agent that marked the task blocked.</summary>
    public required string ChangedBy { get; set; }

    /// <summary>Timestamp of the escalation.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of a blocked-task escalation attempt.
/// </summary>
public sealed class BlockedTaskEscalationResult
{
    /// <summary>Whether the escalation was processed (not a duplicate).</summary>
    public required bool WasNew { get; set; }

    /// <summary>Whether a planner wake was attempted.</summary>
    public bool PlannerWakeAttempted { get; set; }

    /// <summary>Whether a user notification was created.</summary>
    public bool UserNotificationCreated { get; set; }

    /// <summary>Message ID of the planner wake message, if sent.</summary>
    public int? PlannerWakeMessageId { get; set; }

    /// <summary>Message ID of the user notification, if created.</summary>
    public int? UserNotificationMessageId { get; set; }

    /// <summary>Reason if escalation was skipped (e.g. duplicate).</summary>
    public string? SkipReason { get; set; }
}

/// <summary>
/// Describes the validation result for blocker context.
/// </summary>
public sealed class BlockerContextValidation
{
    public required bool IsValid { get; set; }
    public required List<string> Errors { get; set; } = [];

    public static BlockerContextValidation Valid() => new() { IsValid = true, Errors = [] };
    public static BlockerContextValidation Invalid(params string[] errors) => new() { IsValid = false, Errors = [..errors] };
}
