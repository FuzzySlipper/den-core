using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using Microsoft.Extensions.Logging;

namespace DenCore.Service.Routes;

public static class MessageRoutes
{
    public static void MapMessageRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/messages");

        group.MapPost("/", async (IMessageRepository repo, IDispatchDetectionService detection,
            ILoggerFactory loggers, string projectId, SendMessageRequest req) =>
        {
            try
            {
                var msg = new Message
                {
                    ProjectId = projectId,
                    Sender = req.Sender,
                    Content = req.Content,
                    TaskId = req.TaskId,
                    ThreadId = req.ThreadId,
                    Intent = req.Intent,
                    Metadata = NormalizeMetadata(req.Metadata)
                };
                var created = await repo.CreateAsync(msg);
                try
                {
                    await detection.OnMessageCreatedAsync(created);
                }
                catch (Exception ex)
                {
                    loggers.CreateLogger("DispatchDetection")
                        .LogError(ex, "Dispatch detection failed for message {MessageId}", created.Id);
                }
                return Results.Created($"/api/projects/{projectId}/messages/{created.Id}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/{messageId:int}", async (IMessageRepository repo, string projectId, int messageId) =>
        {
            var message = await repo.GetByIdAsync(messageId);
            if (message is null || message.ProjectId != projectId)
                return Results.NotFound(new { error = $"Message {messageId} not found" });
            return Results.Ok(message);
        });

        group.MapGet("/", async (IMessageRepository repo, string projectId,
            int? taskId, string? since, string? unreadFor, int? limit, string? intent) =>
        {
            MessageIntent? parsedIntent = null;
            if (!string.IsNullOrWhiteSpace(intent))
            {
                try
                {
                    parsedIntent = EnumExtensions.ParseMessageIntent(intent);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            DateTime? sinceDate = since is not null ? DateTime.Parse(since) : null;
            var messages = await repo.GetMessagesAsync(projectId, taskId, sinceDate, unreadFor, limit ?? 20, parsedIntent);
            return Results.Ok(messages);
        });

        group.MapGet("/feed", async (IMessageRepository repo, string projectId, int? limit, string? intent) =>
        {
            MessageIntent? parsedIntent = null;
            if (!string.IsNullOrWhiteSpace(intent))
            {
                try
                {
                    parsedIntent = EnumExtensions.ParseMessageIntent(intent);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            var feed = await repo.GetFeedAsync(projectId, limit ?? 20, parsedIntent);
            return Results.Ok(feed);
        });

        group.MapGet("/thread/{threadId:int}", async (IMessageRepository repo, string projectId, int threadId) =>
        {
            try
            {
                var thread = await repo.GetThreadAsync(threadId);
                if (thread.Root.ProjectId != projectId)
                    return Results.NotFound(new { error = $"Message {threadId} not found" });
                return Results.Ok(thread);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Message {threadId} not found" });
            }
        });

        app.MapPost("/api/messages/mark-read", async (IMessageRepository repo, MarkReadRequest req) =>
        {
            var count = await repo.MarkReadAsync(req.Agent, req.MessageIds);
            return Results.Ok(new { marked = count });
        });

        // Notification feed endpoints
        app.MapGet("/api/user-notifications", async (IMessageRepository repo,
            string? projectId, int? taskId, string? sender,
            string? metadataType, string? urgency, bool? isRead,
            string? readFor, int? limit, int? offset) =>
        {
            if (isRead is not null && readFor is null)
                return Results.BadRequest(new { error = "readFor is required when isRead is specified" });

            var notifications = await repo.GetNotificationFeedAsync(
                projectId, taskId, sender, metadataType, urgency,
                isRead, readFor, limit ?? 20, offset ?? 0);
            return Results.Ok(notifications);
        });

        var notificationGroup = app.MapGroup("/api/projects/{projectId}/user-notifications");

        notificationGroup.MapGet("/", async (IMessageRepository repo, string projectId,
            int? taskId, string? sender, string? metadataType, string? urgency,
            bool? isRead, string? readFor, int? limit, int? offset) =>
        {
            if (isRead is not null && readFor is null)
                return Results.BadRequest(new { error = "readFor is required when isRead is specified" });

            var notifications = await repo.GetNotificationFeedAsync(
                projectId, taskId, sender, metadataType, urgency,
                isRead, readFor, limit ?? 20, offset ?? 0);
            return Results.Ok(notifications);
        });

        app.MapPost("/api/user-notifications/mark-read", async (IMessageRepository repo, MarkNotificationsReadRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Agent))
                return Results.BadRequest(new { error = "agent is required" });

            var hasIds = req.NotificationIds is not null && req.NotificationIds.Length > 0;
            var isMarkAll = req.MarkAll == true;

            if (hasIds && isMarkAll)
                return Results.BadRequest(new { error = "Cannot specify both notification_ids and mark_all" });

            if (isMarkAll)
            {
                if (string.IsNullOrWhiteSpace(req.Scope?.ProjectId))
                    return Results.BadRequest(new { error = "scope.project_id is required when mark_all is true" });

                var count = await repo.MarkAllNotificationsReadAsync(req.Agent, req.Scope.ProjectId, req.Scope.TaskId);
                return Results.Ok(new { marked = count });
            }

            if (hasIds)
            {
                var count = await repo.MarkNotificationsReadAsync(req.Agent, req.NotificationIds);
                return Results.Ok(new { marked = count });
            }

            return Results.BadRequest(new { error = "Must provide either notification_ids or mark_all with scope" });
        });
    }

    private static JsonElement? NormalizeMetadata(JsonElement? metadata)
    {
        if (metadata is null)
            return null;

        if (metadata.Value.ValueKind == JsonValueKind.String)
        {
            var str = metadata.Value.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;
            return JsonSerializer.Deserialize<JsonElement>(str);
        }

        return metadata;
    }
}

public record SendMessageRequest(
    string Sender,
    string Content,
    int? TaskId = null,
    int? ThreadId = null,
    JsonElement? Metadata = null,
    MessageIntent? Intent = null);

public record MarkReadRequest(string Agent, int[] MessageIds);

public record MarkNotificationsReadRequest(
    string Agent,
    int[]? NotificationIds = null,
    bool? MarkAll = null,
    MarkAllScope? Scope = null);

public record MarkAllScope(string ProjectId, int? TaskId = null);
