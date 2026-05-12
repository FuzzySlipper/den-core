using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class AgentStreamTools
{
    [McpServerTool(Name = "list_agent_stream"), Description("List agent stream entries with optional global or project-scoped filters. Returns newest first.")]
    public static async Task<string> ListAgentStream(
        IAgentStreamRepository repo,
        [Description("Optional project filter. Omit for the global stream.")] string? project_id = null,
        [Description("Optional task filter.")] int? task_id = null,
        [Description("Optional dispatch filter.")] int? dispatch_id = null,
        [Description("Filter by stream kind: ops or message.")] string? stream_kind = null,
        [Description("Optional event type filter, e.g. review_requested or question.")] string? event_type = null,
        [Description("Optional sender filter.")] string? sender = null,
        [Description("Optional sender instance id filter.")] string? sender_instance_id = null,
        [Description("Optional recipient agent filter.")] string? recipient_agent = null,
        [Description("Optional recipient role filter.")] string? recipient_role = null,
        [Description("Optional recipient instance id filter.")] string? recipient_instance_id = null,
        [Description("Optional metadata run_id filter for sub-agent run events.")] string? metadata_run_id = null,
        [Description("Include debug-level sub-agent work events (subagent_work_* or event_visibility=debug). Default false returns summary/attention events only.")] bool include_debug = false,
        [Description("Maximum entries to return. Default 50, max 200.")] int limit = 50)
    {
        AgentStreamKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(stream_kind))
        {
            try
            {
                parsedKind = EnumExtensions.ParseAgentStreamKind(stream_kind);
            }
            catch (ArgumentException ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
            }
        }

        var entries = await repo.ListAsync(new AgentStreamListOptions
        {
            ProjectId = project_id,
            TaskId = task_id,
            DispatchId = dispatch_id,
            StreamKind = parsedKind,
            EventType = event_type,
            Sender = sender,
            SenderInstanceId = sender_instance_id,
            RecipientAgent = recipient_agent,
            RecipientRole = recipient_role,
            RecipientInstanceId = recipient_instance_id,
            MetadataRunId = metadata_run_id,
            IncludeDebug = include_debug,
            Limit = limit
        });

        return JsonSerializer.Serialize(entries, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_agent_stream_entry"), Description("Get a single agent stream entry by id, optionally scoped to a project.")]
    public static async Task<string> GetAgentStreamEntry(
        IAgentStreamRepository repo,
        [Description("Agent stream entry ID.")] int entry_id,
        [Description("Optional project scope check.")] string? project_id = null)
    {
        var entry = await repo.GetByIdAsync(entry_id);
        if (entry is null || (project_id is not null && !string.Equals(entry.ProjectId, project_id, StringComparison.Ordinal)))
            return JsonSerializer.Serialize(new { error = $"Agent stream entry {entry_id} not found" }, JsonOpts.Default);

        return JsonSerializer.Serialize(entry, JsonOpts.Default);
    }

    [McpServerTool(Name = "send_agent_stream_message"), Description(
        "Append a targeted agent-stream message entry for clarification or nudge flows. " +
        "Use this for question, answer, note, or nudge entries; detailed work context should stay in task-thread messages.")]
    public static async Task<string> SendAgentStreamMessage(
        IAgentStreamMessageService service,
        [Description("Sender identity, e.g. user, pi, or another manual agent identity.")] string sender,
        [Description("Message subtype: question, answer, note, or nudge.")] string event_type,
        [Description("Short freeform message body.")] string body,
        [Description("Optional project scope. Required for recipient_role routing unless recipient_instance_id is provided.")] string? project_id = null,
        [Description("Optional task link.")] int? task_id = null,
        [Description("Optional thread link.")] int? thread_id = null,
        [Description("Optional dispatch link.")] int? dispatch_id = null,
        [Description("Optional sender instance id.")] string? sender_instance_id = null,
        [Description("Optional target agent identity.")] string? recipient_agent = null,
        [Description("Optional target role within a project.")] string? recipient_role = null,
        [Description("Optional exact target instance id.")] string? recipient_instance_id = null,
        [Description("Optional delivery mode: record_only, notify, or wake. Defaults to record_only for note and notify otherwise.")] string? delivery_mode = null,
        [Description("Optional JSON metadata object or JSON-encoded string.")] JsonElement? metadata = null,
        [Description("Optional dedup key for retry-safe appends.")] string? dedup_key = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        AgentStreamDeliveryMode? parsedDeliveryMode = null;
        if (!string.IsNullOrWhiteSpace(delivery_mode))
        {
            try
            {
                parsedDeliveryMode = EnumExtensions.ParseAgentStreamDeliveryMode(delivery_mode);
            }
            catch (ArgumentException ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
            }
        }

        JsonElement? parsedMetadata;
        try
        {
            parsedMetadata = NormalizeMetadata(metadata);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid metadata JSON: {ex.Message}" }, JsonOpts.Default);
        }

        try
        {
            var result = await service.CreateAsync(new AgentStreamMessageCreateRequest
            {
                ProjectId = project_id,
                TaskId = task_id,
                ThreadId = thread_id,
                DispatchId = dispatch_id,
                Sender = sender,
                SenderInstanceId = sender_instance_id,
                EventType = event_type,
                RecipientAgent = recipient_agent,
                RecipientRole = recipient_role,
                RecipientInstanceId = recipient_instance_id,
                DeliveryMode = parsedDeliveryMode,
                Body = body,
                Metadata = parsedMetadata,
                DedupKey = dedup_key
            });

            return verbose
                ? JsonSerializer.Serialize(result, JsonOpts.Default)
                : ConciseResponse.SentAgentStreamMessage(result);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
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
