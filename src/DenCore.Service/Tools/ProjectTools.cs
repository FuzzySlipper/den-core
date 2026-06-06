using System.ComponentModel;
using DenCore.Mcp;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

[McpServerToolType]
public sealed class ProjectTools
{
    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "create_project"), Description("Register a new project for task management, messaging, and document storage.")]
    public static async Task<string> CreateProject(
        IProjectRepository repo,
        [Description("Unique project ID slug, e.g. 'my-project'. Typically the directory name.")] string id,
        [Description("Human-readable display name.")] string name,
        [Description("Absolute path to the project root on disk.")] string? root_path = null,
        [Description("Short description of the project.")] string? description = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var project = await repo.CreateAsync(new Project
        {
            Id = id,
            Name = name,
            RootPath = root_path,
            Description = description
        });
        return verbose
            ? JsonSerializer.Serialize(project, JsonOpts.Default)
            : ConciseResponse.CreatedProject(project);
    }

    [McpToolProfile("admin-current", "curator", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor", "worker-scope-auditor")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "list_projects"), Description("List registered projects. Defaults to normal project-kind spaces only, excluding hidden or archived spaces.")]
    public static async Task<string> ListProjects(IProjectRepository repo)
    {
        var projects = await repo.ListAsync(kind: "project", includeHidden: false, includeArchived: false);
        return JsonSerializer.Serialize(projects, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "curator", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor", "worker-scope-auditor")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "get_project"), Description("Get a project by ID with summary stats (task counts by status, unread messages).")]
    public static async Task<string> GetProject(
        IProjectRepository repo,
        [Description("Project ID.")] string project_id,
        [Description("Your agent identity, for unread message count.")] string? agent = null)
    {
        var stats = await repo.GetWithStatsAsync(project_id, agent);
        return JsonSerializer.Serialize(stats, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("core")]
    [McpServerTool(Name = "update_project"), Description("Update mutable metadata fields for an existing project. Only fields explicitly provided (non-null) are changed; all other fields are left as-is. Safe — will not overwrite existing data with blanks.")]
    public static async Task<string> UpdateProject(
        IProjectRepository repo,
        [Description("Project ID to update.")] string project_id,
        [Description("New display name (optional).")] string? name = null,
        [Description("New absolute root path on disk (optional). Set to empty string to clear.")] string? root_path = null,
        [Description("Updated description (optional).")] string? description = null,
        [Description("Updated owner (optional).")] string? owner = null,
        [Description("Updated settings JSON (optional).")] string? settings_json = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var update = new ProjectUpdateRequest
        {
            Name = name,
            RootPath = root_path,
            Description = description,
            Owner = owner,
            SettingsJson = settings_json,
        };
        var updated = await repo.UpdateProjectAsync(project_id, update);
        return verbose
            ? JsonSerializer.Serialize(updated, JsonOpts.Default)
            : ConciseResponse.CreatedProject(updated);
    }
}
