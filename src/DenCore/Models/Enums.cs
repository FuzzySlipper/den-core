namespace DenCore.Models;

public enum TaskStatus
{
    Planned,
    InProgress,
    Review,
    Blocked,
    Done,
    Cancelled
}

public enum DocType
{
    Prd,
    Spec,
    Adr,
    Convention,
    Reference,
    Note,
    Memory
}

public enum AgentGuidanceImportance
{
    Required,
    Important
}

public enum MessageIntent
{
    General,
    Note,
    StatusUpdate,
    Question,
    Answer,
    Handoff,
    ReviewRequest,
    ReviewFeedback,
    ReviewApproval,
    TaskReady,
    TaskBlocked,
    Notification
}

public enum AgentStreamKind
{
    Ops,
    Message
}

public enum AgentStreamDeliveryMode
{
    RecordOnly,
    Notify,
    Wake
}

public enum AgentInstanceBindingStatus
{
    Active,
    Inactive,
    Degraded
}

public enum AgentWorkspaceState
{
    Planned,
    Active,
    Review,
    Complete,
    Failed,
    Archived
}

public enum AgentWorkspaceCleanupPolicy
{
    Keep,
    DeleteWorktree,
    Archive
}

public enum AgentSessionStatus
{
    Active,
    Inactive
}

public enum DispatchStatus
{
    Pending,
    Approved,
    Rejected,
    Completed,
    Expired
}

public enum DispatchTriggerType
{
    Message,
    TaskStatus
}

public enum ReviewVerdict
{
    ChangesRequested,
    LooksGood,
    FollowUpNeeded,
    BlockedByDependency
}

public enum ReviewFindingCategory
{
    BlockingBug,
    AcceptanceGap,
    TestWeakness,
    FollowUpCandidate
}

public enum ReviewFindingStatus
{
    Open,
    ClaimedFixed,
    VerifiedFixed,
    NotFixed,
    Superseded,
    SplitToFollowUp
}

/// <summary>
/// Projected availability for a task based on its status and dependency state.
/// This is a computed projection, not a persisted column.
/// </summary>
public enum DocumentVisibility
{
    Normal,
    Hidden,
    Archived
}

public enum TaskAvailability
{
    /// <summary>Planned or InProgress, no unfinished dependencies – ready to be claimed.</summary>
    Available,
    /// <summary>Planned, has at least one unfinished dependency – not runnable but not manually blocked.</summary>
    WaitingOnDependencies,
    /// <summary>Status is InProgress – currently claimed/worked on.</summary>
    InProgress,
    /// <summary>Status is Review – under review.</summary>
    Review,
    /// <summary>Status is Blocked – manual/attention-required blocker, not auto-resolved by dependency completion.</summary>
    Blocked,
    /// <summary>Status is Done.</summary>
    Done,
    /// <summary>Status is Cancelled.</summary>
    Cancelled
}
