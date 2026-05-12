using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class DispatchTools
{
    [McpServerTool(Name = "list_dispatches"), Description("List dispatch entries with optional filters. Returns newest first.")]
    public static async Task<string> ListDispatches(
        IDispatchRepository repo,
        [Description("Filter by project ID.")] string? project_id = null,
        [Description("Filter by target agent identity.")] string? target_agent = null,
        [Description("Filter by statuses (comma-separated): pending,approved,rejected,completed,expired.")] string? status = null)
    {
        DispatchStatus[]? statuses;
        try
        {
            statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(EnumExtensions.ParseDispatchStatus).ToArray();
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
        var entries = await repo.ListAsync(project_id, target_agent, statuses);
        return JsonSerializer.Serialize(entries, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_dispatch"), Description("Get a dispatch entry by ID with full details including generated prompt.")]
    public static async Task<string> GetDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID.")] int dispatch_id)
    {
        var entry = await repo.GetByIdAsync(dispatch_id);
        return entry is not null
            ? JsonSerializer.Serialize(entry, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Dispatch {dispatch_id} not found" }, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_dispatch_context"), Description("Get the structured handoff context for a dispatch entry. This is the machine-oriented source of truth for targeted wake-ups.")]
    public static async Task<string> GetDispatchContext(
        IDispatchContextService contexts,
        [Description("Dispatch entry ID.")] int dispatch_id)
    {
        var context = await contexts.GetContextAsync(dispatch_id);
        return context is not null
            ? JsonSerializer.Serialize(context, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Dispatch {dispatch_id} not found" }, JsonOpts.Default);
    }

    [McpServerTool(Name = "approve_dispatch"), Description("Approve a pending dispatch entry. The target agent will be able to pick it up.")]
    public static async Task<string> ApproveDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID to approve.")] int dispatch_id,
        [Description("Identity of who is approving (e.g. 'user').")] string decided_by,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var entry = await repo.ApproveAsync(dispatch_id, decided_by);
            return verbose
                ? JsonSerializer.Serialize(entry, JsonOpts.Default)
                : ConciseResponse.ApprovedDispatch(entry);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "reject_dispatch"), Description("Reject a pending dispatch entry.")]
    public static async Task<string> RejectDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID to reject.")] int dispatch_id,
        [Description("Identity of who is rejecting (e.g. 'user').")] string decided_by,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var entry = await repo.RejectAsync(dispatch_id, decided_by);
            return verbose
                ? JsonSerializer.Serialize(entry, JsonOpts.Default)
                : ConciseResponse.RejectedDispatch(entry);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
    }

    [McpServerTool(Name = "complete_dispatch"), Description("Mark an approved dispatch as completed by the agent.")]
    public static async Task<string> CompleteDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID to complete.")] int dispatch_id,
        [Description("Identity of who completed (e.g. the agent identity).")] string? completed_by = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var entry = await repo.CompleteAsync(dispatch_id, completed_by);
            return verbose
                ? JsonSerializer.Serialize(entry, JsonOpts.Default)
                : ConciseResponse.CompletedDispatch(entry);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
    }
}
