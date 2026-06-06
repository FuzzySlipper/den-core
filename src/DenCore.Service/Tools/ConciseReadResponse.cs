using System.Text.Json;

namespace DenCore.Service.Tools;

/// <summary>
/// Provides concise projections for high-volume MCP read/list response payloads.
/// Default concise responses include IDs, statuses, counts, timestamps,
/// and explicit deep_read_hint entries. Verbose paths preserve full detail.
///
/// Intended to reduce persisted MCP tool payload sizes (#1985/#1986, #2001).
/// </summary>
public static class ConciseReadResponse
{
    private const int DefaultContentPreviewChars = 500;
    private const int MaxRecentMessages = 5;

    /// <summary>
    /// Shrink a serializable object graph by replacing known verbose sub-objects
    /// with concise projections. Leaves unrecognised objects unchanged.
    /// </summary>
    public static object Shrink(object obj)
    {
        return obj switch
        {
            // Array/list — recursively shrink each element
            System.Collections.IList list => ShrinkList(list),
            // Anonymous/dictionary objects with known shape(s)
            _ => ShrinkObject(obj),
        };
    }

    private static List<object> ShrinkList(System.Collections.IList list)
    {
        var result = new List<object>(list.Count);
        foreach (var item in list)
            result.Add(Shrink(item));
        return result;
    }

    private static object ShrinkObject(object obj)
    {
        if (obj is null) return new { };
        var type = obj.GetType();

        // If this object has a "worker_runs" property, shrink each worker_run
        if (TryGetProperty(obj, "worker_runs", out var runsObj) && runsObj is System.Collections.IList runs)
            return ReplaceProperty(obj, "worker_runs", ShrinkWorkerRuns(runs));

        // If this object has a "worker_run" property, shrink it
        if (TryGetProperty(obj, "worker_run", out var wrObj) && wrObj is not null)
            return ReplaceProperty(obj, "worker_run", ShrinkWorkerRun(wrObj));

        // Task with recent_messages — shrink to preview
        if (TryGetProperty(obj, "recent_messages", out var msgObj) && msgObj is System.Collections.IList msgs)
            return ReplaceProperty(obj, "recent_messages", ShrinkMessages(msgs));

        // Message list items
        if (TryGetProperty(obj, "items", out var itemsObj) && itemsObj is System.Collections.IList items)
            return ReplaceProperty(obj, "items", ShrinkMessages(items));

        // Document with content
        if (TryGetProperty(obj, "content", out var content) && content is string contentStr && contentStr.Length > DefaultContentPreviewChars)
            return ReplaceProperty(obj, "content", ContentPreview(contentStr));

        // Pool members
        if (TryGetProperty(obj, "pool_members", out var pmObj) && pmObj is System.Collections.IList pms)
            return ReplaceProperty(obj, "pool_members", ShrinkPoolMembers(pms));

        // Assignments list
        if (TryGetProperty(obj, "assignments", out var assignObj) && assignObj is System.Collections.IList assigns)
            return ReplaceProperty(obj, "assignments", ShrinkAssignments(assigns));

        // Notifications — shrink bodies
        if (TryGetProperty(obj, "notifications", out var notifObj) && notifObj is System.Collections.IList notifs)
            return ReplaceProperty(obj, "notifications", ShrinkMessages(notifs));

        // Documents list
        if (TryGetProperty(obj, "documents", out var docsObj) && docsObj is System.Collections.IList docs)
            return ReplaceProperty(obj, "documents", ShrinkDocuments(docs));

        // Search results
        if (TryGetProperty(obj, "results", out var resultsObj) && resultsObj is System.Collections.IList results)
            return ReplaceProperty(obj, "results", ShrinkSearchResults(results));

        return obj;
    }

    private static List<object> ShrinkWorkerRuns(System.Collections.IList runs)
    {
        var result = new List<object>(runs.Count);
        foreach (var run in runs)
            result.Add(ShrinkWorkerRun(run));
        return result;
    }

    private static object ShrinkWorkerRun(object run)
    {
        if (run is null) return new { };

        return new
        {
            run_id = Prop(run, "run_id"),
            assignment_id = Prop(run, "assignment_id"),
            project_id = Prop(run, "project_id"),
            task_id = Prop(run, "task_id"),
            role = Prop(run, "role"),
            state = Prop(run, "state"),
            status = Prop(run, "status"),
            worker_identity = Prop(run, "worker_identity"),
            updated_at = Prop(run, "updated_at"),
            deep_read_hint = "Use get_worker_run with verbose=true for full projection including launch metadata.",
        };
    }

    private static List<object> ShrinkMessages(System.Collections.IList messages)
    {
        var result = new List<object>(Math.Min(messages.Count, MaxRecentMessages));
        var count = 0;
        foreach (var msg in messages)
        {
            if (count++ >= MaxRecentMessages) break;
            result.Add(ShrinkMessage(msg));
        }
        return result;
    }

