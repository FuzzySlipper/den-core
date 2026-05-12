using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class AgentTools
{
    [McpServerTool(Name = "list_active_agents"), Description(
        "List agents currently active on a project (or all projects). " +
        "Shows who else is working, useful for coordination.")]
    public static async Task<string> ListActiveAgents(
        IAgentSessionRepository repo,
        [Description("Project ID to filter by. Omit to see all active agents.")] string? project_id = null)
    {
        var sessions = await repo.ListActiveAsync(project_id);
        return JsonSerializer.Serialize(sessions, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_agent_instance_bindings"), Description(
        "List agent instance bindings used for role-aware delivery resolution. Defaults to active/degraded bindings.")]
    public static async Task<string> ListAgentInstanceBindings(
        IAgentInstanceBindingRepository repo,
        [Description("Project ID to filter by. Omit to see all projects.")] string? project_id = null,
        [Description("Filter by binding statuses (comma-separated): active,inactive,degraded. Defaults to active,degraded.")] string? status = null,
        [Description("Filter by agent identity.")] string? agent_identity = null,
        [Description("Filter by role.")] string? role = null,
        [Description("Filter by transport kind.")] string? transport_kind = null)
    {
        AgentInstanceBindingStatus[]? statuses = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            try
            {
                statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(EnumExtensions.ParseAgentInstanceBindingStatus)
                    .ToArray();
            }
            catch (ArgumentException ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
            }
        }

        var bindings = await repo.ListAsync(new AgentInstanceBindingListOptions
        {
            ProjectId = project_id,
            AgentIdentity = agent_identity,
            Role = role,
            TransportKind = transport_kind,
            Statuses = statuses ??
            [
                AgentInstanceBindingStatus.Active,
                AgentInstanceBindingStatus.Degraded
            ]
        });

        return JsonSerializer.Serialize(bindings, JsonOpts.Default);
    }
}
