using System.Text.Json;

namespace DenMcp.Core.Models;

/// <summary>
/// First-class discussion thread, separate from general messages and document body.
/// Each thread targets a specific entity via generic target columns.
/// </summary>
public sealed class DiscussionThread
{
    public int Id { get; set; }
    public required string TargetType { get; set; }
    public required string TargetProjectId { get; set; }
    public int? TargetId { get; set; }
    public string? TargetSlug { get; set; }
    public string? TargetAnchor { get; set; }
    public required string ThreadKey { get; set; }
    public required string Title { get; set; }
    public required string Status { get; set; } = DiscussionThreadStatus.Open;
    public required string CreatedBy { get; set; }
    public string? Summary { get; set; }
    public string? ResolutionSummary { get; set; }
    public string? MetadataJson { get; set; }
    public string? LastCommentAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public T? DeserializeMetadata<T>() where T : class =>
        MetadataJson is not null ? JsonSerializer.Deserialize<T>(MetadataJson) : null;
}

/// <summary>
/// A comment within a discussion thread. Supports replies via parent_comment_id.
/// </summary>
public sealed class DiscussionComment
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public int? ParentCommentId { get; set; }
    public required string AuthorIdentity { get; set; }
    public required string BodyMarkdown { get; set; }
    public required string CommentKind { get; set; } = DiscussionCommentKind.Comment;
    public required string Status { get; set; } = DiscussionCommentStatus.Active;
    public string? MentionsJson { get; set; }
    public string? SourceRefsJson { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? EditedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Result shape for listing comments with parent pointers.
/// Includes Id, ParentCommentId so the caller can reconstruct trees client-side.
/// </summary>
public sealed class DiscussionCommentSummary
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public int? ParentCommentId { get; set; }
    public required string AuthorIdentity { get; set; }
    public required string BodyMarkdown { get; set; }
    public required string CommentKind { get; set; }
    public required string Status { get; set; }
    public string? MentionsJson { get; set; }
    public string? SourceRefsJson { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? EditedAt { get; set; }
}

/// <summary>
/// Known thread status values.
/// </summary>
public static class DiscussionThreadStatus
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Archived = "archived";

    public static readonly string[] Allowed = [Open, Resolved, Archived];

    public static bool IsValid(string value) => Allowed.Contains(value);
}

/// <summary>
/// Known comment kind values.
/// </summary>
public static class DiscussionCommentKind
{
    public const string Comment = "comment";
    public const string Question = "question";
    public const string Answer = "answer";
    public const string Resolution = "resolution";
    public const string VersionNote = "version_note";

    public static readonly string[] Allowed = [Comment, Question, Answer, Resolution, VersionNote];

    public static bool IsValid(string value) => Allowed.Contains(value);
}

/// <summary>
/// Known comment status values.
/// </summary>
public static class DiscussionCommentStatus
{
    public const string Active = "active";
    public const string Resolved = "resolved";
    public const string Hidden = "hidden";
    public const string Deleted = "deleted";

    public static readonly string[] Allowed = [Active, Resolved, Hidden, Deleted];

    public static bool IsValid(string value) => Allowed.Contains(value);
}
