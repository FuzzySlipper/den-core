using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class AgentGuidanceTools
{
    [McpServerTool(Name = "get_agent_guidance"), Description("Resolve the Den-native agent guidance packet for a project or space, combining _global and space-scoped guidance entries in deterministic order.")]
    public static async Task<string> GetAgentGuidance(
        IAgentGuidanceRepository repo,
        [Description("Project or space ID to resolve guidance for.")] string project_id)
    {
        var guidance = await repo.ResolveAsync(project_id);
        return JsonSerializer.Serialize(guidance, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_agent_guidance_entries"), Description("List first-class agent guidance entries for a project or space. Use include_global to also show inherited _global entries.")]
    public static async Task<string> ListAgentGuidanceEntries(
        IAgentGuidanceRepository repo,
        [Description("Project or space ID whose entries should be listed.")] string project_id,
        [Description("Whether to include _global entries inherited by the project.")] bool include_global = false)
    {
        var entries = await repo.ListAsync(project_id, include_global);
        return JsonSerializer.Serialize(entries, JsonOpts.Default);
    }

    [McpServerTool(Name = "add_agent_guidance_entry"), Description("Add or update a first-class agent guidance entry that points at an existing Den document.")]
    public static async Task<string> AddAgentGuidanceEntry(
        IAgentGuidanceRepository repo,
        IDocumentRepository documents,
        [Description("Guidance scope project or space ID. Use '_global' for guidance inherited by all projects and spaces.")] string project_id,
        [Description("Document slug to include in resolved guidance.")] string document_slug,
        [Description("Project ID where the referenced document lives. Defaults to project_id.")] string? document_project_id = null,
        [Description("Importance: required or important. Default: important.")] string importance = "important",
        [Description("Optional comma-separated audience labels, e.g. 'pi,conductor'.")] string? audience = null,
        [Description("Deterministic sort order within the scope. Lower comes first. Default: 0.")] int sort_order = 0,
        [Description("Optional notes for why this document is included.")] string? notes = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var docProjectId = document_project_id ?? project_id;
        var doc = await documents.GetAsync(docProjectId, document_slug);
        if (doc is null)
            return JsonSerializer.Serialize(new { error = $"Document '{docProjectId}/{document_slug}' not found." }, JsonOpts.Default);

        var parsedAudience = SplitAudience(audience);
        var entry = await repo.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = project_id,
            DocumentProjectId = docProjectId,
            DocumentSlug = document_slug,
            Importance = EnumExtensions.ParseAgentGuidanceImportance(importance),
            Audience = parsedAudience,
            SortOrder = sort_order,
            Notes = notes
        });
        return verbose
            ? JsonSerializer.Serialize(entry, JsonOpts.Default)
            : ConciseResponse.AddedAgentGuidanceEntry(entry);
    }

    [McpServerTool(Name = "delete_agent_guidance_entry"), Description("Delete a first-class agent guidance entry by project or space scope and ID.")]
    public static async Task<string> DeleteAgentGuidanceEntry(
        IAgentGuidanceRepository repo,
        [Description("Guidance scope project or space ID that owns the entry. Use '_global' for global guidance entries.")] string project_id,
        [Description("Agent guidance entry ID.")] int entry_id)
    {
        var deleted = await repo.DeleteAsync(entry_id, project_id);
        return deleted
            ? JsonSerializer.Serialize(new { message = $"Agent guidance entry {entry_id} deleted from project '{project_id}'." }, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Agent guidance entry {entry_id} not found in project '{project_id}'." }, JsonOpts.Default);
    }

    private static List<string>? SplitAudience(string? audience)
    {
        var values = audience?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values is { Length: > 0 } ? values.ToList() : null;
    }
}
