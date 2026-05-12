using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class SpaceTools
{
    [McpServerTool(Name = "create_space"), Description("Create a new space. Can be any kind (project, personal, assistant, knowledge_base, system).")]
    public static async Task<string> CreateSpace(
        IProjectRepository repo,
        [Description("Unique space ID slug.")] string id,
        [Description("Human-readable display name.")] string name,
        [Description("Space kind: project, personal, assistant, knowledge_base, system. Defaults to project.")] string? kind = null,
        [Description("Visibility: normal, hidden, archived. Defaults to normal.")] string? visibility = null,
        [Description("Optional owner identifier.")] string? owner = null,
        [Description("Absolute path to the project root on disk (meaningful mainly for project kind).")] string? root_path = null,
        [Description("Short description of the space.")] string? description = null,
        [Description("Optional JSON settings string.")] string? settings_json = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var project = await repo.CreateAsync(new Project
        {
            Id = id,
            Name = name,
            Kind = kind ?? "project",
            Visibility = visibility ?? "normal",
            Owner = owner,
            RootPath = root_path,
            Description = description,
            SettingsJson = settings_json
        });
        return verbose
            ? JsonSerializer.Serialize(project, JsonOpts.Default)
            : ConciseResponse.CreatedSpace(project);
    }

    [McpServerTool(Name = "list_spaces"), Description("List spaces with optional kind and visibility filters.")]
    public static async Task<string> ListSpaces(
        IProjectRepository repo,
        [Description("Filter by space kind (project, personal, assistant, knowledge_base, system). Omit to include all kinds.")] string? kind = null,
        [Description("Include hidden spaces.")] bool include_hidden = false,
        [Description("Include archived spaces.")] bool include_archived = false)
    {
        var spaces = await repo.ListAsync(kind: kind, includeHidden: include_hidden, includeArchived: include_archived);
        return JsonSerializer.Serialize(spaces, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_space"), Description("Get a space by ID with summary stats (task counts by status, unread messages).")]
    public static async Task<string> GetSpace(
        IProjectRepository repo,
        [Description("Space ID.")] string space_id,
        [Description("Your agent identity, for unread message count.")] string? agent = null)
    {
        var stats = await repo.GetWithStatsAsync(space_id, agent);
        return JsonSerializer.Serialize(stats, JsonOpts.Default);
    }
}
