using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class TopicTools
{
    [McpServerTool(Name = "create_topic"), Description("Create a new consolidation topic in the registry.")]
    public static async Task<string> CreateTopic(
        ITopicRepository repo,
        [Description("Unique slug for the topic. Used as the canonical tag.")] string slug,
        [Description("Human-readable display name.")] string display_name,
        [Description("Optional description of what this topic covers.")] string? description = null,
        [Description("Optional array of alias tags that resolve to this topic.")] string[]? aliases = null,
        [Description("Status: active, inactive, deprecated. Defaults to active.")] string? status = null,
        [Description("Optional owning space/project ID.")] string? owning_space = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var topic = await repo.CreateAsync(new ConsolidationTopic
            {
                Slug = slug,
                DisplayName = display_name,
                Description = description,
                Aliases = aliases?.ToList(),
                Status = status ?? "active",
                OwningSpace = owning_space
            });
            return verbose
                ? JsonSerializer.Serialize(topic, JsonOpts.Default)
                : ConciseResponse.CreatedTopic(topic);
        }
        catch (InvalidOperationException ex)
        {
            return $"{{\"error\":\"{ex.Message}\"}}";
        }
    }

    [McpServerTool(Name = "list_topics"), Description("List consolidation topics. Defaults to active topics only.")]
    public static async Task<string> ListTopics(
        ITopicRepository repo,
        [Description("Filter by owning space ID.")] string? owning_space = null,
        [Description("Include inactive and deprecated topics.")] bool include_inactive = false)
    {
        var topics = await repo.ListAsync(owningSpace: owning_space, includeInactive: include_inactive);
        return JsonSerializer.Serialize(topics, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_topic"), Description("Get a consolidation topic by slug.")]
    public static async Task<string> GetTopic(
        ITopicRepository repo,
        [Description("Topic slug.")] string slug)
    {
        var topic = await repo.GetBySlugAsync(slug);
        return topic is not null
            ? JsonSerializer.Serialize(topic, JsonOpts.Default)
            : $"{{\"error\":\"Topic '{slug}' not found\"}}";
    }

    [McpServerTool(Name = "update_topic"), Description("Update a consolidation topic by id.")]
    public static async Task<string> UpdateTopic(
        ITopicRepository repo,
        [Description("Topic id.")] int id,
        [Description("Unique slug for the topic.")] string slug,
        [Description("Human-readable display name.")] string display_name,
        [Description("Optional description.")] string? description = null,
        [Description("Optional array of alias tags.")] string[]? aliases = null,
        [Description("Status: active, inactive, deprecated.")] string? status = null,
        [Description("Optional owning space/project ID.")] string? owning_space = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var topic = await repo.UpdateAsync(id, new ConsolidationTopic
            {
                Slug = slug,
                DisplayName = display_name,
                Description = description,
                Aliases = aliases?.ToList(),
                Status = status ?? "active",
                OwningSpace = owning_space
            });
            return verbose
                ? JsonSerializer.Serialize(topic, JsonOpts.Default)
                : ConciseResponse.UpdatedTopic(topic);
        }
        catch (InvalidOperationException ex)
        {
            return $"{{\"error\":\"{ex.Message}\"}}";
        }
    }

    [McpServerTool(Name = "delete_topic"), Description("Delete a consolidation topic by id.")]
    public static async Task<string> DeleteTopic(
        ITopicRepository repo,
        [Description("Topic id.")] int id)
    {
        var deleted = await repo.DeleteAsync(id);
        return deleted
            ? $"{{\"success\":true,\"summary\":\"Deleted topic {id}\"}}"
            : $"{{\"error\":\"Topic {id} not found\"}}";
    }

    [McpServerTool(Name = "validate_topic_tags"), Description("Validate topic tags against the registry. Resolves aliases to canonical slugs.")]
    public static async Task<string> ValidateTopicTags(
        ITopicRepository repo,
        [Description("Array of topic tags to validate.")] string[] tags,
        [Description("If true, allow inactive/deprecated topics to pass validation.")] bool allow_inactive = false)
    {
        var results = await repo.ValidateManyAsync(tags, allow_inactive);
        return JsonSerializer.Serialize(results, JsonOpts.Default);
    }
}
