using System.Text;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Llm;

public sealed class LibrarianGatherer
{
    private readonly ITaskRepository _tasks;
    private readonly IDocumentRepository _docs;
    private readonly IMessageRepository _messages;

    public LibrarianGatherer(ITaskRepository tasks, IDocumentRepository docs, IMessageRepository messages)
    {
        _tasks = tasks;
        _docs = docs;
        _messages = messages;
    }

    public async Task<GatheredContext> GatherAsync(
        string projectId,
        string query,
        int? taskId = null,
        bool includeGlobal = true,
        int tokenBudget = 8000)
    {
        var sb = new StringBuilder();
        var tokensUsed = 0;

        // Phase 1: Task context (highest priority — never truncated)
        TaskDetail? taskDetail = null;
        ProjectTask? parentTask = null;
        if (taskId.HasValue)
        {
            taskDetail = await _tasks.GetDetailAsync(taskId.Value);
            if (taskDetail?.Task.ParentId is { } parentId)
                parentTask = await _tasks.GetByIdAsync(parentId);
        }

        if (taskDetail is not null)
        {
            var section = FormatTaskSection(taskDetail, parentTask);
            tokensUsed += AppendWithinBudget(sb, section, tokensUsed, tokenBudget);
        }

        // Phase 2: Document search via FTS
        var ftsQuery = BuildSearchQuery(query, taskDetail);
        if (ftsQuery is not null)
        {
            var projectDocs = await SearchDocsSafe(ftsQuery, projectId);
            var globalDocs = includeGlobal ? await SearchDocsSafe(ftsQuery, "_global") : [];

            if (projectDocs.Count > 0 || globalDocs.Count > 0)
            {
                var section = FormatDocumentSection(projectDocs, globalDocs);
                tokensUsed += AppendWithinBudget(sb, section, tokensUsed, tokenBudget);
            }
        }

        // Phase 3: Recent project messages (lowest priority — truncated first)
        var recentMessages = await _messages.GetMessagesAsync(projectId, limit: 50);
        if (recentMessages.Count > 0 && tokensUsed < tokenBudget - 200)
        {
            var section = FormatMessageSection(recentMessages);
            var remaining = tokenBudget - tokensUsed;
            var truncated = TruncateToTokenBudget(section, remaining);
            tokensUsed += EstimateTokens(truncated);
            sb.Append(truncated);
        }

        return new GatheredContext(sb.ToString(), tokensUsed);
    }

    private static string? BuildSearchQuery(string query, TaskDetail? taskDetail)
    {
        if (taskDetail is null)
            return FtsQuerySanitizer.Sanitize(query);

        var tagText = taskDetail.Task.Tags is { Count: > 0 }
            ? string.Join(" ", taskDetail.Task.Tags)
            : null;

        return FtsQuerySanitizer.BuildCombinedQuery(query, taskDetail.Task.Title, tagText);
    }

    private async Task<List<DocumentSearchResult>> SearchDocsSafe(string ftsQuery, string projectId)
    {
        try
        {
            return await _docs.SearchAsync(ftsQuery, projectId);
        }
        catch
        {
            // FTS query failure (e.g., malformed despite sanitization) — degrade gracefully
            return [];
        }
    }

    // --- Formatting ---

    private static string FormatTaskSection(TaskDetail detail, ProjectTask? parent)
    {
        var sb = new StringBuilder();
        var task = detail.Task;

        sb.AppendLine("## Task Context");
        sb.AppendLine();
        sb.AppendLine($"### Task #{task.Id}: {task.Title} [project: {task.ProjectId}]");
        sb.AppendLine($"Status: {task.Status.ToDbValue()} | Priority: {task.Priority} | Assigned: {task.AssignedTo ?? "unassigned"}");
        if (task.Tags is { Count: > 0 })
            sb.AppendLine($"Tags: {string.Join(", ", task.Tags)}");
        if (!string.IsNullOrEmpty(task.Description))
        {
            sb.AppendLine();
            sb.AppendLine(task.Description);
        }
        sb.AppendLine();

        if (parent is not null)
        {
            sb.AppendLine($"### Parent Task #{parent.Id}: {parent.Title}");
            sb.AppendLine($"Status: {parent.Status.ToDbValue()} | Priority: {parent.Priority}");
            if (!string.IsNullOrEmpty(parent.Description))
                sb.AppendLine(parent.Description);
            sb.AppendLine();
        }

        if (detail.Subtasks.Count > 0)
        {
            sb.AppendLine("### Subtasks");
            foreach (var sub in detail.Subtasks)
                sb.AppendLine($"- #{sub.Id}: {sub.Title} [{sub.Status.ToDbValue()}]");
            sb.AppendLine();
        }

        if (detail.Dependencies.Count > 0)
        {
            sb.AppendLine("### Dependencies");
            foreach (var dep in detail.Dependencies)
                sb.AppendLine($"- #{dep.TaskId}: {dep.Title} [{dep.Status.ToDbValue()}]");
            sb.AppendLine();
        }

        if (detail.RecentMessages.Count > 0)
        {
            sb.AppendLine("### Task Messages");
            foreach (var msg in detail.RecentMessages)
                sb.AppendLine($"[msg#{msg.Id} by {msg.Sender} at {msg.CreatedAt:yyyy-MM-dd}] {msg.Content}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatDocumentSection(
        List<DocumentSearchResult> projectDocs,
        List<DocumentSearchResult> globalDocs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Relevant Documents");
        sb.AppendLine();

        foreach (var doc in projectDocs)
        {
            sb.AppendLine($"### [doc: {doc.ProjectId}/{doc.Slug}] {doc.Title} ({doc.DocType.ToDbValue()})");
            sb.AppendLine(StripHtmlTags(doc.Snippet));
            sb.AppendLine();
        }

        foreach (var doc in globalDocs)
        {
            sb.AppendLine($"### [doc: _global/{doc.Slug}] {doc.Title} ({doc.DocType.ToDbValue()})");
            sb.AppendLine(StripHtmlTags(doc.Snippet));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatMessageSection(List<Message> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Recent Project Messages");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var taskRef = msg.TaskId.HasValue ? $" (task #{msg.TaskId})" : "";
            sb.AppendLine($"[msg#{msg.Id} by {msg.Sender} at {msg.CreatedAt:yyyy-MM-dd}]{taskRef} {msg.Content}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    // --- Utilities ---

    private static int AppendWithinBudget(StringBuilder sb, string section, int currentTokens, int budget)
    {
        var sectionTokens = EstimateTokens(section);
        if (currentTokens + sectionTokens <= budget)
        {
            sb.Append(section);
            return sectionTokens;
        }

        // Try truncating the section to fit
        var remaining = budget - currentTokens;
        if (remaining > 200)
        {
            var truncated = TruncateToTokenBudget(section, remaining);
            sb.Append(truncated);
            return EstimateTokens(truncated);
        }

        return 0;
    }

    internal static int EstimateTokens(string text) => text.Length / 4;

    private static string TruncateToTokenBudget(string text, int tokenBudget)
    {
        var charBudget = tokenBudget * 4;
        if (text.Length <= charBudget)
            return text;

        // Truncate at a line boundary
        var truncated = text[..charBudget];
        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > charBudget / 2)
            truncated = truncated[..(lastNewline + 1)];

        return truncated + "\n[...truncated]\n";
    }

    private static string StripHtmlTags(string html) =>
        html.Replace("<b>", "").Replace("</b>", "");
}
