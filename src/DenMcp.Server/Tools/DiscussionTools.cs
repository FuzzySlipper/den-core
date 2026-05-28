using System.ComponentModel;
using DenMcp.Core.Mcp;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

/// <summary>
/// MCP tools for first-class discussion threads and comments on documents.
/// Discussion data is kept separate from document content — get_document returns
/// canonical document JSON with no discussion fields.
/// </summary>
[McpServerToolType]
public sealed class DiscussionTools
{
    // ── Read tools (reader profiles + worker-reviewer get read-only) ──

    [McpToolProfile("admin-current", "planner", "runner", "worker-reviewer")]
    [McpToolBundle("discussion")]
    [McpServerTool(Name = "get_document_discussion"), Description(
        "Get discussion threads and comments for a document. Discussion is separate from document content — " +
        "use get_document for canonical document JSON without discussion fields. " +
        "When create_if_missing=false (default) and no default thread exists, returns an empty result. " +
        "Set create_if_missing=true to auto-create a default thread. " +
        "Use include_resolved=true to include resolved/archived threads.")]
    public static async Task<string> GetDocumentDiscussion(
        IDiscussionRepository repo,
        IDocumentRepository docRepo,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug,
        [Description("If true, auto-create a default discussion thread if none exists.")] bool create_if_missing = false,
        [Description("If true, include resolved and archived threads in results.")] bool include_resolved = false,
        [Description("Optional anchor to filter threads by.")] string? anchor = null,
        [Description("If true, return full JSON records instead of concise summary.")] bool verbose = false)
    {
        // Verify document exists
        var doc = await docRepo.GetAsync(project_id, slug);
        if (doc is null)
            return JsonSerializer.Serialize(new { error = $"Document '{slug}' not found in project '{project_id}'." }, JsonOpts.Default);

        var threadKey = ThreadKeyForAnchor(anchor);
        DiscussionThread? selectedThread = null;

        if (create_if_missing)
        {
            selectedThread = await GetOrCreateDocumentThreadAsync(repo, project_id, slug, threadKey, "mcp-agent");
        }
        else
        {
            var threads = await repo.ListDocumentThreadsAsync(project_id, slug);
            selectedThread = threads.FirstOrDefault(t => t.ThreadKey == threadKey);
        }

        if (selectedThread is null)
        {
            // No discussion threads at all — return empty
            if (verbose)
                return JsonSerializer.Serialize(new { document_id = doc.Id, project_id, slug, threads = Array.Empty<object>(), comments = Array.Empty<object>() }, JsonOpts.Default);
            return JsonSerializer.Serialize(new { summary = $"No discussions for '{project_id}/{slug}'.", threads_count = 0, comments_count = 0 }, JsonOpts.Default);
        }

        // Collect threads
        var allThreads = await repo.ListDocumentThreadsAsync(project_id, slug);
        var filteredThreads = allThreads
            .Where(t => anchor is null || t.ThreadKey == threadKey)
            .Where(t => include_resolved || t.Status == DiscussionThreadStatus.Open)
            .ToList();

        // Collect comments for the selected/default thread
        var comments = await repo.ListCommentsAsync(selectedThread.Id);

        if (verbose)
        {
            return JsonSerializer.Serialize(new
            {
                document_id = doc.Id,
                project_id,
                slug,
                threads = filteredThreads,
                default_thread = selectedThread,
                comments
            }, JsonOpts.Default);
        }

        return ConciseResponse.Serialize(new
        {
            summary = $"Discussion for '{project_id}/{slug}': {filteredThreads.Count} thread(s), {comments.Count} comment(s)",
            threads_count = filteredThreads.Count,
            comments_count = comments.Count,
            default_thread_id = selectedThread.Id,
            default_thread_title = selectedThread.Title,
            default_thread_status = selectedThread.Status
        });
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-reviewer")]
    [McpToolBundle("discussion")]
    [McpServerTool(Name = "list_discussion_threads"), Description(
        "List discussion threads across targets. Currently supports document-target threads only; " +
        "omit target_type or set target_type=document. " +
        "Filter by target_project_id, target_slug, or status. Default limit is 50.")]
    public static async Task<string> ListDiscussionThreads(
        IDiscussionRepository repo,
        [Description("Target type filter. Currently only 'document' is supported. Omit to search all document threads.")] string? target_type = null,
        [Description("Project or space ID to filter by.")] string? target_project_id = null,
        [Description("Document slug to filter by.")] string? target_slug = null,
        [Description("Filter by thread status: open, resolved, archived.")] string? status = null,
        [Description("Maximum results to return.")] int limit = 50,
        [Description("If true, return full JSON records instead of concise summary.")] bool verbose = false)
    {
        // Phase 1: only document target type is supported
        if (target_type is not null && target_type != "document")
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Phase 1 only supports target_type='document'. Got '{target_type}'. No threads returned.",
                threads = Array.Empty<object>()
            }, JsonOpts.Default);
        }

        if (string.IsNullOrWhiteSpace(target_project_id) || string.IsNullOrWhiteSpace(target_slug))
        {
            if (verbose)
                return JsonSerializer.Serialize(new { threads = Array.Empty<object>(), notice = "Phase 1 requires both target_project_id and target_slug. Support for unfiltered listing may be added in a future phase." }, JsonOpts.Default);
            return JsonSerializer.Serialize(new { summary = "Phase 1 requires both target_project_id and target_slug. No threads returned.", count = 0 }, JsonOpts.Default);
        }

        var threads = await repo.ListDocumentThreadsAsync(target_project_id, target_slug, status);

        return verbose
            ? JsonSerializer.Serialize(new { threads = threads.Take(limit).ToList() }, JsonOpts.Default)
            : ConciseResponse.Serialize(new
            {
                summary = $"Found {threads.Count} thread(s) for '{target_project_id}/{target_slug}'",
                count = threads.Count,
                thread_ids = threads.Take(limit).Select(t => t.Id).ToArray(),
                thread_titles = threads.Take(limit).Select(t => t.Title).ToArray()
            });
    }

    [McpToolProfile("admin-current", "planner", "runner", "worker-reviewer")]
    [McpToolBundle("discussion")]
    [McpServerTool(Name = "get_discussion_thread"), Description(
        "Get a single discussion thread by ID, optionally including its comments. " +
        "Returns thread metadata and, when include_comments=true, all comments in chronological order.")]
    public static async Task<string> GetDiscussionThread(
        IDiscussionRepository repo,
        [Description("Discussion thread ID.")] int thread_id,
        [Description("If true, include full comment list.")] bool include_comments = true,
        [Description("If true, return full JSON records instead of concise summary.")] bool verbose = false)
    {
        var thread = await repo.GetThreadByIdAsync(thread_id);
        if (thread is null)
            return JsonSerializer.Serialize(new { error = $"Discussion thread {thread_id} not found." }, JsonOpts.Default);

        List<DiscussionCommentSummary>? comments = null;
        if (include_comments)
            comments = await repo.ListCommentsAsync(thread_id);

        if (verbose)
        {
            return JsonSerializer.Serialize(new
            {
                thread,
                comments = (IReadOnlyList<DiscussionCommentSummary>?)comments ?? Array.Empty<DiscussionCommentSummary>()
            }, JsonOpts.Default);
        }

        return ConciseResponse.Serialize(new
        {
            summary = $"Thread #{thread.Id}: '{thread.Title}' ({thread.Status}, {comments?.Count ?? 0} comment(s))",
            id = thread.Id,
            title = thread.Title,
            status = thread.Status,
            target_type = thread.TargetType,
            target_project_id = thread.TargetProjectId,
            target_slug = thread.TargetSlug,
            created_by = thread.CreatedBy,
            comments_count = comments?.Count ?? 0,
            last_comment_at = thread.LastCommentAt
        });
    }

    // ── Write tools (mutation — planner, runner, admin-current) ──

    [McpToolProfile("admin-current", "planner", "runner")]
    [McpToolBundle("discussion")]
    [McpServerTool(Name = "comment_on_document"), Description(
        "Add a comment to a document's default discussion thread. " +
        "If no default thread exists, one is auto-created. " +
        "For replying to an existing comment, use parent_comment_id. " +
        "Use get_document_discussion (not get_document) to see comments — discussion is separate from document content.")]
    public static async Task<string> CommentOnDocument(
        IDiscussionRepository repo,
        IDocumentRepository docRepo,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug,
        [Description("Author identity string (e.g. agent name or username).")] string author_identity,
        [Description("Comment body in markdown format.")] string body_markdown,
        [Description("Parent comment ID for replies. Omit for root-level comments.")] int? parent_comment_id = null,
        [Description("Comment kind: comment, question, answer, resolution, version_note. Default: comment.")] string? comment_kind = null,
        [Description("Optional anchor within the document.")] string? anchor = null,
        [Description("JSON array of mentioned identities. Accepts native JSON or JSON-encoded string.")] object? mentions = null,
        [Description("JSON array of source references. Accepts native JSON or JSON-encoded string.")] object? source_refs = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        // Verify document exists
        var doc = await docRepo.GetAsync(project_id, slug);
        if (doc is null)
            return JsonSerializer.Serialize(new { error = $"Document '{slug}' not found in project '{project_id}'." }, JsonOpts.Default);

        // Get or create default or anchor-specific thread
        var thread = await GetOrCreateDocumentThreadAsync(repo, project_id, slug, ThreadKeyForAnchor(anchor), author_identity);

        var parsedMentions = ToolArgumentJson.ParseStringArray(mentions, "mentions");
        var sourceRefsJson = SerializeJsonArgument(source_refs, "source_refs");

        DiscussionComment comment;
        try
        {
            if (parent_comment_id is int parentId)
            {
                comment = await repo.AddReplyAsync(
                    thread.Id, parentId, body_markdown, author_identity,
                    comment_kind,
                    parsedMentions is not null ? JsonSerializer.Serialize(parsedMentions) : null,
                    sourceRefsJson);
            }
            else
            {
                comment = await repo.AddCommentAsync(
                    thread.Id, body_markdown, author_identity,
                    comment_kind,
                    parsedMentions is not null ? JsonSerializer.Serialize(parsedMentions) : null,
                    sourceRefsJson);
            }
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }

        if (verbose)
            return JsonSerializer.Serialize(comment, JsonOpts.Default);

        return ConciseResponse.Serialize(new
        {
            summary = $"Commented on '{project_id}/{slug}' (thread #{thread.Id})",
            comment_id = comment.Id,
            thread_id = comment.ThreadId,
            author = comment.AuthorIdentity,
            comment_kind = comment.CommentKind,
            parent_comment_id = comment.ParentCommentId
        });
    }

    [McpToolProfile("admin-current", "planner", "runner")]
    [McpToolBundle("discussion")]
    [McpServerTool(Name = "create_discussion_comment"), Description(
        "Add a comment to an existing discussion thread by thread ID. " +
        "For document-level comments, consider using comment_on_document instead. " +
        "Use parent_comment_id to reply to an existing comment.")]
    public static async Task<string> CreateDiscussionComment(
        IDiscussionRepository repo,
        [Description("Discussion thread ID.")] int thread_id,
        [Description("Author identity string (e.g. agent name or username).")] string author_identity,
        [Description("Comment body in markdown format.")] string body_markdown,
        [Description("Parent comment ID for replies. Omit for root-level comments.")] int? parent_comment_id = null,
        [Description("Comment kind: comment, question, answer, resolution, version_note. Default: comment.")] string? comment_kind = null,
        [Description("JSON array of mentioned identities. Accepts native JSON or JSON-encoded string.")] object? mentions = null,
        [Description("JSON array of source references. Accepts native JSON or JSON-encoded string.")] object? source_refs = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var parsedMentions = ToolArgumentJson.ParseStringArray(mentions, "mentions");
        var sourceRefsJson = SerializeJsonArgument(source_refs, "source_refs");

        DiscussionComment comment;
        try
        {
            if (parent_comment_id is int parentId)
            {
                comment = await repo.AddReplyAsync(
                    thread_id, parentId, body_markdown, author_identity,
                    comment_kind,
                    parsedMentions is not null ? JsonSerializer.Serialize(parsedMentions) : null,
                    sourceRefsJson);
            }
            else
            {
                comment = await repo.AddCommentAsync(
                    thread_id, body_markdown, author_identity,
                    comment_kind,
                    parsedMentions is not null ? JsonSerializer.Serialize(parsedMentions) : null,
                    sourceRefsJson);
            }
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }

        if (verbose)
            return JsonSerializer.Serialize(comment, JsonOpts.Default);

        return ConciseResponse.Serialize(new
        {
            summary = $"Added comment to thread #{thread_id}",
            comment_id = comment.Id,
            thread_id = comment.ThreadId,
            author = comment.AuthorIdentity,
            comment_kind = comment.CommentKind,
            parent_comment_id = comment.ParentCommentId
        });
    }

    // ── Admin tool (admin-current only) ──

    [McpToolProfile("admin-current")]
    [McpToolBundle("discussion")]
    [McpServerTool(Name = "update_discussion_thread"), Description(
        "Update a discussion thread's status, summary, resolution_summary, or title. " +
        "Common use: mark threads as resolved or archived after discussion concludes.")]
    public static async Task<string> UpdateDiscussionThread(
        IDiscussionRepository repo,
        [Description("Discussion thread ID to update.")] int thread_id,
        [Description("New status: open, resolved, archived. Omit to leave unchanged.")] string? status = null,
        [Description("Optional thread summary update.")] string? summary = null,
        [Description("Optional resolution summary. Typically set when status=resolved.")] string? resolution_summary = null,
        [Description("Optional title update.")] string? title = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var thread = await repo.GetThreadByIdAsync(thread_id);
        if (thread is null)
            return JsonSerializer.Serialize(new { error = $"Discussion thread {thread_id} not found." }, JsonOpts.Default);

        // Apply partial updates
        if (status is not null)
            thread.Status = status;
        if (summary is not null)
            thread.Summary = summary;
        if (resolution_summary is not null)
            thread.ResolutionSummary = resolution_summary;
        if (title is not null)
            thread.Title = title;

        try
        {
            var updated = await repo.UpdateThreadAsync(thread);

            if (verbose)
                return JsonSerializer.Serialize(updated, JsonOpts.Default);

            return ConciseResponse.Serialize(new
            {
                summary = $"Updated thread #{thread_id}: status={updated.Status}, title='{updated.Title}'",
                id = updated.Id,
                status = updated.Status,
                title = updated.Title,
                summary_text = updated.Summary,
                resolution_summary = updated.ResolutionSummary
            });
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
        }
    }

    private static string ThreadKeyForAnchor(string? anchor) =>
        string.IsNullOrWhiteSpace(anchor) ? "default" : $"section:{anchor.Trim()}";

    private static async Task<DiscussionThread> GetOrCreateDocumentThreadAsync(
        IDiscussionRepository repo,
        string projectId,
        string slug,
        string threadKey,
        string createdBy)
    {
        if (threadKey == "default")
            return await repo.GetOrCreateDefaultDocumentThreadAsync(projectId, slug, createdBy);

        var existing = (await repo.ListDocumentThreadsAsync(projectId, slug))
            .FirstOrDefault(t => t.ThreadKey == threadKey);
        if (existing is not null)
            return existing;

        return await repo.CreateDocumentThreadAsync(
            projectId,
            slug,
            threadKey,
            $"Discussion {threadKey} for {slug}",
            createdBy);
    }

    private static string? SerializeJsonArgument(object? value, string fieldName)
    {
        if (value is null)
            return null;

        if (value is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(text);
                return JsonSerializer.Serialize(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{fieldName} must be valid JSON when supplied as a string.", ex);
            }
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return JsonSerializer.Serialize(element);
        }

        return JsonSerializer.Serialize(value, JsonOpts.Default);
    }

}
