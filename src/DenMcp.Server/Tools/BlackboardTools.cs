using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class BlackboardTools
{
    [McpServerTool(Name = "store_blackboard_entry"), Description("Create or update a cross-project shared blackboard Markdown entry. Entries are not scoped to a project.")]
    public static async Task<string> StoreBlackboardEntry(
        IBlackboardRepository repo,
        [Description("Unique blackboard slug, e.g. 'agent-handoff-note'.")] string slug,
        [Description("Entry title.")] string title,
        [Description("Markdown entry content.")] string content,
        [Description("JSON array of string tags.")] string? tags = null,
        [Description("Optional idle TTL in seconds. If set, the entry expires when not accessed for this many seconds.")] int? idle_ttl_seconds = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var parsedTags = tags is not null ? JsonSerializer.Deserialize<List<string>>(tags) : null;
        var entry = await repo.UpsertAsync(new BlackboardEntry
        {
            Slug = slug,
            Title = title,
            Content = content,
            Tags = parsedTags,
            IdleTtlSeconds = idle_ttl_seconds
        });
        return verbose
            ? JsonSerializer.Serialize(entry, JsonOpts.Default)
            : ConciseResponse.StoredBlackboardEntry(entry);
    }

    [McpServerTool(Name = "get_blackboard_entry"), Description("Get a cross-project shared blackboard entry by slug. Access refreshes idle TTL.")]
    public static async Task<string> GetBlackboardEntry(
        IBlackboardRepository repo,
        [Description("Blackboard entry slug.")] string slug)
    {
        var entry = await repo.GetAsync(slug);
        return entry is not null
            ? JsonSerializer.Serialize(entry, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Blackboard entry '{slug}' not found." }, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_blackboard_entries"), Description("List cross-project shared blackboard entry summaries. Listing refreshes idle TTL for returned expiring entries.")]
    public static async Task<string> ListBlackboardEntries(
        IBlackboardRepository repo,
        [Description("Filter by tags (comma-separated). Entry must have ALL specified tags.")] string? tags = null)
    {
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = await repo.ListAsync(tagList);
        return JsonSerializer.Serialize(entries, JsonOpts.Default);
    }

    [McpServerTool(Name = "delete_blackboard_entry"), Description("Delete a cross-project shared blackboard entry by slug.")]
    public static async Task<string> DeleteBlackboardEntry(
        IBlackboardRepository repo,
        [Description("Blackboard entry slug.")] string slug)
    {
        var deleted = await repo.DeleteAsync(slug);
        return deleted
            ? JsonSerializer.Serialize(new { message = $"Blackboard entry '{slug}' deleted." }, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Blackboard entry '{slug}' not found." }, JsonOpts.Default);
    }

    [McpServerTool(Name = "cleanup_blackboard_entries"), Description("Delete expired cross-project shared blackboard entries and return the number removed.")]
    public static async Task<string> CleanupBlackboardEntries(IBlackboardRepository repo)
    {
        var deleted = await repo.DeleteExpiredAsync();
        return JsonSerializer.Serialize(new { deleted }, JsonOpts.Default);
    }
}
