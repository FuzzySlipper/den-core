namespace DenMcp.Core.Models;

public static class EnumExtensions
{
    public static string ToDbValue(this TaskStatus status) => status switch
    {
        TaskStatus.Planned => "planned",
        TaskStatus.InProgress => "in_progress",
        TaskStatus.Review => "review",
        TaskStatus.Blocked => "blocked",
        TaskStatus.Done => "done",
        TaskStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static TaskStatus ParseTaskStatus(string value) => value switch
    {
        "planned" => TaskStatus.Planned,
        "in_progress" => TaskStatus.InProgress,
        "review" => TaskStatus.Review,
        "blocked" => TaskStatus.Blocked,
        "done" => TaskStatus.Done,
        "cancelled" => TaskStatus.Cancelled,
        _ => throw new ArgumentException($"Unknown task status: {value}", nameof(value))
    };

    public static string ToDbValue(this DocType docType) => docType switch
    {
        DocType.Prd => "prd",
        DocType.Spec => "spec",
        DocType.Adr => "adr",
        DocType.Convention => "convention",
        DocType.Reference => "reference",
        DocType.Note => "note",
        DocType.Memory => "memory",
        _ => throw new ArgumentOutOfRangeException(nameof(docType), docType, null)
    };

    public static DocType ParseDocType(string value) => value switch
    {
        "prd" => DocType.Prd,
        "spec" => DocType.Spec,
        "adr" => DocType.Adr,
        "convention" => DocType.Convention,
        "reference" => DocType.Reference,
        "note" => DocType.Note,
        "memory" => DocType.Memory,
        _ => throw new ArgumentException($"Unknown doc type: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentGuidanceImportance importance) => importance switch
    {
        AgentGuidanceImportance.Required => "required",
        AgentGuidanceImportance.Important => "important",
        _ => throw new ArgumentOutOfRangeException(nameof(importance), importance, null)
    };

    public static AgentGuidanceImportance ParseAgentGuidanceImportance(string value) => value switch
    {
        "required" => AgentGuidanceImportance.Required,
        "important" => AgentGuidanceImportance.Important,
        _ => throw new ArgumentException($"Unknown agent guidance importance: {value}", nameof(value))
    };

    public static string ToDbValue(this MessageIntent intent) => intent switch
    {
        MessageIntent.General => "general",
        MessageIntent.Note => "note",
        MessageIntent.StatusUpdate => "status_update",
        MessageIntent.Question => "question",
        MessageIntent.Answer => "answer",
        MessageIntent.Handoff => "handoff",
        MessageIntent.ReviewRequest => "review_request",
        MessageIntent.ReviewFeedback => "review_feedback",
        MessageIntent.ReviewApproval => "review_approval",
        MessageIntent.TaskReady => "task_ready",
        MessageIntent.TaskBlocked => "task_blocked",
        MessageIntent.Notification => "notification",
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
    };

    public static MessageIntent ParseMessageIntent(string value) => value switch
    {
        "general" => MessageIntent.General,
        "note" => MessageIntent.Note,
        "status_update" => MessageIntent.StatusUpdate,
        "question" => MessageIntent.Question,
        "answer" => MessageIntent.Answer,
        "handoff" => MessageIntent.Handoff,
        "review_request" => MessageIntent.ReviewRequest,
        "review_feedback" => MessageIntent.ReviewFeedback,
        "review_approval" => MessageIntent.ReviewApproval,
        "task_ready" => MessageIntent.TaskReady,
        "task_blocked" => MessageIntent.TaskBlocked,
        "notification" => MessageIntent.Notification,
        _ => throw new ArgumentException($"Unknown message intent: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentStreamKind kind) => kind switch
    {
        AgentStreamKind.Ops => "ops",
        AgentStreamKind.Message => "message",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static AgentStreamKind ParseAgentStreamKind(string value) => value switch
    {
        "ops" => AgentStreamKind.Ops,
        "message" => AgentStreamKind.Message,
        _ => throw new ArgumentException($"Unknown agent stream kind: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentStreamDeliveryMode deliveryMode) => deliveryMode switch
    {
        AgentStreamDeliveryMode.RecordOnly => "record_only",
        AgentStreamDeliveryMode.Notify => "notify",
        AgentStreamDeliveryMode.Wake => "wake",
        _ => throw new ArgumentOutOfRangeException(nameof(deliveryMode), deliveryMode, null)
    };

    public static AgentStreamDeliveryMode ParseAgentStreamDeliveryMode(string value) => value switch
    {
        "record_only" => AgentStreamDeliveryMode.RecordOnly,
        "notify" => AgentStreamDeliveryMode.Notify,
        "wake" => AgentStreamDeliveryMode.Wake,
        _ => throw new ArgumentException($"Unknown agent stream delivery mode: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentInstanceBindingStatus status) => status switch
    {
        AgentInstanceBindingStatus.Active => "active",
        AgentInstanceBindingStatus.Inactive => "inactive",
        AgentInstanceBindingStatus.Degraded => "degraded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static AgentInstanceBindingStatus ParseAgentInstanceBindingStatus(string value) => value switch
    {
        "active" => AgentInstanceBindingStatus.Active,
        "inactive" => AgentInstanceBindingStatus.Inactive,
        "degraded" => AgentInstanceBindingStatus.Degraded,
        _ => throw new ArgumentException($"Unknown agent instance binding status: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentSessionStatus status) => status switch
    {
        AgentSessionStatus.Active => "active",
        AgentSessionStatus.Inactive => "inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static AgentSessionStatus ParseAgentSessionStatus(string value) => value switch
    {
        "active" => AgentSessionStatus.Active,
        "inactive" => AgentSessionStatus.Inactive,
        _ => throw new ArgumentException($"Unknown agent session status: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentWorkspaceState state) => state switch
    {
        AgentWorkspaceState.Planned => "planned",
        AgentWorkspaceState.Active => "active",
        AgentWorkspaceState.Review => "review",
        AgentWorkspaceState.Complete => "complete",
        AgentWorkspaceState.Failed => "failed",
        AgentWorkspaceState.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static AgentWorkspaceState ParseAgentWorkspaceState(string value) => value switch
    {
        "planned" => AgentWorkspaceState.Planned,
        "active" => AgentWorkspaceState.Active,
        "review" => AgentWorkspaceState.Review,
        "complete" => AgentWorkspaceState.Complete,
        "failed" => AgentWorkspaceState.Failed,
        "archived" => AgentWorkspaceState.Archived,
        _ => throw new ArgumentException($"Unknown agent workspace state: {value}", nameof(value))
    };

    public static string ToDbValue(this AgentWorkspaceCleanupPolicy policy) => policy switch
    {
        AgentWorkspaceCleanupPolicy.Keep => "keep",
        AgentWorkspaceCleanupPolicy.DeleteWorktree => "delete_worktree",
        AgentWorkspaceCleanupPolicy.Archive => "archive",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    public static AgentWorkspaceCleanupPolicy ParseAgentWorkspaceCleanupPolicy(string value) => value switch
    {
        "keep" => AgentWorkspaceCleanupPolicy.Keep,
        "delete_worktree" => AgentWorkspaceCleanupPolicy.DeleteWorktree,
        "archive" => AgentWorkspaceCleanupPolicy.Archive,
        _ => throw new ArgumentException($"Unknown agent workspace cleanup policy: {value}", nameof(value))
    };

    public static string ToDbValue(this DesktopSnapshotState state) => state switch
    {
        DesktopSnapshotState.Ok => "ok",
        DesktopSnapshotState.PathNotVisible => "path_not_visible",
        DesktopSnapshotState.NotGitRepository => "not_git_repository",
        DesktopSnapshotState.GitError => "git_error",
        DesktopSnapshotState.SourceOffline => "source_offline",
        DesktopSnapshotState.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static DesktopSnapshotState ParseDesktopSnapshotState(string value) => value switch
    {
        "ok" => DesktopSnapshotState.Ok,
        "path_not_visible" => DesktopSnapshotState.PathNotVisible,
        "not_git_repository" => DesktopSnapshotState.NotGitRepository,
        "git_error" => DesktopSnapshotState.GitError,
        "source_offline" => DesktopSnapshotState.SourceOffline,
        "missing" => DesktopSnapshotState.Missing,
        _ => throw new ArgumentException($"Unknown desktop snapshot state: {value}", nameof(value))
    };

    public static string ToDbValue(this CollaborationSessionStatus status) => status switch
    {
        CollaborationSessionStatus.Active => "active",
        CollaborationSessionStatus.Resolved => "resolved",
        CollaborationSessionStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static CollaborationSessionStatus ParseCollaborationSessionStatus(string value) => value switch
    {
        "active" => CollaborationSessionStatus.Active,
        "resolved" => CollaborationSessionStatus.Resolved,
        "archived" => CollaborationSessionStatus.Archived,
        _ => throw new ArgumentException($"Unknown collaboration session status: {value}", nameof(value))
    };

    public static string ToDbValue(this CollaborationSegmentType type) => type switch
    {
        CollaborationSegmentType.Heading => "heading",
        CollaborationSegmentType.Paragraph => "paragraph",
        CollaborationSegmentType.CodeBlock => "code_block",
        CollaborationSegmentType.List => "list",
        CollaborationSegmentType.BlockQuote => "block_quote",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static CollaborationSegmentType ParseCollaborationSegmentType(string value) => value switch
    {
        "heading" => CollaborationSegmentType.Heading,
        "paragraph" => CollaborationSegmentType.Paragraph,
        "code_block" => CollaborationSegmentType.CodeBlock,
        "list" => CollaborationSegmentType.List,
        "block_quote" => CollaborationSegmentType.BlockQuote,
        _ => throw new ArgumentException($"Unknown collaboration segment type: {value}", nameof(value))
    };

    public static string ToDbValue(this CollaborationAnnotationType type) => type switch
    {
        CollaborationAnnotationType.Note => "note",
        CollaborationAnnotationType.Skip => "skip",
        CollaborationAnnotationType.Done => "done",
        CollaborationAnnotationType.Flag => "flag",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static CollaborationAnnotationType ParseCollaborationAnnotationType(string value) => value switch
    {
        "note" => CollaborationAnnotationType.Note,
        "skip" => CollaborationAnnotationType.Skip,
        "done" => CollaborationAnnotationType.Done,
        "flag" => CollaborationAnnotationType.Flag,
        _ => throw new ArgumentException($"Unknown collaboration annotation type: {value}", nameof(value))
    };

    public static string ToDbValue(this DispatchStatus status) => status switch
    {
        DispatchStatus.Pending => "pending",
        DispatchStatus.Approved => "approved",
        DispatchStatus.Rejected => "rejected",
        DispatchStatus.Completed => "completed",
        DispatchStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static DispatchStatus ParseDispatchStatus(string value) => value switch
    {
        "pending" => DispatchStatus.Pending,
        "approved" => DispatchStatus.Approved,
        "rejected" => DispatchStatus.Rejected,
        "completed" => DispatchStatus.Completed,
        "expired" => DispatchStatus.Expired,
        _ => throw new ArgumentException($"Unknown dispatch status: {value}", nameof(value))
    };

    public static string ToDbValue(this DispatchTriggerType type) => type switch
    {
        DispatchTriggerType.Message => "message",
        DispatchTriggerType.TaskStatus => "task_status",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static DispatchTriggerType ParseDispatchTriggerType(string value) => value switch
    {
        "message" => DispatchTriggerType.Message,
        "task_status" => DispatchTriggerType.TaskStatus,
        _ => throw new ArgumentException($"Unknown dispatch trigger type: {value}", nameof(value))
    };

    public static string ToDbValue(this ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.ChangesRequested => "changes_requested",
        ReviewVerdict.LooksGood => "looks_good",
        ReviewVerdict.FollowUpNeeded => "follow_up_needed",
        ReviewVerdict.BlockedByDependency => "blocked_by_dependency",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null)
    };

    public static ReviewVerdict ParseReviewVerdict(string value) => value switch
    {
        "changes_requested" => ReviewVerdict.ChangesRequested,
        "looks_good" => ReviewVerdict.LooksGood,
        "follow_up_needed" => ReviewVerdict.FollowUpNeeded,
        "blocked_by_dependency" => ReviewVerdict.BlockedByDependency,
        _ => throw new ArgumentException($"Unknown review verdict: {value}", nameof(value))
    };

    public static string ToDbValue(this ReviewFindingCategory category) => category switch
    {
        ReviewFindingCategory.BlockingBug => "blocking_bug",
        ReviewFindingCategory.AcceptanceGap => "acceptance_gap",
        ReviewFindingCategory.TestWeakness => "test_weakness",
        ReviewFindingCategory.FollowUpCandidate => "follow_up_candidate",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    public static ReviewFindingCategory ParseReviewFindingCategory(string value) => value switch
    {
        "blocking_bug" => ReviewFindingCategory.BlockingBug,
        "acceptance_gap" => ReviewFindingCategory.AcceptanceGap,
        "test_weakness" => ReviewFindingCategory.TestWeakness,
        "follow_up_candidate" => ReviewFindingCategory.FollowUpCandidate,
        _ => throw new ArgumentException($"Unknown review finding category: {value}", nameof(value))
    };

    public static string ToDbValue(this ReviewFindingStatus status) => status switch
    {
        ReviewFindingStatus.Open => "open",
        ReviewFindingStatus.ClaimedFixed => "claimed_fixed",
        ReviewFindingStatus.VerifiedFixed => "verified_fixed",
        ReviewFindingStatus.NotFixed => "not_fixed",
        ReviewFindingStatus.Superseded => "superseded",
        ReviewFindingStatus.SplitToFollowUp => "split_to_follow_up",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static ReviewFindingStatus ParseReviewFindingStatus(string value) => value switch
    {
        "open" => ReviewFindingStatus.Open,
        "claimed_fixed" => ReviewFindingStatus.ClaimedFixed,
        "verified_fixed" => ReviewFindingStatus.VerifiedFixed,
        "not_fixed" => ReviewFindingStatus.NotFixed,
        "superseded" => ReviewFindingStatus.Superseded,
        "split_to_follow_up" => ReviewFindingStatus.SplitToFollowUp,
        _ => throw new ArgumentException($"Unknown review finding status: {value}", nameof(value))
    };

    public static ReviewFindingStatus[]? GetReviewFindingStatuses(string? statusList, bool? resolved)
    {
        if (!string.IsNullOrWhiteSpace(statusList))
        {
            return statusList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseReviewFindingStatus)
                .ToArray();
        }

        return resolved switch
        {
            true =>
            [
                ReviewFindingStatus.VerifiedFixed,
                ReviewFindingStatus.Superseded,
                ReviewFindingStatus.SplitToFollowUp
            ],
            false =>
            [
                ReviewFindingStatus.Open,
                ReviewFindingStatus.ClaimedFixed,
                ReviewFindingStatus.NotFixed
            ],
            _ => null
        };
    }

    public static bool IsResolved(this ReviewFindingStatus status) => status switch
    {
        ReviewFindingStatus.VerifiedFixed => true,
        ReviewFindingStatus.Superseded => true,
        ReviewFindingStatus.SplitToFollowUp => true,
        _ => false
    };

    /// <summary>
    /// Maps AgentRecipientResolutionStatus to compact API-facing string values
    /// used in wake_resolution_status responses and wake_dropped metadata.
    /// </summary>
    public static string ToApiValue(this AgentRecipientResolutionStatus status) => status switch
    {
        AgentRecipientResolutionStatus.Resolved => "resolved",
        AgentRecipientResolutionStatus.MissingRecipient => "missing_recipient",
        AgentRecipientResolutionStatus.MissingBinding => "missing_binding",
        AgentRecipientResolutionStatus.Ambiguous => "ambiguous",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown agent recipient resolution status.")
    };

    public static string ToDbValue(this SessionEventType eventType) => eventType switch
    {
        SessionEventType.Created => "created",
        SessionEventType.Discovered => "discovered",
        SessionEventType.StatusChanged => "status_changed",
        SessionEventType.CapabilitiesChanged => "capabilities_changed",
        SessionEventType.Attached => "attached",
        SessionEventType.Detached => "detached",
        SessionEventType.InputSent => "input_sent",
        SessionEventType.ResizeRequested => "resize_requested",
        SessionEventType.TerminateRequested => "terminate_requested",
        SessionEventType.TerminateCompleted => "terminate_completed",
        SessionEventType.Reconnect => "reconnect",
        SessionEventType.ReconnectRequested => "reconnect_requested",
        SessionEventType.Reconnected => "reconnected",
        SessionEventType.LeaseAcquired => "lease_acquired",
        SessionEventType.LeaseLost => "lease_lost",
        SessionEventType.LeaseConflict => "lease_conflict",
        SessionEventType.Warning => "warning",
        SessionEventType.Crashed => "crashed",
        SessionEventType.Exited => "exited",
        SessionEventType.SnapshotPublished => "snapshot_published",
        SessionEventType.SnapshotPublishFailed => "snapshot_publish_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, null)
    };

    public static SessionEventType ParseSessionEventType(string value) => value switch
    {
        "created" => SessionEventType.Created,
        "discovered" => SessionEventType.Discovered,
        "status_changed" => SessionEventType.StatusChanged,
        "capabilities_changed" => SessionEventType.CapabilitiesChanged,
        "attached" => SessionEventType.Attached,
        "detached" => SessionEventType.Detached,
        "input_sent" => SessionEventType.InputSent,
        "resize_requested" => SessionEventType.ResizeRequested,
        "terminate_requested" => SessionEventType.TerminateRequested,
        "terminate_completed" => SessionEventType.TerminateCompleted,
        "reconnect" => SessionEventType.Reconnect,
        "reconnect_requested" => SessionEventType.ReconnectRequested,
        "reconnected" => SessionEventType.Reconnected,
        "lease_acquired" => SessionEventType.LeaseAcquired,
        "lease_lost" => SessionEventType.LeaseLost,
        "lease_conflict" => SessionEventType.LeaseConflict,
        "warning" => SessionEventType.Warning,
        "crashed" => SessionEventType.Crashed,
        "exited" => SessionEventType.Exited,
        "snapshot_published" => SessionEventType.SnapshotPublished,
        "snapshot_publish_failed" => SessionEventType.SnapshotPublishFailed,
        _ => throw new ArgumentException($"Unknown session event type: {value}", nameof(value))
    };
}
