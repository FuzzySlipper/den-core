using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class CollaborationRoutes
{
    public static void MapCollaborationRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/collaboration/sessions");

        group.MapPost("", async (
            ICollaborationRepository repo,
            string projectId,
            CreateCollaborationSessionRequest req) =>
        {
            try
            {
                var created = await repo.CreateSessionAsync(new CreateCollaborationSessionRequestModel
                {
                    ProjectId = projectId.Trim(),
                    TaskId = req.TaskId,
                    MessageId = req.MessageId,
                    AgentStreamEntryId = req.AgentStreamEntryId,
                    PiRunId = TrimToNull(req.PiRunId),
                    PiSessionId = TrimToNull(req.PiSessionId),
                    DesktopOperatorSessionId = TrimToNull(req.DesktopOperatorSessionId),
                    Title = TrimToNull(req.Title),
                    CreatedBy = TrimToNull(req.CreatedBy),
                    InitialTurn = BuildTurn(req.InitialTurn ?? new CreateCollaborationTurnRequest())
                });
                return Results.Created($"/api/projects/{projectId}/collaboration/sessions/{created.Id}", created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.BadRequest(new { error = "Collaboration session references an unknown project, task, message, or agent-stream entry." });
            }
        });

        group.MapGet("", async (
            ICollaborationRepository repo,
            string projectId,
            int? taskId,
            string? status,
            int? limit) =>
        {
            var parsedStatus = ParseSessionStatus(status);
            if (parsedStatus.Invalid)
                return Results.BadRequest(new { error = $"Unknown collaboration session status: {status}" });

            var sessions = await repo.ListSessionsAsync(new CollaborationSessionListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                Status = parsedStatus.Status,
                Limit = limit ?? 50
            });
            return Results.Ok(sessions);
        });

        group.MapGet("/{sessionId:long}", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId) =>
        {
            var session = await repo.GetSessionAsync(projectId, sessionId);
            return session is null ? Results.NotFound(new { error = $"Collaboration session {sessionId} was not found." }) : Results.Ok(session);
        });

        group.MapPost("/{sessionId:long}/turns", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            CreateCollaborationTurnRequest req) =>
        {
            try
            {
                var turn = await repo.AddTurnAsync(projectId, sessionId, BuildTurn(req));
                return Results.Created($"/api/projects/{projectId}/collaboration/sessions/{sessionId}/turns/{turn.Id}", turn);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPost("/{sessionId:long}/turns/{turnId:long}/annotations", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            long turnId,
            CreateCollaborationAnnotationRequest req) =>
        {
            if (req.SegmentId <= 0)
                return Results.BadRequest(new { error = "segment_id is required." });
            try
            {
                var created = await repo.CreateAnnotationAsync(
                    projectId,
                    sessionId,
                    turnId,
                    req.SegmentId,
                    req.AnnotationType,
                    req.Body,
                    req.CreatedBy);
                return Results.Created($"/api/projects/{projectId}/collaboration/sessions/{sessionId}/annotations/{created.Id}", created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPut("/{sessionId:long}/annotations/{annotationId:long}", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            long annotationId,
            UpdateCollaborationAnnotationRequest req) =>
        {
            try
            {
                var updated = await repo.UpdateAnnotationAsync(
                    projectId,
                    sessionId,
                    annotationId,
                    req.ExpectedRevision,
                    req.AnnotationType,
                    req.Body,
                    req.UpdatedBy);
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (CollaborationConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPost("/{sessionId:long}/drafts", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            CreateCollaborationDraftRequest req) =>
        {
            try
            {
                var created = await repo.CreateDraftAsync(projectId, sessionId, req.TurnId, req.Content ?? string.Empty, req.CreatedBy);
                return Results.Created($"/api/projects/{projectId}/collaboration/sessions/{sessionId}/drafts/{created.Id}", created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPut("/{sessionId:long}/drafts/{draftId:long}", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            long draftId,
            UpdateCollaborationDraftRequest req) =>
        {
            try
            {
                var updated = await repo.UpdateDraftAsync(projectId, sessionId, draftId, req.ExpectedRevision, req.Content ?? string.Empty, req.UpdatedBy);
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (CollaborationConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPatch("/{sessionId:long}/status", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            UpdateSessionStatusRequest req) =>
        {
            try
            {
                var parsedExpected = ParseSessionStatus(req.ExpectedStatus);
                if (parsedExpected.Invalid)
                    return Results.BadRequest(new { error = $"Unknown expected collaboration session status: {req.ExpectedStatus}" });

                var parsedNew = ParseSessionStatus(req.Status);
                if (parsedNew.Invalid)
                    return Results.BadRequest(new { error = $"Unknown collaboration session status: {req.Status}" });

                var updated = await repo.UpdateSessionStatusAsync(
                    projectId, sessionId,
                    parsedExpected.Status!.Value,
                    parsedNew.Status!.Value);
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (CollaborationConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapGet("/{sessionId:long}/annotations", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            long? turnId,
            long? segmentId,
            int? limit) =>
        {
            try
            {
                var annotations = await repo.ListAnnotationsAsync(projectId, sessionId, new CollaborationAnnotationListOptions
                {
                    TurnId = turnId,
                    SegmentId = segmentId,
                    Limit = limit ?? 50
                });
                return Results.Ok(annotations);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapDelete("/{sessionId:long}/annotations/{annotationId:long}", async (
            ICollaborationRepository repo,
            string projectId,
            long sessionId,
            long annotationId,
            int expectedRevision) =>
        {
            try
            {
                var deleted = await repo.DeleteAnnotationAsync(projectId, sessionId, annotationId, expectedRevision);
                return Results.Ok(deleted);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (CollaborationConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }

    private static CreateCollaborationTurnRequestModel BuildTurn(CreateCollaborationTurnRequest req) => new()
    {
        Role = TrimToNull(req.Role),
        SourceKind = TrimToNull(req.SourceKind),
        SourceRef = TrimToNull(req.SourceRef),
        SourceLabel = TrimToNull(req.SourceLabel),
        SourceUri = TrimToNull(req.SourceUri),
        SourceContext = req.SourceContext?.Clone(),
        RawMarkdown = req.RawMarkdown ?? string.Empty
    };

    private static (CollaborationSessionStatus? Status, bool Invalid) ParseSessionStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return (null, false);

        try
        {
            return (EnumExtensions.ParseCollaborationSessionStatus(status.Trim()), false);
        }
        catch (ArgumentException)
        {
            return (null, true);
        }
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record CreateCollaborationSessionRequest
{
    public int? TaskId { get; init; }
    public long? MessageId { get; init; }
    public long? AgentStreamEntryId { get; init; }
    public string? PiRunId { get; init; }
    public string? PiSessionId { get; init; }
    public string? DesktopOperatorSessionId { get; init; }
    public string? Title { get; init; }
    public string? CreatedBy { get; init; }
    public CreateCollaborationTurnRequest? InitialTurn { get; init; }
}

public sealed record CreateCollaborationTurnRequest
{
    public string? Role { get; init; }
    public string? SourceKind { get; init; }
    public string? SourceRef { get; init; }
    public string? SourceLabel { get; init; }
    public string? SourceUri { get; init; }
    public JsonElement? SourceContext { get; init; }
    public string? RawMarkdown { get; init; }
}

public sealed record CreateCollaborationAnnotationRequest
{
    public long SegmentId { get; init; }
    public CollaborationAnnotationType AnnotationType { get; init; } = CollaborationAnnotationType.Note;
    public string? Body { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed record UpdateCollaborationAnnotationRequest
{
    public int ExpectedRevision { get; init; }
    public CollaborationAnnotationType AnnotationType { get; init; } = CollaborationAnnotationType.Note;
    public string? Body { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record CreateCollaborationDraftRequest
{
    public long? TurnId { get; init; }
    public string? Content { get; init; }
    public string? CreatedBy { get; init; }
}

public sealed record UpdateCollaborationDraftRequest
{
    public int ExpectedRevision { get; init; }
    public string? Content { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record UpdateSessionStatusRequest
{
    public string? ExpectedStatus { get; init; }
    public string? Status { get; init; }
}
