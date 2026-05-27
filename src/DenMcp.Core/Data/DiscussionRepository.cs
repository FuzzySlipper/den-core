using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IDiscussionRepository
{
    /// <summary>
    /// Creates a discussion thread for a document target.
    /// Validates that the referenced document exists before creating.
    /// </summary>
    Task<DiscussionThread> CreateDocumentThreadAsync(
        string projectId,
        string slug,
        string threadKey,
        string title,
        string createdBy,
        string? summary = null,
        string? metadataJson = null);

    /// <summary>
    /// Gets or creates the default thread for a document idempotently.
    /// If the thread already exists, returns it without modification.
    /// </summary>
    Task<DiscussionThread> GetOrCreateDefaultDocumentThreadAsync(
        string projectId,
        string slug,
        string createdBy);

    /// <summary>
    /// Gets a thread by its primary key.
    /// </summary>
    Task<DiscussionThread?> GetThreadByIdAsync(int threadId);

    /// <summary>
    /// Lists threads for a document target.
    /// </summary>
    Task<List<DiscussionThread>> ListDocumentThreadsAsync(string projectId, string slug);

    /// <summary>
    /// Updates thread status, summary, resolution_summary, or title.
    /// </summary>
    Task<DiscussionThread> UpdateThreadAsync(DiscussionThread thread);

    /// <summary>
    /// Adds a root-level comment to a thread.
    /// </summary>
    Task<DiscussionComment> AddCommentAsync(
        int threadId,
        string bodyMarkdown,
        string authorIdentity,
        string? commentKind = null,
        string? mentionsJson = null,
        string? sourceRefsJson = null,
        string? metadataJson = null);

    /// <summary>
    /// Adds a reply comment to an existing comment.
    /// </summary>
    Task<DiscussionComment> AddReplyAsync(
        int threadId,
        int parentCommentId,
        string bodyMarkdown,
        string authorIdentity,
        string? commentKind = null,
        string? mentionsJson = null,
        string? sourceRefsJson = null,
        string? metadataJson = null);

    /// <summary>
    /// Lists comments for a thread in chronological order with parent pointers.
    /// </summary>
    Task<List<DiscussionCommentSummary>> ListCommentsAsync(int threadId);

    /// <summary>
    /// Gets a single comment by its primary key.
    /// </summary>
    Task<DiscussionComment?> GetCommentByIdAsync(int commentId);
}

public sealed class DiscussionRepository : IDiscussionRepository
{
    private readonly DbConnectionFactory _db;
    private readonly IDocumentRepository _documents;

    public DiscussionRepository(DbConnectionFactory db, IDocumentRepository documents)
    {
        _db = db;
        _documents = documents;
    }

