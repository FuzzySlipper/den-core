using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging;

namespace DenMcp.Server.Routes;

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
