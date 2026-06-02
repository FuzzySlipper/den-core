using System.ComponentModel;
using DenCore.Mcp;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

[McpServerToolType]
public sealed class SpaceTools
{
    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("core")]
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

    [McpToolProfile("admin-current", "curator", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("core")]
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

    [McpToolProfile("admin-current", "curator", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "get_space"), Description("Get a space by ID with summary stats (task counts by status, unread messages).")]
    public static async Task<string> GetSpace(
        IProjectRepository repo,
        [Description("Space ID.")] string space_id,
        [Description("Your agent identity, for unread message count.")] string? agent = null)
    {
        var stats = await repo.GetWithStatsAsync(space_id, agent);
        return JsonSerializer.Serialize(stats, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "curator", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "update_space_visibility"), Description("Update a space's visibility (normal, hidden, archived). The preferred green path for removing spaces from normal lists without data loss. Archived spaces are preserved but excluded from default list views.")]
    public static async Task<string> UpdateSpaceVisibility(
        IProjectRepository repo,
        [Description("Space ID.")] string space_id,
        [Description("New visibility value: normal, hidden, archived.")] string visibility)
    {
        var validVisibilities = new[] { "normal", "hidden", "archived" };
        if (!validVisibilities.Contains(visibility))
            return ConciseResponse.InvalidVisibility(visibility);

        var existing = await repo.GetByIdAsync(space_id)
            ?? throw new KeyNotFoundException($"Space '{space_id}' not found");

        var oldVisibility = existing.Visibility;
        var updated = await repo.UpdateVisibilityAsync(space_id, visibility);
        return ConciseResponse.UpdatedSpaceVisibility(updated, oldVisibility);
    }

    [McpToolProfile("admin-current", "curator", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "archive_space"), Description("Convenience method to archive a space (set visibility to 'archived'). Preserves all data but removes space from default listing. Reversible via update_space_visibility.")]
    public static async Task<string> ArchiveSpace(
        IProjectRepository repo,
        [Description("Space ID to archive.")] string space_id)
    {
        var existing = await repo.GetByIdAsync(space_id)
            ?? throw new KeyNotFoundException($"Space '{space_id}' not found");

        var updated = await repo.UpdateVisibilityAsync(space_id, "archived");
        return ConciseResponse.ArchivedSpace(updated);
    }

    [McpToolProfile("admin-current")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "delete_space"), Description("ADMIN: Permanently delete a space and all its data. Refuses to delete system, personal, and core project spaces. Reports dependent records before deletion. Use archive_space or update_space_visibility as the non-destructive alternative.")]
    public static async Task<string> DeleteSpace(
        IProjectRepository repo,
        [Description("Space ID to delete.")] string space_id,
        [Description("Bypass protection for system/personal/core project spaces. Use with extreme caution.")] bool force = false)
    {
        var existing = await repo.GetByIdAsync(space_id);
        if (existing is null)
            throw new KeyNotFoundException($"Space '{space_id}' not found");

        // Guard: protect system, personal, and core project spaces
        var protectedKinds = new[] { "system", "personal" };
        var protectedIds = new[] { "den", "den-core", "core" };

        if (!force && protectedKinds.Contains(existing.Kind))
            return ConciseResponse.SpaceDeleteBlocked(space_id,
                $"cannot delete {existing.Kind} kind space (protected)");

        if (!force && protectedIds.Contains(space_id))
            return ConciseResponse.SpaceDeleteBlocked(space_id,
                "core project space is protected from deletion");

        // Report dependent records
        var dependentCounts = await repo.GetDependentRecordCountsAsync(space_id);
        if (dependentCounts.Count > 0 && !force)
            return ConciseResponse.SpaceDeleteBlocked(space_id,
                "space has dependent records; archive or hide instead, or use force=true to delete everything",
                dependentCounts);

        // Proceed with deletion
        await repo.DeleteSpaceAsync(space_id);
        return ConciseResponse.DeletedSpace(existing);
    }
}
