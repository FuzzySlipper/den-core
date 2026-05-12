using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class LibrarianTools
{
    [McpServerTool(Name = "query_librarian"), Description(
        "Ask the librarian for context relevant to your current work. " +
        "Returns relevant tasks, documents, and messages with source attribution and recommendations. " +
        "Best used at the start of a task or when you need background on a topic. " +
        "Works with any space ID (project, personal, assistant, knowledge_base, etc.).")]
    public static async Task<string> QueryLibrarian(
        LibrarianService librarian,
        LlmConfig llmConfig,
        [Description("Project or space ID to search within.")] string project_id,
        [Description("What you're working on or looking for, e.g. 'I'm starting work on task 47 — implementing FTS5 search'.")] string query,
        [Description("Task ID to enrich search with task context (dependencies, messages, tags).")] int? task_id = null,
        [Description("Whether to also search _global project documents. Default: true.")] bool include_global = true)
    {
        if (string.IsNullOrEmpty(llmConfig.Endpoint))
            return JsonSerializer.Serialize(
                new { error = "Librarian is not configured. Set DenMcp:Llm:Endpoint in appsettings.json or pass --llm-endpoint." },
                JsonOpts.Default);

        try
        {
            var response = await librarian.QueryAsync(project_id, query, task_id, include_global);
            return JsonSerializer.Serialize(response, JsonOpts.Default);
        }
        catch (KeyNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
    }
}
