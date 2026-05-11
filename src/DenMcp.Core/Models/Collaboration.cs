using System.Text.Json;

namespace DenMcp.Core.Models;

public enum CollaborationSessionStatus
{
    Active,
    Resolved,
    Archived
}

public enum CollaborationSegmentType
{
    Heading,
    Paragraph,
    CodeBlock,
    List,
    BlockQuote
}

public enum CollaborationAnnotationType
{
    Note,
    Skip,
    Done,
    Flag
}

public sealed class CollaborationSession
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public long? MessageId { get; set; }
    public long? AgentStreamEntryId { get; set; }
    public string? PiRunId { get; set; }
    public string? PiSessionId { get; set; }
    public string? DesktopOperatorSessionId { get; set; }
    public string? Title { get; set; }
    public CollaborationSessionStatus Status { get; set; } = CollaborationSessionStatus.Active;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<CollaborationTurn> Turns { get; set; } = [];
    public List<CollaborationAnnotation> Annotations { get; set; } = [];
    public List<CollaborationResponseDraft> Drafts { get; set; } = [];
}

public sealed class CollaborationTurn
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int TurnOrder { get; set; }
    public string? Role { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceRef { get; set; }
    public string? SourceLabel { get; set; }
    public string? SourceUri { get; set; }
    public JsonElement? SourceContext { get; set; }
    public required string RawMarkdown { get; set; }
    public required string SourceContentHash { get; set; }
    public required string SegmenterVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<CollaborationSegment> Segments { get; set; } = [];
}

public sealed class CollaborationSegment
{
    public long Id { get; set; }
    public long TurnId { get; set; }
    public int SequenceNumber { get; set; }
    public required string SegmentHash { get; set; }
    public CollaborationSegmentType SegmentType { get; set; }
    public required string RawMarkdown { get; set; }
    public string? Text { get; set; }
    public int? HeadingLevel { get; set; }
    public string? CodeLanguage { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CollaborationAnnotation
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public long TurnId { get; set; }
    public long SegmentId { get; set; }
    public required string SegmentHash { get; set; }
    public CollaborationAnnotationType AnnotationType { get; set; }
    public string? Body { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CollaborationResponseDraft
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public long? TurnId { get; set; }
    public required string Content { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CollaborationSessionListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public CollaborationSessionStatus? Status { get; set; }
    public int Limit { get; set; } = 50;
}

public sealed class CreateCollaborationSessionRequestModel
{
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public long? MessageId { get; set; }
    public long? AgentStreamEntryId { get; set; }
    public string? PiRunId { get; set; }
    public string? PiSessionId { get; set; }
    public string? DesktopOperatorSessionId { get; set; }
    public string? Title { get; set; }
    public string? CreatedBy { get; set; }
    public required CreateCollaborationTurnRequestModel InitialTurn { get; set; }
}

public sealed class CreateCollaborationTurnRequestModel
{
    public string? Role { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceRef { get; set; }
    public string? SourceLabel { get; set; }
    public string? SourceUri { get; set; }
    public JsonElement? SourceContext { get; set; }
    public required string RawMarkdown { get; set; }
}

public sealed class UpdateSessionStatusRequestModel
{
    public CollaborationSessionStatus ExpectedStatus { get; set; }
    public CollaborationSessionStatus Status { get; set; }
}

public sealed class CollaborationAnnotationListOptions
{
    public long? TurnId { get; set; }
    public long? SegmentId { get; set; }
    public int Limit { get; set; } = 50;
}
