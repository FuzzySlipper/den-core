using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Realtime;

namespace DenMcp.Server.Routes;

public static class SubagentRunRoutes
{
    private const string SubagentRunSchema = "den_subagent_run";
    private const int SubagentRunSchemaVersion = 1;

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static void MapSubagentRunRoutes(this WebApplication app)
    {
        app.MapGet("/api/subagent-runs", async (
            ISubagentRunService service,
            string? projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var runs = await service.ListAsync(new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = state,
                Limit = limit ?? 8
            });

            return Results.Ok(runs);
        });

        app.MapGet("/api/subagent-runs/events", StreamSubagentRunEvents);

        app.MapGet("/api/subagent-runs/{runId}", async (
            ISubagentRunService service,
            string runId,
            string? projectId,
            int? taskId) =>
        {
            var detail = await service.GetAsync(runId, new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId
            });

            return detail is not null
                ? Results.Ok(detail)
                : Results.NotFound(new { error = $"Sub-agent run {runId} not found" });
        });

        app.MapPost("/api/subagent-runs/{runId}/control", async (
            ISubagentRunService service,
            IAgentStreamOpsService ops,
            AgentStreamRealtimeHub realtime,
            string runId,
            SubagentRunControlRequest req,
            string? projectId,
            int? taskId) =>
        {
            return await RequestSubagentRunControlAsync(service, ops, realtime, runId, req, projectId, taskId);
        });

        app.MapGet("/api/projects/{projectId}/subagent-runs", async (
            ISubagentRunService service,
            string projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var runs = await service.ListAsync(new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = state,
                Limit = limit ?? 8
            });

            return Results.Ok(runs);
        });

        app.MapGet("/api/projects/{projectId}/subagent-runs/events", async (
            HttpContext context,
            AgentStreamRealtimeHub realtime,
            string projectId,
            int? taskId) =>
        {
            await StreamSubagentRunEvents(context, realtime, projectId, taskId);
        });

        app.MapGet("/api/projects/{projectId}/subagent-runs/{runId}", async (
            ISubagentRunService service,
            string projectId,
            string runId,
            int? taskId) =>
        {
            var detail = await service.GetAsync(runId, new SubagentRunListOptions
            {
                ProjectId = projectId,
                TaskId = taskId
            });

            return detail is not null
                ? Results.Ok(detail)
                : Results.NotFound(new { error = $"Sub-agent run {runId} not found" });
        });

        app.MapPost("/api/projects/{projectId}/subagent-runs/{runId}/control", async (
            ISubagentRunService service,
            IAgentStreamOpsService ops,
            AgentStreamRealtimeHub realtime,
            string projectId,
            string runId,
            SubagentRunControlRequest req,
            int? taskId) =>
        {
            return await RequestSubagentRunControlAsync(service, ops, realtime, runId, req, projectId, taskId);
        });
    }

    private static async Task<IResult> RequestSubagentRunControlAsync(
        ISubagentRunService service,
        IAgentStreamOpsService ops,
        AgentStreamRealtimeHub realtime,
        string runId,
        SubagentRunControlRequest req,
        string? projectId,
        int? taskId)
    {
        var action = NormalizeAction(req.Action);
        if (action is null)
            return Results.BadRequest(new { error = "Control action must be 'abort' or 'rerun'." });

        var detail = await service.GetAsync(runId, new SubagentRunListOptions
        {
            ProjectId = projectId,
            TaskId = taskId
        });
        if (detail is null)
            return Results.NotFound(new { error = $"Sub-agent run {runId} not found" });

        var summary = detail.Summary;
        if (action == "abort" && !IsAbortable(summary.State))
            return Results.Conflict(new { error = $"Sub-agent run {runId} is {summary.State}; only active runs can be aborted." });

        if (action == "rerun" && IsAbortable(summary.State))
            return Results.Conflict(new { error = $"Sub-agent run {runId} is {summary.State}; abort it before requesting a rerun." });

        var requestedBy = string.IsNullOrWhiteSpace(req.RequestedBy) ? "web-ui" : req.RequestedBy.Trim();
        var eventType = action == "abort" ? "subagent_abort_requested" : "subagent_rerun_requested";
        var entry = await ops.AppendOpsAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = summary.ProjectId ?? projectId,
            TaskId = summary.TaskId ?? taskId,
            Sender = requestedBy,
            RecipientAgent = summary.Latest.Sender,
            RecipientInstanceId = summary.Latest.SenderInstanceId,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = action == "abort"
                ? $"Abort requested for {summary.Role ?? "sub-agent"} run {runId}."
                : $"Rerun requested for {summary.Role ?? "sub-agent"} run {runId}.",
            Metadata = JsonSerializer.SerializeToElement(new
            {
                schema = SubagentRunSchema,
                schema_version = SubagentRunSchemaVersion,
                run_id = runId,
                role = summary.Role,
                task_id = summary.TaskId ?? taskId,
                backend = summary.Backend,
                model = summary.Model,
                action,
                requested_by = requestedBy,
                reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
                source_state = summary.State,
                requested_at = DateTime.UtcNow
            }),
            DedupKey = $"subagent-control:{summary.ProjectId ?? projectId ?? "_global"}:{runId}:{action}"
        });
        realtime.Publish(entry);

        return Results.Created($"/api/agent-stream/{entry.Id}", entry);
    }

    private static string? NormalizeAction(string? action)
    {
        var normalized = action?.Trim().ToLowerInvariant();
        return normalized is "abort" or "rerun" ? normalized : null;
    }

    private static bool IsAbortable(string state) =>
        state is "running" or "retrying" or "aborting";

    private static async Task StreamSubagentRunEvents(
        HttpContext context,
        AgentStreamRealtimeHub realtime,
        string? projectId,
        int? taskId)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        await context.Response.StartAsync(context.RequestAborted);
        await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        try
        {
            await foreach (var entry in realtime.SubscribeAsync(
                new AgentStreamRealtimeFilter(projectId, taskId, EventTypePrefix: "subagent_"),
                context.RequestAborted))
            {
                await WriteSseEventAsync(context.Response, "subagent_run_updated", entry, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected.
        }
    }

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventName,
        AgentStreamEntry entry,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(entry, SseJsonOptions);
        await response.WriteAsync($"id: {entry.Id}\n", cancellationToken);
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await response.WriteAsync($"data: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

public sealed record SubagentRunControlRequest(
    string? Action,
    string? RequestedBy = null,
    string? Reason = null);
