using System.ComponentModel;
using DenCore.Mcp;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

[McpServerToolType]
public sealed class DocumentTools
{
    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "store_document"), Description("Create or update a document. If a document with the same project_id + slug exists, it is overwritten.")]
    public static async Task<string> StoreDocument(
        IDocumentRepository repo,
        [Description("Project or space ID. Use '_global' for cross-project docs.")] string project_id,
        [Description("Unique slug within the project, e.g. 'damage-system-spec'.")] string slug,
        [Description("Document title.")] string title,
        [Description("Document content (markdown).")] string content,
        [Description("Document type: prd, spec, adr, convention, reference, note, memory. Default: spec.")] string doc_type = "spec",
        [Description("JSON array of string tags. Accepts a native JSON array or a JSON-encoded string for backward compatibility.")] object? tags = null,
        [Description("Optional short summary for indexing and listing.")] string? summary = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var parsedTags = ToolArgumentJson.ParseStringArray(tags, "tags");
        var doc = await repo.UpsertAsync(new Document
        {
            ProjectId = project_id,
            Slug = slug,
            Title = title,
            Content = content,
            DocType = EnumExtensions.ParseDocType(doc_type),
            Tags = parsedTags,
            Summary = summary
        });
        return verbose
            ? JsonSerializer.Serialize(doc, JsonOpts.Default)
            : ConciseResponse.StoredDocument(doc);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "get_document"), Description("Get a document's full content by project or space ID and slug. Returns documents regardless of visibility (normal, hidden, archived).")]
    public static async Task<string> GetDocument(
        IDocumentRepository repo,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug)
    {
        var doc = await repo.GetAsync(project_id, slug);
        if (doc is null)
            return JsonSerializer.Serialize(new { error = $"Document '{slug}' not found in project '{project_id}'." }, JsonOpts.Default);
        return JsonSerializer.Serialize(doc, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "list_documents"), Description("List document summaries (without content). Excludes archived documents by default. Omit project_id to list across all projects and spaces.")]
    public static async Task<string> ListDocuments(
        IDocumentRepository repo,
        [Description("Project or space ID. Omit to list across all projects and spaces.")] string? project_id = null,
        [Description("Filter by type: prd, spec, adr, convention, reference, note, memory.")] string? doc_type = null,
        [Description("Filter by tags (comma-separated). Document must have ALL specified tags.")] string? tags = null,
        [Description("Filter by visibility: normal, hidden, archived. Omit to exclude archived documents.")] string? visibility = null)
    {
        var parsedType = doc_type is not null ? EnumExtensions.ParseDocType(doc_type) : (DocType?)null;
        var parsedVisibility = visibility is not null ? EnumExtensions.ParseDocumentVisibility(visibility) : (DocumentVisibility?)null;
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var docs = await repo.ListAsync(project_id, parsedType, tagList, parsedVisibility);
        return JsonSerializer.Serialize(docs, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer", "worker-validator", "worker-drift-checker", "worker-packet-auditor")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "search_documents"), Description("Full-text search across documents. Excludes archived documents. Supports AND, OR, NOT, and \"phrase\" queries.")]
    public static async Task<string> SearchDocuments(
        IDocumentRepository repo,
        [Description("FTS5 search query.")] string query,
        [Description("Scope search to one project or space.")] string? project_id = null)
    {
        var results = await repo.SearchAsync(query, project_id);
        return JsonSerializer.Serialize(results, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "delete_document"), Description("Delete a document by project or space ID and slug.")]
    public static async Task<string> DeleteDocument(
        IDocumentRepository repo,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug)
    {
        var deleted = await repo.DeleteAsync(project_id, slug);
        return deleted
            ? JsonSerializer.Serialize(new { message = $"Document '{slug}' deleted from project '{project_id}'." }, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Document '{slug}' not found in project '{project_id}'." }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "update_document_visibility"), Description("Update a document's visibility to normal, hidden, or archived. Archiving is a status flip — not data movement. Use archive_document_preflight first to check for active references.")]
    public static async Task<string> UpdateDocumentVisibility(
        IDocumentRepository repo,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug,
        [Description("New visibility: normal, hidden, archived.")] string visibility,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var parsedVisibility = EnumExtensions.ParseDocumentVisibility(visibility);
        var doc = await repo.UpdateVisibilityAsync(project_id, slug, parsedVisibility);
        if (doc is null)
            return JsonSerializer.Serialize(new { error = $"Document '{slug}' not found in project '{project_id}'." }, JsonOpts.Default);
        return verbose
            ? JsonSerializer.Serialize(doc, JsonOpts.Default)
            : ConciseResponse.UpdatedDocumentVisibility(doc);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "archive_document_preflight"), Description("Check whether a document can be safely archived. Returns active references (agent guidance entries) that would be affected. Prefer running this before update_document_visibility to archived.")]
    public static async Task<string> ArchiveDocumentPreflight(
        IDocumentRepository repo,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug)
    {
        var result = await repo.ArchivePreflightAsync(project_id, slug);
        return JsonSerializer.Serialize(result, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-coder", "worker-reviewer")]
    [McpToolBundle("document")]
    [McpServerTool(Name = "query_archived_documents"), Description("Deliberate archived document recall path. List or search documents that have been archived. Separate from default list_documents to prevent accidental inclusion of archived content.")]
    public static async Task<string> QueryArchivedDocuments(
        IDocumentRepository repo,
        [Description("Optional FTS5 search query. Omit to list all archived documents.")] string? query = null,
        [Description("Scope to one project or space.")] string? project_id = null,
        [Description("Filter by type: prd, spec, adr, convention, reference, note, memory.")] string? doc_type = null,
        [Description("Filter by tags (comma-separated).")] string? tags = null)
    {
        var parsedType = doc_type is not null ? EnumExtensions.ParseDocType(doc_type) : (DocType?)null;
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var results = await repo.SearchArchivedAsync(query, project_id);
            return JsonSerializer.Serialize(results, JsonOpts.Default);
        }

        var docs = await repo.ListArchivedAsync(project_id, parsedType, tagList);
        return JsonSerializer.Serialize(docs, JsonOpts.Default);
    }
}
