using System.Text.Json;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface ICollaborationRepository
{
    Task<CollaborationSession> CreateSessionAsync(CreateCollaborationSessionRequestModel request);
    Task<CollaborationSession?> GetSessionAsync(string projectId, long sessionId);
    Task<List<CollaborationSession>> ListSessionsAsync(CollaborationSessionListOptions options);
    Task<CollaborationTurn> AddTurnAsync(string projectId, long sessionId, CreateCollaborationTurnRequestModel request);
    Task<CollaborationAnnotation> CreateAnnotationAsync(string projectId, long sessionId, long turnId, long segmentId, CollaborationAnnotationType annotationType, string? body, string? createdBy);
    Task<CollaborationAnnotation> UpdateAnnotationAsync(string projectId, long sessionId, long annotationId, int expectedRevision, CollaborationAnnotationType annotationType, string? body, string? updatedBy);
    Task<CollaborationSession> UpdateSessionStatusAsync(string projectId, long sessionId, CollaborationSessionStatus expectedStatus, CollaborationSessionStatus status);
    Task<List<CollaborationAnnotation>> ListAnnotationsAsync(string projectId, long sessionId, CollaborationAnnotationListOptions options);
    Task<CollaborationAnnotation> DeleteAnnotationAsync(string projectId, long sessionId, long annotationId, int expectedRevision);
    Task<CollaborationResponseDraft> CreateDraftAsync(string projectId, long sessionId, long? turnId, string content, string? createdBy);
    Task<CollaborationResponseDraft> UpdateDraftAsync(string projectId, long sessionId, long draftId, int expectedRevision, string content, string? updatedBy);
}

public sealed class CollaborationConflictException : Exception
{
    public CollaborationConflictException(string message) : base(message) { }
}

public sealed class CollaborationRepository : ICollaborationRepository
{
    // Segment identity is snapshot-scoped: each immutable turn stores a 1-based
    // sequence_number for ordering plus a deterministic segment_hash of
    // segmenter_version, sequence_number, segment type, and raw markdown. If parser
    // logic changes, new turns use a new segmenter_version/hash set; existing
    // snapshots and annotations remain anchored to their stored segment rows.
    public static string SegmenterVersion => MarkdownBlockSegmenter.DefaultSegmenterVersion;

    private const string SessionColumns = "id, project_id, task_id, message_id, agent_stream_entry_id, pi_run_id, pi_session_id, desktop_operator_session_id, title, status, created_by, created_at, updated_at";
    private const string TurnColumns = "id, session_id, turn_order, role, source_kind, source_ref, source_label, source_uri, source_context, raw_markdown, source_content_hash, segmenter_version, created_at";
    private const string SegmentColumns = "id, turn_id, sequence_number, segment_hash, segment_type, raw_markdown, text, heading_level, code_language, created_at";
    private const string AnnotationColumns = "id, session_id, turn_id, segment_id, segment_hash, annotation_type, body, created_by, updated_by, revision, created_at, updated_at";
    private const string DraftColumns = "id, session_id, turn_id, content, created_by, updated_by, revision, created_at, updated_at";

    private readonly DbConnectionFactory _db;

    public CollaborationRepository(DbConnectionFactory db) => _db = db;

    public async Task<CollaborationSession> CreateSessionAsync(CreateCollaborationSessionRequestModel request)
    {
        ValidateSessionRequest(request);

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO collaboration_sessions (
                project_id, task_id, message_id, agent_stream_entry_id, pi_run_id,
                pi_session_id, desktop_operator_session_id, title, created_by
            ) VALUES (
                @projectId, @taskId, @messageId, @agentStreamEntryId, @piRunId,
                @piSessionId, @desktopOperatorSessionId, @title, @createdBy
            )
            RETURNING {SessionColumns}
            """;
        AddSessionParameters(cmd, request);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var session = ReadSession(reader);
        await reader.DisposeAsync();

        var turn = await InsertTurnAsync(conn, session.Id, request.InitialTurn);
        session.Turns.Add(turn);

        await tx.CommitAsync();
        return (await GetSessionAsync(session.ProjectId, session.Id))!;
    }

    public async Task<CollaborationSession?> GetSessionAsync(string projectId, long sessionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || sessionId <= 0)
            return null;

        await using var conn = await _db.CreateConnectionAsync();
        var session = await GetSessionHeaderAsync(conn, projectId, sessionId);
        if (session is null)
            return null;

        session.Turns = await LoadTurnsAsync(conn, session.Id);
        session.Annotations = await LoadAnnotationsAsync(conn, session.Id);
        session.Drafts = await LoadDraftsAsync(conn, session.Id);
        return session;
    }

    public async Task<List<CollaborationSession>> ListSessionsAsync(CollaborationSessionListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId.Trim());
        }
        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId.Value);
        }
        if (options.Status is not null)
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", options.Status.Value.ToDbValue());
        }

        var whereClause = where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
        cmd.CommandText = $"""
            SELECT {SessionColumns}
            FROM collaboration_sessions
            {whereClause}
            ORDER BY updated_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var sessions = new List<CollaborationSession>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sessions.Add(ReadSession(reader));
        return sessions;
    }