    public async Task<DiscussionThread> CreateDocumentThreadAsync(
        string projectId,
        string slug,
        string threadKey,
        string title,
        string createdBy,
        string? summary = null,
        string? metadataJson = null)
    {
        ValidateThreadStatus(DiscussionThreadStatus.Open);

        // Validate the target document exists before creating the thread
        var document = await _documents.GetAsync(projectId, slug);
        if (document is null)
            throw new InvalidOperationException(
                $"Document project_id={projectId} slug={slug} not found. Cannot create discussion thread.");

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discussion_threads (
                target_type, target_project_id, target_id, target_slug,
                thread_key, title, status, created_by, summary, metadata_json
            )
            VALUES (
                @targetType, @targetProjectId, NULL, @targetSlug,
                @threadKey, @title, @status, @createdBy, @summary, @metadataJson
            )
            RETURNING id, target_type, target_project_id, target_id, target_slug,
                      target_anchor, thread_key, title, status, created_by,
                      summary, resolution_summary, metadata_json, last_comment_at,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@targetType", "document");
        cmd.Parameters.AddWithValue("@targetProjectId", projectId);
        cmd.Parameters.AddWithValue("@targetSlug", slug);
        cmd.Parameters.AddWithValue("@threadKey", threadKey);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@status", DiscussionThreadStatus.Open);
        cmd.Parameters.AddWithValue("@createdBy", createdBy);
        cmd.Parameters.AddWithValue("@summary", summary ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", metadataJson ?? (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadThread(reader);
    }

    public async Task<DiscussionThread> GetOrCreateDefaultDocumentThreadAsync(
        string projectId,
        string slug,
        string createdBy)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_type, target_project_id, target_id, target_slug,
                   target_anchor, thread_key, title, status, created_by,
                   summary, resolution_summary, metadata_json, last_comment_at,
                   created_at, updated_at
            FROM discussion_threads
            WHERE target_type = 'document'
              AND target_project_id = @targetProjectId
              AND target_slug = @targetSlug
              AND thread_key = 'default'
            """;
        cmd.Parameters.AddWithValue("@targetProjectId", projectId);
        cmd.Parameters.AddWithValue("@targetSlug", slug);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return ReadThread(reader);

        // Not found — create it idempotently
        return await CreateDocumentThreadAsync(projectId, slug, "default",
            $"Discussion for {slug}", createdBy);
    }

    public async Task<DiscussionThread?> GetThreadByIdAsync(int threadId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_type, target_project_id, target_id, target_slug,
                   target_anchor, thread_key, title, status, created_by,
                   summary, resolution_summary, metadata_json, last_comment_at,
                   created_at, updated_at
            FROM discussion_threads
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", threadId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadThread(reader) : null;
    }

    public async Task<List<DiscussionThread>> ListDocumentThreadsAsync(string projectId, string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_type, target_project_id, target_id, target_slug,
                   target_anchor, thread_key, title, status, created_by,
                   summary, resolution_summary, metadata_json, last_comment_at,
                   created_at, updated_at
            FROM discussion_threads
            WHERE target_type = 'document'
              AND target_project_id = @targetProjectId
              AND target_slug = @targetSlug
            ORDER BY updated_at DESC
            """;
        cmd.Parameters.AddWithValue("@targetProjectId", projectId);
        cmd.Parameters.AddWithValue("@targetSlug", slug);

        var results = new List<DiscussionThread>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadThread(reader));
        return results;
    }

    public async Task<DiscussionThread> UpdateThreadAsync(DiscussionThread thread)
    {
        ValidateThreadStatus(thread.Status);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE discussion_threads
            SET status = @status,
                title = @title,
                summary = @summary,
                resolution_summary = @resolutionSummary,
                metadata_json = @metadataJson,
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, target_type, target_project_id, target_id, target_slug,
                      target_anchor, thread_key, title, status, created_by,
                      summary, resolution_summary, metadata_json, last_comment_at,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", thread.Id);
        cmd.Parameters.AddWithValue("@status", thread.Status);
        cmd.Parameters.AddWithValue("@title", thread.Title ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@summary", thread.Summary ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@resolutionSummary", thread.ResolutionSummary ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", thread.MetadataJson ?? (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadThread(reader);
    }

    public async Task<DiscussionComment> AddCommentAsync(
        int threadId,
        string bodyMarkdown,
        string authorIdentity,
        string? commentKind = null,
        string? mentionsJson = null,
        string? sourceRefsJson = null,
        string? metadataJson = null)
    {
        var kind = commentKind ?? DiscussionCommentKind.Comment;
        ValidateCommentKind(kind);
        ValidateCommentStatus(DiscussionCommentStatus.Active);

        await using var conn = await _db.CreateConnectionAsync();

        // Ensure the thread exists
        var thread = await GetThreadByIdInternalAsync(conn, threadId);
        if (thread is null)
            throw new InvalidOperationException($"Thread id={threadId} not found.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discussion_comments (
                thread_id, parent_comment_id, author_identity, body_markdown,
                comment_kind, status, mentions_json, source_refs_json, metadata_json
            )
            VALUES (
                @threadId, NULL, @authorIdentity, @bodyMarkdown,
                @commentKind, @status, @mentionsJson, @sourceRefsJson, @metadataJson
            )
            RETURNING id, thread_id, parent_comment_id, author_identity, body_markdown,
                      comment_kind, status, mentions_json, source_refs_json, metadata_json,
                      created_at, edited_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@threadId", threadId);
        cmd.Parameters.AddWithValue("@authorIdentity", authorIdentity);
        cmd.Parameters.AddWithValue("@bodyMarkdown", bodyMarkdown);
        cmd.Parameters.AddWithValue("@commentKind", kind);
        cmd.Parameters.AddWithValue("@status", DiscussionCommentStatus.Active);
        cmd.Parameters.AddWithValue("@mentionsJson", mentionsJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceRefsJson", sourceRefsJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", metadataJson ?? (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var comment = ReadComment(reader);

        // Update last_comment_at on the thread
        await TouchThreadLastCommentAsync(conn, threadId);

        return comment;
    }

    public async Task<DiscussionComment> AddReplyAsync(
        int threadId,
        int parentCommentId,
        string bodyMarkdown,
        string authorIdentity,
        string? commentKind = null,
        string? mentionsJson = null,
        string? sourceRefsJson = null,
        string? metadataJson = null)
    {
        var kind = commentKind ?? DiscussionCommentKind.Comment;
        ValidateCommentKind(kind);
        ValidateCommentStatus(DiscussionCommentStatus.Active);

        await using var conn = await _db.CreateConnectionAsync();

        // Ensure the thread and parent comment exist
        var thread = await GetThreadByIdInternalAsync(conn, threadId);
        if (thread is null)
            throw new InvalidOperationException($"Thread id={threadId} not found.");

        var parent = await GetCommentByIdInternalAsync(conn, parentCommentId);
        if (parent is null)
            throw new InvalidOperationException($"Parent comment id={parentCommentId} not found.");

        if (parent.ThreadId != threadId)
            throw new InvalidOperationException(
                $"Parent comment id={parentCommentId} does not belong to thread id={threadId}.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discussion_comments (
                thread_id, parent_comment_id, author_identity, body_markdown,
                comment_kind, status, mentions_json, source_refs_json, metadata_json
            )
            VALUES (
                @threadId, @parentCommentId, @authorIdentity, @bodyMarkdown,
                @commentKind, @status, @mentionsJson, @sourceRefsJson, @metadataJson
            )
            RETURNING id, thread_id, parent_comment_id, author_identity, body_markdown,
                      comment_kind, status, mentions_json, source_refs_json, metadata_json,
                      created_at, edited_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@threadId", threadId);
        cmd.Parameters.AddWithValue("@parentCommentId", parentCommentId);
        cmd.Parameters.AddWithValue("@authorIdentity", authorIdentity);
        cmd.Parameters.AddWithValue("@bodyMarkdown", bodyMarkdown);
        cmd.Parameters.AddWithValue("@commentKind", kind);
        cmd.Parameters.AddWithValue("@status", DiscussionCommentStatus.Active);
        cmd.Parameters.AddWithValue("@mentionsJson", mentionsJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceRefsJson", sourceRefsJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", metadataJson ?? (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var comment = ReadComment(reader);

        // Update last_comment_at on the thread
        await TouchThreadLastCommentAsync(conn, threadId);

        return comment;
    }

    public async Task<List<DiscussionCommentSummary>> ListCommentsAsync(int threadId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, thread_id, parent_comment_id, author_identity, body_markdown,
                   comment_kind, status, mentions_json, source_refs_json, metadata_json,
                   created_at, edited_at
            FROM discussion_comments
            WHERE thread_id = @threadId
            ORDER BY created_at ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("@threadId", threadId);

        var results = new List<DiscussionCommentSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DiscussionCommentSummary
            {
                Id = reader.GetInt32(0),
                ThreadId = reader.GetInt32(1),
                ParentCommentId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                AuthorIdentity = reader.GetString(3),
                BodyMarkdown = reader.GetString(4),
                CommentKind = reader.GetString(5),
                Status = reader.GetString(6),
                MentionsJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                SourceRefsJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                MetadataJson = reader.IsDBNull(9) ? null : reader.GetString(9),
                CreatedAt = DateTime.Parse(reader.GetString(10)),
                EditedAt = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }
        return results;
    }

    public async Task<DiscussionComment?> GetCommentByIdAsync(int commentId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        return await GetCommentByIdInternalAsync(conn, commentId);
    }

    // ---- Internal helpers ----

    private static async Task<DiscussionThread?> GetThreadByIdInternalAsync(SqliteConnection conn, int threadId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_type, target_project_id, target_id, target_slug,
                   target_anchor, thread_key, title, status, created_by,
                   summary, resolution_summary, metadata_json, last_comment_at,
                   created_at, updated_at
            FROM discussion_threads
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", threadId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadThread(reader) : null;
    }

    private static async Task<DiscussionComment?> GetCommentByIdInternalAsync(SqliteConnection conn, int commentId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, thread_id, parent_comment_id, author_identity, body_markdown,
                   comment_kind, status, mentions_json, source_refs_json, metadata_json,
                   created_at, edited_at, updated_at
            FROM discussion_comments
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", commentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadComment(reader) : null;
    }

    private static async Task TouchThreadLastCommentAsync(SqliteConnection conn, int threadId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE discussion_threads
            SET last_comment_at = datetime('now'), updated_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", threadId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static DiscussionThread ReadThread(SqliteDataReader reader)
    {
        return new DiscussionThread
        {
            Id = reader.GetInt32(0),
            TargetType = reader.GetString(1),
            TargetProjectId = reader.GetString(2),
            TargetId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            TargetSlug = reader.IsDBNull(4) ? null : reader.GetString(4),
            TargetAnchor = reader.IsDBNull(5) ? null : reader.GetString(5),
            ThreadKey = reader.GetString(6),
            Title = reader.IsDBNull(7) ? null : reader.GetString(7),
            Status = reader.GetString(8),
            CreatedBy = reader.GetString(9),
            Summary = reader.IsDBNull(10) ? null : reader.GetString(10),
            ResolutionSummary = reader.IsDBNull(11) ? null : reader.GetString(11),
            MetadataJson = reader.IsDBNull(12) ? null : reader.GetString(12),
            LastCommentAt = reader.IsDBNull(13) ? null : reader.GetString(13),
            CreatedAt = DateTime.Parse(reader.GetString(14)),
            UpdatedAt = DateTime.Parse(reader.GetString(15)),
        };
    }

    private static DiscussionComment ReadComment(SqliteDataReader reader)
    {
        return new DiscussionComment
        {
            Id = reader.GetInt32(0),
            ThreadId = reader.GetInt32(1),
            ParentCommentId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            AuthorIdentity = reader.GetString(3),
            BodyMarkdown = reader.GetString(4),
            CommentKind = reader.GetString(5),
            Status = reader.GetString(6),
            MentionsJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            SourceRefsJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            MetadataJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAt = DateTime.Parse(reader.GetString(10)),
            EditedAt = reader.IsDBNull(11) ? null : reader.GetString(11),
            UpdatedAt = DateTime.Parse(reader.GetString(12)),
        };
    }

    private static void ValidateThreadStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status) || !DiscussionThreadStatus.IsValid(status))
            throw new ArgumentException(
                $"Invalid thread status '{status}'. Allowed: {string.Join(", ", DiscussionThreadStatus.Allowed)}",
                nameof(status));
    }

    private static void ValidateCommentKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || !DiscussionCommentKind.IsValid(kind))
            throw new ArgumentException(
                $"Invalid comment kind '{kind}'. Allowed: {string.Join(", ", DiscussionCommentKind.Allowed)}",
                nameof(kind));
    }

    private static void ValidateCommentStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status) || !DiscussionCommentStatus.IsValid(status))
            throw new ArgumentException(
                $"Invalid comment status '{status}'. Allowed: {string.Join(", ", DiscussionCommentStatus.Allowed)}",
                nameof(status));
    }
}
