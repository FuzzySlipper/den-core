using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

/// <summary>
/// Legacy dispatch archive tools. Dispatch is retired per
/// den-communication-surfaces-concept-map. Only read-only list/get
/// remain; mutation tools return an error indicating retirement.
/// </summary>
[McpServerToolType]
public sealed class DispatchTools
{
    [McpServerTool(Name = "list_dispatches"), Description("List dispatch entries with optional filters. Returns newest first. Dispatch is a retired legacy primitive; this tool remains only for archive inspection.")]
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

    [McpServerTool(Name = "get_dispatch"), Description("Get a dispatch entry by ID with full details including generated prompt. Dispatch is a retired legacy primitive; this tool remains only for archive inspection.")]
    public static async Task<string> GetDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID.")] int dispatch_id)
    {
        var entry = await repo.GetByIdAsync(dispatch_id);
        return entry is not null
            ? JsonSerializer.Serialize(entry, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Dispatch {dispatch_id} not found" }, JsonOpts.Default);
    }

    [McpServerTool(Name = "approve_dispatch"), Description("RETIRED: Dispatch approval is no longer supported. Dispatch is a retired legacy primitive per den-communication-surfaces-concept-map.")]
    public static Task<string> ApproveDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID to approve.")] int dispatch_id,
        [Description("Identity of who is approving (e.g. 'user').")] string decided_by,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            error = "Dispatch approval is retired. Dispatch is a legacy primitive per den-communication-surfaces-concept-map."
        }, JsonOpts.Default));
    }

    [McpServerTool(Name = "reject_dispatch"), Description("RETIRED: Dispatch rejection is no longer supported. Dispatch is a retired legacy primitive per den-communication-surfaces-concept-map.")]
    public static Task<string> RejectDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID to reject.")] int dispatch_id,
        [Description("Identity of who is rejecting (e.g. 'user').")] string decided_by,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            error = "Dispatch rejection is retired. Dispatch is a legacy primitive per den-communication-surfaces-concept-map."
        }, JsonOpts.Default));
    }

    [McpServerTool(Name = "complete_dispatch"), Description("RETIRED: Dispatch completion is no longer supported. Dispatch is a retired legacy primitive per den-communication-surfaces-concept-map.")]
    public static Task<string> CompleteDispatch(
        IDispatchRepository repo,
        [Description("Dispatch entry ID to complete.")] int dispatch_id,
        [Description("Identity of who completed (e.g. the agent identity).")] string? completed_by = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            error = "Dispatch completion is retired. Dispatch is a legacy primitive per den-communication-surfaces-concept-map."
        }, JsonOpts.Default));
    }
}