    public async Task<CollaborationTurn> AddTurnAsync(string projectId, long sessionId, CreateCollaborationTurnRequestModel request)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project id is required.", nameof(projectId));
        ValidateTurnRequest(request);

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var session = await GetSessionHeaderAsync(conn, projectId, sessionId);
        if (session is null)
            throw new KeyNotFoundException($"Collaboration session {sessionId} was not found in project '{projectId}'.");

        var turn = await InsertTurnAsync(conn, sessionId, request);
        await TouchSessionAsync(conn, sessionId);
        await tx.CommitAsync();
        return turn;
    }

    public async Task<CollaborationAnnotation> CreateAnnotationAsync(string projectId, long sessionId, long turnId, long segmentId, CollaborationAnnotationType annotationType, string? body, string? createdBy)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await EnsureSessionExistsAsync(conn, projectId, sessionId);
        var segment = await GetSegmentAsync(conn, sessionId, turnId, segmentId);
        if (segment is null)
            throw new KeyNotFoundException($"Segment {segmentId} was not found for turn {turnId} in collaboration session {sessionId}.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO collaboration_annotations (
                session_id, turn_id, segment_id, segment_hash, annotation_type, body, created_by, updated_by
            ) VALUES (
                @sessionId, @turnId, @segmentId, @segmentHash, @annotationType, @body, @createdBy, @updatedBy
            )
            RETURNING {AnnotationColumns}
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@turnId", turnId);
        cmd.Parameters.AddWithValue("@segmentId", segmentId);
        cmd.Parameters.AddWithValue("@segmentHash", segment.SegmentHash);
        cmd.Parameters.AddWithValue("@annotationType", annotationType.ToDbValue());
        cmd.Parameters.AddWithValue("@body", NullIfWhiteSpace(body));
        cmd.Parameters.AddWithValue("@createdBy", NullIfWhiteSpace(createdBy));
        cmd.Parameters.AddWithValue("@updatedBy", NullIfWhiteSpace(createdBy));
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var annotation = ReadAnnotation(reader);
        await reader.DisposeAsync();
        await TouchSessionAsync(conn, sessionId);
        await tx.CommitAsync();
        return annotation;
    }

    public async Task<CollaborationAnnotation> UpdateAnnotationAsync(string projectId, long sessionId, long annotationId, int expectedRevision, CollaborationAnnotationType annotationType, string? body, string? updatedBy)
    {
        if (expectedRevision <= 0)
            throw new ArgumentException("Expected revision must be positive.", nameof(expectedRevision));

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await EnsureSessionExistsAsync(conn, projectId, sessionId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE collaboration_annotations SET
                annotation_type = @annotationType,
                body = @body,
                updated_by = @updatedBy,
                revision = revision + 1,
                updated_at = datetime('now')
            WHERE id = @annotationId
              AND session_id = @sessionId
              AND revision = @expectedRevision
            RETURNING {AnnotationColumns}
            """;
        cmd.Parameters.AddWithValue("@annotationId", annotationId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@expectedRevision", expectedRevision);
        cmd.Parameters.AddWithValue("@annotationType", annotationType.ToDbValue());
        cmd.Parameters.AddWithValue("@body", NullIfWhiteSpace(body));
        cmd.Parameters.AddWithValue("@updatedBy", NullIfWhiteSpace(updatedBy));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var updated = ReadAnnotation(reader);
            await reader.DisposeAsync();
            await TouchSessionAsync(conn, sessionId);
            await tx.CommitAsync();
            return updated;
        }

        await reader.DisposeAsync();
        if (!await AnnotationExistsAsync(conn, sessionId, annotationId))
            throw new KeyNotFoundException($"Annotation {annotationId} was not found in collaboration session {sessionId}.");
        throw new CollaborationConflictException($"Annotation {annotationId} has changed since revision {expectedRevision}.");
    }

    public async Task<CollaborationResponseDraft> CreateDraftAsync(string projectId, long sessionId, long? turnId, string content, string? createdBy)
    {
        ValidateDraftContent(content);
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await EnsureSessionExistsAsync(conn, projectId, sessionId);
        if (turnId is not null && !await TurnBelongsToSessionAsync(conn, sessionId, turnId.Value))
            throw new KeyNotFoundException($"Turn {turnId} was not found in collaboration session {sessionId}.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO collaboration_response_drafts (session_id, turn_id, content, created_by, updated_by)
            VALUES (@sessionId, @turnId, @content, @createdBy, @updatedBy)
            RETURNING {DraftColumns}
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@turnId", (object?)turnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@createdBy", NullIfWhiteSpace(createdBy));
        cmd.Parameters.AddWithValue("@updatedBy", NullIfWhiteSpace(createdBy));
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var draft = ReadDraft(reader);
        await reader.DisposeAsync();
        await TouchSessionAsync(conn, sessionId);
        await tx.CommitAsync();
        return draft;
    }

    public async Task<CollaborationResponseDraft> UpdateDraftAsync(string projectId, long sessionId, long draftId, int expectedRevision, string content, string? updatedBy)
    {
        if (expectedRevision <= 0)
            throw new ArgumentException("Expected revision must be positive.", nameof(expectedRevision));
        ValidateDraftContent(content);

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await EnsureSessionExistsAsync(conn, projectId, sessionId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE collaboration_response_drafts SET
                content = @content,
                updated_by = @updatedBy,
                revision = revision + 1,
                updated_at = datetime('now')
            WHERE id = @draftId
              AND session_id = @sessionId
              AND revision = @expectedRevision
            RETURNING {DraftColumns}
            """;
        cmd.Parameters.AddWithValue("@draftId", draftId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@expectedRevision", expectedRevision);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@updatedBy", NullIfWhiteSpace(updatedBy));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var updated = ReadDraft(reader);
            await reader.DisposeAsync();
            await TouchSessionAsync(conn, sessionId);
            await tx.CommitAsync();
            return updated;
        }

        await reader.DisposeAsync();
        if (!await DraftExistsAsync(conn, sessionId, draftId))
            throw new KeyNotFoundException($"Response draft {draftId} was not found in collaboration session {sessionId}.");
        throw new CollaborationConflictException($"Response draft {draftId} has changed since revision {expectedRevision}.");
    }

    public async Task<CollaborationSession> UpdateSessionStatusAsync(string projectId, long sessionId, CollaborationSessionStatus expectedStatus, CollaborationSessionStatus status)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project id is required.", nameof(projectId));

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var session = await GetSessionHeaderAsync(conn, projectId, sessionId);
        if (session is null)
            throw new KeyNotFoundException($"Collaboration session {sessionId} was not found in project '{projectId}'.");

        if (session.Status != expectedStatus)
            throw new CollaborationConflictException($"Collaboration session {sessionId} has status '{session.Status.ToDbValue()}' but expected '{expectedStatus.ToDbValue()}'.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE collaboration_sessions SET
                status = @status,
                updated_at = datetime('now')
            WHERE id = @sessionId
              AND project_id = @projectId
            RETURNING {SessionColumns}
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        cmd.Parameters.AddWithValue("@status", status.ToDbValue());

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var updated = ReadSession(reader);
        await reader.DisposeAsync();
        await tx.CommitAsync();
        return updated;
    }

    public async Task<List<CollaborationAnnotation>> ListAnnotationsAsync(string projectId, long sessionId, CollaborationAnnotationListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await EnsureSessionExistsAsync(conn, projectId, sessionId);

        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "session_id = @sessionId" };
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        if (options.TurnId is not null)
        {
            where.Add("turn_id = @turnId");
            cmd.Parameters.AddWithValue("@turnId", options.TurnId.Value);
        }
        if (options.SegmentId is not null)
        {
            where.Add("segment_id = @segmentId");
            cmd.Parameters.AddWithValue("@segmentId", options.SegmentId.Value);
        }

        var whereClause = string.Join(" AND ", where);
        cmd.CommandText = $"""
            SELECT {AnnotationColumns}
            FROM collaboration_annotations
            WHERE {whereClause}
            ORDER BY updated_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var annotations = new List<CollaborationAnnotation>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            annotations.Add(ReadAnnotation(reader));
        return annotations;
    }

    public async Task<CollaborationAnnotation> DeleteAnnotationAsync(string projectId, long sessionId, long annotationId, int expectedRevision)
    {
        if (expectedRevision <= 0)
            throw new ArgumentException("Expected revision must be positive.", nameof(expectedRevision));

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await EnsureSessionExistsAsync(conn, projectId, sessionId);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM collaboration_annotations
            WHERE id = @annotationId
              AND session_id = @sessionId
              AND revision = @expectedRevision
            RETURNING {AnnotationColumns}
            """;
        cmd.Parameters.AddWithValue("@annotationId", annotationId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@expectedRevision", expectedRevision);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var deleted = ReadAnnotation(reader);
            await reader.DisposeAsync();
            await TouchSessionAsync(conn, sessionId);
            await tx.CommitAsync();
            return deleted;
        }

        await reader.DisposeAsync();
        if (!await AnnotationExistsAsync(conn, sessionId, annotationId))
            throw new KeyNotFoundException($"Annotation {annotationId} was not found in collaboration session {sessionId}.");
        throw new CollaborationConflictException($"Annotation {annotationId} has changed since revision {expectedRevision}.");
    }

    private static async Task<CollaborationTurn> InsertTurnAsync(SqliteConnection conn, long sessionId, CreateCollaborationTurnRequestModel request)
    {
        ValidateTurnRequest(request);
        var sourceContentHash = HashText(request.RawMarkdown);
        var segments = MarkdownBlockSegmenter.Segment(request.RawMarkdown, SegmenterVersion);

        if (segments.Count == 0)
            throw new ArgumentException("The provided markdown does not produce any annotatable segments.");

        await using var orderCmd = conn.CreateCommand();
        orderCmd.CommandText = "SELECT COALESCE(MAX(turn_order), 0) + 1 FROM collaboration_turns WHERE session_id = @sessionId";
        orderCmd.Parameters.AddWithValue("@sessionId", sessionId);
        var turnOrder = Convert.ToInt32(await orderCmd.ExecuteScalarAsync());

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO collaboration_turns (
                session_id, turn_order, role, source_kind, source_ref, source_label,
                source_uri, source_context, raw_markdown, source_content_hash, segmenter_version
            ) VALUES (
                @sessionId, @turnOrder, @role, @sourceKind, @sourceRef, @sourceLabel,
                @sourceUri, @sourceContext, @rawMarkdown, @sourceContentHash, @segmenterVersion
            )
            RETURNING {TurnColumns}
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@turnOrder", turnOrder);
        cmd.Parameters.AddWithValue("@role", NullIfWhiteSpace(request.Role));
        cmd.Parameters.AddWithValue("@sourceKind", NullIfWhiteSpace(request.SourceKind));
        cmd.Parameters.AddWithValue("@sourceRef", NullIfWhiteSpace(request.SourceRef));
        cmd.Parameters.AddWithValue("@sourceLabel", NullIfWhiteSpace(request.SourceLabel));
        cmd.Parameters.AddWithValue("@sourceUri", NullIfWhiteSpace(request.SourceUri));
        cmd.Parameters.AddWithValue("@sourceContext", request.SourceContext is null ? DBNull.Value : JsonSerializer.Serialize(request.SourceContext.Value));
        cmd.Parameters.AddWithValue("@rawMarkdown", request.RawMarkdown);
        cmd.Parameters.AddWithValue("@sourceContentHash", sourceContentHash);
        cmd.Parameters.AddWithValue("@segmenterVersion", SegmenterVersion);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var turn = ReadTurn(reader);
        await reader.DisposeAsync();

        foreach (var segment in segments)
        {
            await using var segmentCmd = conn.CreateCommand();
            segmentCmd.CommandText = $"""
                INSERT INTO collaboration_segments (
                    turn_id, sequence_number, segment_hash, segment_type, raw_markdown,
                    text, heading_level, code_language
                ) VALUES (
                    @turnId, @sequenceNumber, @segmentHash, @segmentType, @rawMarkdown,
                    @text, @headingLevel, @codeLanguage
                )
                RETURNING {SegmentColumns}
                """;
            segmentCmd.Parameters.AddWithValue("@turnId", turn.Id);
            segmentCmd.Parameters.AddWithValue("@sequenceNumber", segment.SequenceNumber);
            segmentCmd.Parameters.AddWithValue("@segmentHash", segment.SegmentHash);
            segmentCmd.Parameters.AddWithValue("@segmentType", segment.SegmentType.ToDbValue());
            segmentCmd.Parameters.AddWithValue("@rawMarkdown", segment.RawMarkdown);
            segmentCmd.Parameters.AddWithValue("@text", NullIfWhiteSpace(segment.Text));
            segmentCmd.Parameters.AddWithValue("@headingLevel", (object?)segment.HeadingLevel ?? DBNull.Value);
            segmentCmd.Parameters.AddWithValue("@codeLanguage", NullIfWhiteSpace(segment.CodeLanguage));
            await using var segmentReader = await segmentCmd.ExecuteReaderAsync();
            await segmentReader.ReadAsync();
            turn.Segments.Add(ReadSegment(segmentReader));
        }

        return turn;
    }

    private static async Task<CollaborationSession?> GetSessionHeaderAsync(SqliteConnection conn, string projectId, long sessionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SessionColumns} FROM collaboration_sessions WHERE id = @id AND project_id = @projectId";
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@projectId", projectId.Trim());
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSession(reader) : null;
    }

    private static async Task EnsureSessionExistsAsync(SqliteConnection conn, string projectId, long sessionId)
    {
        if (await GetSessionHeaderAsync(conn, projectId, sessionId) is null)
            throw new KeyNotFoundException($"Collaboration session {sessionId} was not found in project '{projectId}'.");
    }

    private static async Task<List<CollaborationTurn>> LoadTurnsAsync(SqliteConnection conn, long sessionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {TurnColumns} FROM collaboration_turns WHERE session_id = @sessionId ORDER BY turn_order, id";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        var turns = new List<CollaborationTurn>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            turns.Add(ReadTurn(reader));
        await reader.DisposeAsync();

        foreach (var turn in turns)
            turn.Segments = await LoadSegmentsAsync(conn, turn.Id);
        return turns;
    }

    private static async Task<List<CollaborationSegment>> LoadSegmentsAsync(SqliteConnection conn, long turnId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SegmentColumns} FROM collaboration_segments WHERE turn_id = @turnId ORDER BY sequence_number, id";
        cmd.Parameters.AddWithValue("@turnId", turnId);
        var segments = new List<CollaborationSegment>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            segments.Add(ReadSegment(reader));
        return segments;
    }

    private static async Task<List<CollaborationAnnotation>> LoadAnnotationsAsync(SqliteConnection conn, long sessionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {AnnotationColumns} FROM collaboration_annotations WHERE session_id = @sessionId ORDER BY updated_at DESC, id DESC";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        var annotations = new List<CollaborationAnnotation>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            annotations.Add(ReadAnnotation(reader));
        return annotations;
    }

    private static async Task<List<CollaborationResponseDraft>> LoadDraftsAsync(SqliteConnection conn, long sessionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {DraftColumns} FROM collaboration_response_drafts WHERE session_id = @sessionId ORDER BY updated_at DESC, id DESC";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        var drafts = new List<CollaborationResponseDraft>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            drafts.Add(ReadDraft(reader));
        return drafts;
    }

    private static async Task<CollaborationSegment?> GetSegmentAsync(SqliteConnection conn, long sessionId, long turnId, long segmentId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.{SegmentColumns.Replace(", ", ", s.")}
            FROM collaboration_segments s
            JOIN collaboration_turns t ON t.id = s.turn_id
            WHERE s.id = @segmentId
              AND s.turn_id = @turnId
              AND t.session_id = @sessionId
            """;
        cmd.Parameters.AddWithValue("@segmentId", segmentId);
        cmd.Parameters.AddWithValue("@turnId", turnId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSegment(reader) : null;
    }

    private static async Task<bool> TurnBelongsToSessionAsync(SqliteConnection conn, long sessionId, long turnId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM collaboration_turns WHERE id = @turnId AND session_id = @sessionId";
        cmd.Parameters.AddWithValue("@turnId", turnId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> AnnotationExistsAsync(SqliteConnection conn, long sessionId, long annotationId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM collaboration_annotations WHERE id = @annotationId AND session_id = @sessionId";
        cmd.Parameters.AddWithValue("@annotationId", annotationId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> DraftExistsAsync(SqliteConnection conn, long sessionId, long draftId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM collaboration_response_drafts WHERE id = @draftId AND session_id = @sessionId";
        cmd.Parameters.AddWithValue("@draftId", draftId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task TouchSessionAsync(SqliteConnection conn, long sessionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE collaboration_sessions SET updated_at = datetime('now') WHERE id = @sessionId";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddSessionParameters(SqliteCommand cmd, CreateCollaborationSessionRequestModel request)
    {
        cmd.Parameters.AddWithValue("@projectId", request.ProjectId.Trim());
        cmd.Parameters.AddWithValue("@taskId", (object?)request.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@messageId", (object?)request.MessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@agentStreamEntryId", (object?)request.AgentStreamEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@piRunId", NullIfWhiteSpace(request.PiRunId));
        cmd.Parameters.AddWithValue("@piSessionId", NullIfWhiteSpace(request.PiSessionId));
        cmd.Parameters.AddWithValue("@desktopOperatorSessionId", NullIfWhiteSpace(request.DesktopOperatorSessionId));
        cmd.Parameters.AddWithValue("@title", NullIfWhiteSpace(request.Title));
        cmd.Parameters.AddWithValue("@createdBy", NullIfWhiteSpace(request.CreatedBy));
    }

    private static CollaborationSession ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ProjectId = reader.GetString(1),
        TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        MessageId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        AgentStreamEntryId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        PiRunId = reader.IsDBNull(5) ? null : reader.GetString(5),
        PiSessionId = reader.IsDBNull(6) ? null : reader.GetString(6),
        DesktopOperatorSessionId = reader.IsDBNull(7) ? null : reader.GetString(7),
        Title = reader.IsDBNull(8) ? null : reader.GetString(8),
        Status = EnumExtensions.ParseCollaborationSessionStatus(reader.GetString(9)),
        CreatedBy = reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAt = DateTime.Parse(reader.GetString(11)),
        UpdatedAt = DateTime.Parse(reader.GetString(12))
    };

    private static CollaborationTurn ReadTurn(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SessionId = reader.GetInt64(1),
        TurnOrder = reader.GetInt32(2),
        Role = reader.IsDBNull(3) ? null : reader.GetString(3),
        SourceKind = reader.IsDBNull(4) ? null : reader.GetString(4),
        SourceRef = reader.IsDBNull(5) ? null : reader.GetString(5),
        SourceLabel = reader.IsDBNull(6) ? null : reader.GetString(6),
        SourceUri = reader.IsDBNull(7) ? null : reader.GetString(7),
        SourceContext = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(8)).Clone(),
        RawMarkdown = reader.GetString(9),
        SourceContentHash = reader.GetString(10),
        SegmenterVersion = reader.GetString(11),
        CreatedAt = DateTime.Parse(reader.GetString(12))
    };

    private static CollaborationSegment ReadSegment(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        TurnId = reader.GetInt64(1),
        SequenceNumber = reader.GetInt32(2),
        SegmentHash = reader.GetString(3),
        SegmentType = EnumExtensions.ParseCollaborationSegmentType(reader.GetString(4)),
        RawMarkdown = reader.GetString(5),
        Text = reader.IsDBNull(6) ? null : reader.GetString(6),
        HeadingLevel = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        CodeLanguage = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedAt = DateTime.Parse(reader.GetString(9))
    };

    private static CollaborationAnnotation ReadAnnotation(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SessionId = reader.GetInt64(1),
        TurnId = reader.GetInt64(2),
        SegmentId = reader.GetInt64(3),
        SegmentHash = reader.GetString(4),
        AnnotationType = EnumExtensions.ParseCollaborationAnnotationType(reader.GetString(5)),
        Body = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
        UpdatedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
        Revision = reader.GetInt32(9),
        CreatedAt = DateTime.Parse(reader.GetString(10)),
        UpdatedAt = DateTime.Parse(reader.GetString(11))
    };

    private static CollaborationResponseDraft ReadDraft(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SessionId = reader.GetInt64(1),
        TurnId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
        Content = reader.GetString(3),
        CreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
        UpdatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
        Revision = reader.GetInt32(6),
        CreatedAt = DateTime.Parse(reader.GetString(7)),
        UpdatedAt = DateTime.Parse(reader.GetString(8))
    };

    private static void ValidateSessionRequest(CreateCollaborationSessionRequestModel request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId))
            throw new ArgumentException("Project id is required.", nameof(request));
        if (request.InitialTurn is null)
            throw new ArgumentException("Initial turn is required.", nameof(request));
        ValidateTurnRequest(request.InitialTurn);
    }

    private static void ValidateTurnRequest(CreateCollaborationTurnRequestModel request)
    {
        if (string.IsNullOrWhiteSpace(request.RawMarkdown))
            throw new ArgumentException("Raw markdown is required.", nameof(request));
    }

    private static void ValidateDraftContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Draft content is required.", nameof(content));
    }

    private static object NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string HashText(string text) => MarkdownBlockSegmenter.ComputeSHA256(text);
}