    private static object ShrinkMessage(object msg)
    {
        if (msg is null) return new { };
        var body = Prop(msg, "body")?.ToString() ?? "";
        var bodyStr = body is string s && s.Length > DefaultContentPreviewChars
            ? s[..DefaultContentPreviewChars]
            : body;

        return new
        {
            id = Prop(msg, "id"),
            sender = Prop(msg, "sender"),
            task_id = Prop(msg, "task_id"),
            thread_id = Prop(msg, "thread_id"),
            created_at = Prop(msg, "created_at"),
            content_preview = bodyStr,
            content_chars = body is string bs ? bs.Length : 0,
            content_truncated = body is string bs2 && bs2.Length > DefaultContentPreviewChars,
        };
    }

    private static List<object> ShrinkPoolMembers(System.Collections.IList members)
    {
        var result = new List<object>(members.Count);
        foreach (var m in members)
        {
            result.Add(new
            {
                worker_identity = Prop(m, "worker_identity"),
                worker_role = Prop(m, "worker_role"),
                status = Prop(m, "status"),
                updated_at = Prop(m, "updated_at"),
                active_assignment_count = Prop(m, "active_assignment_count"),
                deep_read_hint = "Use get_worker_pool_summary or list_pool_members with verbose=true for full member/assignment details.",
            });
        }
        return result;
    }

    private static List<object> ShrinkAssignments(System.Collections.IList assignments)
    {
        var result = new List<object>(assignments.Count);
        foreach (var a in assignments)
        {
            result.Add(new
            {
                id = Prop(a, "id"),
                run_id = Prop(a, "run_id"),
                project_id = Prop(a, "project_id"),
                task_id = Prop(a, "task_id"),
                role = Prop(a, "role"),
                state = Prop(a, "state"),
                worker_identity = Prop(a, "worker_identity"),
                updated_at = Prop(a, "updated_at"),
                deep_read_hint = "Use verbose=true for full assignment details.",
            });
        }
        return result;
    }

    private static List<object> ShrinkDocuments(System.Collections.IList documents)
    {
        var result = new List<object>(documents.Count);
        foreach (var d in documents)
        {
            result.Add(new
            {
                project_id = Prop(d, "project_id"),
                slug = Prop(d, "slug"),
                title = Prop(d, "title"),
                doc_type = Prop(d, "doc_type"),
                visibility = Prop(d, "visibility"),
                summary = Prop(d, "summary"),
                tags = Prop(d, "tags"),
                deep_read_hint = "Use get_document with verbose=true for full content.",
            });
        }
        return result;
    }

    private static List<object> ShrinkSearchResults(System.Collections.IList results)
    {
        var result = new List<object>(results.Count);
        foreach (var r in results)
        {
            result.Add(new
            {
                project_id = Prop(r, "project_id"),
                slug = Prop(r, "slug"),
                title = Prop(r, "title"),
                doc_type = Prop(r, "doc_type"),
                snippet = Prop(r, "snippet"),
                deep_read_hint = "Use get_document with verbose=true for full content.",
            });
        }
        return result;
    }

    /// <summary>
    /// Build a bounded content preview for document/blackboard content.
    /// </summary>
    public static object ContentPreview(string content)
    {
        var truncated = content.Length > DefaultContentPreviewChars;
        return new
        {
            content_preview = truncated ? content[..DefaultContentPreviewChars] : content,
            content_chars = content.Length,
            content_truncated = truncated,
            deep_read_hint = truncated ? "Use verbose=true for full content." : null,
        };
    }

    public static object DocumentMetadata(object doc)
    {
        return new
        {
            project_id = Prop(doc, "project_id"),
            slug = Prop(doc, "slug"),
            title = Prop(doc, "title"),
            doc_type = Prop(doc, "doc_type"),
            visibility = Prop(doc, "visibility"),
            summary = Prop(doc, "summary"),
            tags = Prop(doc, "tags"),
            created_at = Prop(doc, "created_at"),
            updated_at = Prop(doc, "updated_at"),
            deep_read_hint = "Use verbose=true for full document content.",
        };
    }

    // ── Reflection helpers ───────────────────────────────────────────────

    private static bool TryGetProperty(object obj, string name, out object? value)
    {
        var prop = obj.GetType().GetProperty(name);
        if (prop is not null)
        {
            value = prop.GetValue(obj);
            return true;
        }
        value = null;
        return false;
    }

    private static object ReplaceProperty(object obj, string name, object newValue)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.GetType().GetProperties())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                dict[ToSnakeCase(prop.Name)] = newValue;
            else
                dict[ToSnakeCase(prop.Name)] = prop.GetValue(obj);
        }
        return dict;
    }

    private static object? Prop(object obj, string name)
    {
        return obj.GetType().GetProperty(name)?.GetValue(obj);
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }
}
