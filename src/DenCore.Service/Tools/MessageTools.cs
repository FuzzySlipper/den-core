using System.ComponentModel;
using DenCore.Mcp;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

[McpServerToolType]
public sealed class MessageTools
{
    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("messaging")]
    [McpServerTool(Name = "send_message"), Description("Send a message in a project. Can be project-level, attached to a task, or a reply in a thread.")]
    public static async Task<string> SendMessage(
        IMessageRepository repo,
        IDispatchDetectionService detection,
        ILogger<MessageTools> logger,
        [Description("Project ID.")] string project_id,
        [Description("Your agent identity, e.g. 'pi' or another manual agent identity.")] string sender,
        [Description("Message body (markdown).")] string content,
        [Description("Attach to a task by ID.")] int? task_id = null,
        [Description("Reply to an existing message (forms a thread).")] int? thread_id = null,
        [Description("Optional JSON metadata object or JSON-encoded string, e.g. {\"type\":\"review_request\"}.")] JsonElement? metadata = null,
        [Description("Optional canonical intent, e.g. review_feedback or handoff.")] string? intent = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var (parsedIntent, rawIntent) = ParseIntent(intent);
        var normalizedMetadata = NormalizeMetadata(metadata);

        if (rawIntent is not null)
        {
            normalizedMetadata = MergeRequestedIntentIntoMetadata(normalizedMetadata, rawIntent);
        }

        var msg = new Message
        {
            ProjectId = project_id,
            Sender = sender,
            Content = content,
            TaskId = task_id,
            ThreadId = thread_id,
            Intent = parsedIntent,
            Metadata = normalizedMetadata
        };

        var created = await repo.CreateAsync(msg);
        try
        {
            await detection.OnMessageCreatedAsync(created);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dispatch detection failed for message {MessageId}", created.Id);
        }
        return verbose
            ? JsonSerializer.Serialize(created, JsonOpts.Default)
            : ConciseResponse.SentMessage(created);
    }

    [McpToolProfile("admin-current", "planner", "runner")]
    [McpToolBundle("messaging")]
    [McpServerTool(Name = "send_user_notification"), Description(
        "Send a user-facing notification message in a project. " +
        "Use this when you have noteworthy information for the user that should not require stopping the run or waiting for a final response. " +
        "Examples: server needs redeployment, a long-running task completed, a blocking issue needs user decision. " +
        "Notifications appear prominently in the Den Desktop Messages tab." +
        "Prefer this over send_message when the message is specifically for the user rather than general task tracking.")]
    public static async Task<string> SendUserNotification(
        IMessageRepository repo,
        IDispatchDetectionService detection,
        ILogger<MessageTools> logger,
        [Description("Project ID.")] string project_id,
        [Description("Your agent identity, e.g. 'pi' or another manual agent identity.")] string sender,
        [Description("Notification body (markdown). Keep it concise and actionable.")] string content,
        [Description("Attach to a task by ID.")] int? task_id = null,
        [Description("Optional JSON metadata object or JSON-encoded string.")] JsonElement? metadata = null,
        [Description("Optional urgency hint: low, normal, or high. Defaults to normal.")] string? urgency = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var normalizedUrgency = urgency?.ToLowerInvariant() switch
        {
            "low" => "low",
            "high" => "high",
            _ => "normal"
        };

        var mergedMetadata = MergeUrgencyIntoMetadata(NormalizeMetadata(metadata), normalizedUrgency, sender);

        var msg = new Message
        {
            ProjectId = project_id,
            Sender = sender,
            Content = content,
            TaskId = task_id,
            Intent = MessageIntent.Notification,
            Metadata = mergedMetadata
        };

        var created = await repo.CreateAsync(msg);
        try
        {
            await detection.OnMessageCreatedAsync(created);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dispatch detection failed for notification {MessageId}", created.Id);
        }
        return verbose
            ? JsonSerializer.Serialize(created, JsonOpts.Default)
            : ConciseResponse.SentMessage(created);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("messaging")]
    [McpServerTool(Name = "get_messages"), Description("Get messages in a project, with optional filters. Returns newest first.")]
    public static async Task<string> GetMessages(
        IMessageRepository repo,
        [Description("Project ID.")] string project_id,
        [Description("Filter to messages on a specific task.")] int? task_id = null,
        [Description("ISO datetime — only messages after this time.")] string? since = null,
        [Description("Agent identity — only unread messages for this agent.")] string? unread_for = null,
        [Description("Max messages to return. Default 20, max 100.")] int limit = 20,
        [Description("Optional canonical intent filter.")] string? intent = null)
    {
        DateTime? sinceDate = since is not null ? DateTime.Parse(since) : null;
        var (parsedIntent, _) = ParseIntent(intent);
        var messages = await repo.GetMessagesAsync(project_id, task_id, sinceDate, unread_for, limit, parsedIntent);
        return JsonSerializer.Serialize(messages, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("messaging")]
    [McpServerTool(Name = "get_thread"), Description("Get a complete message thread — the root message plus all replies in chronological order.")]
    public static async Task<string> GetThread(
        IMessageRepository repo,
        [Description("ID of the root message.")] int thread_id)
    {
        var thread = await repo.GetThreadAsync(thread_id);
        return JsonSerializer.Serialize(thread, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("messaging")]
    [McpServerTool(Name = "mark_read"), Description("Mark messages as read for an agent.")]
    public static async Task<string> MarkRead(
        IMessageRepository repo,
        [Description("Your agent identity.")] string agent,
        [Description("Comma-separated message IDs to mark as read.")] string message_ids)
    {
        var ids = message_ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();
        var count = await repo.MarkReadAsync(agent, ids);
        return JsonSerializer.Serialize(new { marked = count }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("messaging")]
    [McpServerTool(Name = "get_user_notifications"), Description(
        "Get the canonical user notification feed. Returns notifications across projects, " +
        "newest first, with optional filters for project, task, sender, metadata type, urgency, and read state. " +
        "Each notification includes id, project_id, task_id, sender, content, metadata, urgency, is_read, and created_at.")]
    public static async Task<string> GetUserNotifications(
        IMessageRepository repo,
        [Description("Filter to a specific project/space ID. Omit for cross-project listing.")] string? project_id = null,
        [Description("Filter to a specific task ID.")] int? task_id = null,
        [Description("Filter by sender/agent identity.")] string? sender = null,
        [Description("Filter by metadata type, e.g. 'agent_work_complete'.")] string? metadata_type = null,
        [Description("Filter by urgency: low, normal, or high.")] string? urgency = null,
        [Description("Filter by read state. Must provide read_for_agent when set.")] bool? is_read = null,
        [Description("Agent identity for read-state derivation. Required when is_read is set.")] string? read_for_agent = null,
        [Description("Max results. Default 20, max 100.")] int limit = 20,
        [Description("Offset for pagination. Default 0.")] int offset = 0)
    {
        var notifications = await repo.GetNotificationFeedAsync(
            project_id, task_id, sender, metadata_type, urgency,
            is_read, read_for_agent, limit, offset);
        return JsonSerializer.Serialize(notifications, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("messaging")]
    [McpServerTool(Name = "mark_notifications_read"), Description(
        "Mark user notifications as read for an agent identity. Supports two modes:\n" +
        "1) Explicit IDs: pass notification_ids (comma-separated).\n" +
        "2) Scoped mark-all: pass mark_all=\"true\" with scope_project_id (required) and optional scope_task_id.\n" +
        "The two modes are mutually exclusive. Only marks messages with intent='notification'.")]
    public static async Task<string> MarkNotificationsRead(
        IMessageRepository repo,
        [Description("Agent identity to mark read for.")] string agent,
        [Description("Comma-separated notification IDs to mark as read.")] string? notification_ids = null,
        [Description("Set to \"true\" to mark all notifications in scope as read.")] string? mark_all = null,
        [Description("Project ID for mark-all scope. Required when mark_all is true.")] string? scope_project_id = null,
        [Description("Optional task ID to narrow mark-all scope to a specific task.")] int? scope_task_id = null)
    {
        var isMarkAll = string.Equals(mark_all, "true", StringComparison.OrdinalIgnoreCase);

        // Parse explicit IDs if provided
        List<int>? parsedIds = null;
        if (!string.IsNullOrWhiteSpace(notification_ids))
        {
            parsedIds = new List<int>();
            foreach (var segment in notification_ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(segment, out var id))
                    return JsonSerializer.Serialize(new { error = $"Invalid notification ID: '{segment}'" }, JsonOpts.Default);
                parsedIds.Add(id);
            }
        }

        var hasIds = parsedIds is not null && parsedIds.Count > 0;

        if (hasIds && isMarkAll)
            return JsonSerializer.Serialize(new { error = "Cannot specify both notification_ids and mark_all" }, JsonOpts.Default);

        if (isMarkAll)
        {
            if (string.IsNullOrWhiteSpace(scope_project_id))
                return JsonSerializer.Serialize(new { error = "scope_project_id is required when mark_all is true" }, JsonOpts.Default);

            var count = await repo.MarkAllNotificationsReadAsync(agent, scope_project_id, scope_task_id);
            return JsonSerializer.Serialize(new { marked = count }, JsonOpts.Default);
        }

        if (hasIds)
        {
            var count = await repo.MarkNotificationsReadAsync(agent, parsedIds!.ToArray());
            return JsonSerializer.Serialize(new { marked = count }, JsonOpts.Default);
        }

        return JsonSerializer.Serialize(new { error = "Must provide either notification_ids or mark_all with scope" }, JsonOpts.Default);
    }

    private static (MessageIntent? canonical, string? raw) ParseIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return (null, null);

        if (EnumExtensions.TryParseMessageIntent(intent, out var canonical))
            return (canonical, null);

        return (null, intent);
    }

    private static JsonElement? MergeRequestedIntentIntoMetadata(JsonElement? metadata, string requestedIntent)
    {
        var obj = new System.Text.Json.Nodes.JsonObject();

        if (metadata.HasValue && metadata.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.Value.EnumerateObject())
            {
                obj[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            }
        }

        obj["requested_intent"] = requestedIntent;

        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
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

    private static JsonElement? MergeUrgencyIntoMetadata(JsonElement? metadata, string urgency, string sender)
    {
        var obj = new System.Text.Json.Nodes.JsonObject();

        if (metadata.HasValue && metadata.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.Value.EnumerateObject())
            {
                obj[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            }
        }

        obj["urgency"] = urgency;
        obj["source_sender"] = sender;

        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
    }
}
