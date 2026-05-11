namespace DenMcp.Core.Models;

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
