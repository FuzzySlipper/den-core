using System.Text.Json;
using DenCore.Models;
using DenCore.Service.Tools;

namespace DenCore.Service.Tests;

/// <summary>
/// Tests for concise MCP read-response projections (#2001).
/// Verifies concise defaults are bounded and verbose paths expose full details.
/// </summary>
public sealed class ConciseReadResponseTests
{
    [Fact]
    public void Shrink_WorkerRunList_ProducesConciseProjection()
    {
        var workers = new[]
        {
            new
            {
                run_id = "piw-test-1",
                assignment_id = 1,
                project_id = "test",
                task_id = 100,
                role = "coder",
                state = "running",
                status = "running",
                worker_identity = "pi-abc",
                assigned_by = "runner",
                updated_at = "2026-06-06T00:00:00Z",
                created_at = "2026-06-06T00:00:00Z",
                substrate = "external",
                launch_metadata = new { host = "den-k8", workdir = "/tmp", profile = "test", timeout_seconds = 600 },
                requested_repo = new { branch = "task/100", base_branch = "main", base_commit = "abc123" },
            },
        };

        var payload = new { worker_runs = workers, count = workers.Length, summary = "listed 1" };
        var shrunk = ConciseReadResponse.Shrink(payload);

        var json = JsonSerializer.Serialize(shrunk);
        using var doc = JsonDocument.Parse(json);

        var runs = doc.RootElement.GetProperty("worker_runs");
        Assert.Equal(1, runs.GetArrayLength());

        var first = runs[0];
        Assert.Equal("piw-test-1", first.GetProperty("run_id").GetString());
        Assert.True(first.TryGetProperty("deep_read_hint", out _));

        // Launch metadata must be absent from concise projection
        Assert.False(first.TryGetProperty("launch_metadata", out _));
        Assert.False(first.TryGetProperty("requested_repo", out _));

        // Output must be reasonably bounded
        Assert.True(json.Length < 2000, $"Concise output {json.Length} chars exceeds 2000 bound");
    }

    [Fact]
    public void Shrink_WorkerRun_WithVerbose_PreservesFullDetail()
    {
        var worker = new
        {
            run_id = "piw-full",
            assignment_id = 2,
            project_id = "test",
            task_id = 200,
            role = "reviewer",
            state = "completed",
            status = "completed",
            worker_identity = "pi-def",
            assigned_by = "planner",
            updated_at = "2026-06-06T00:00:00Z",
            created_at = "2026-06-06T00:00:00Z",
            substrate = "external",
            launch_metadata = new { host = "den-k8", workdir = "/work", profile = "test", timeout_seconds = 300 },
            requested_repo = new { branch = "task/200", base_branch = "main", base_commit = "def456" },
        };

        // Simulate verbose path — direct serialization without Shrink
        var verboseJson = JsonSerializer.Serialize(worker);
        using var doc = JsonDocument.Parse(verboseJson);

        // Verbose output retains all fields
        Assert.Equal("piw-full", doc.RootElement.GetProperty("run_id").GetString());
        Assert.True(doc.RootElement.TryGetProperty("launch_metadata", out _));
        Assert.True(doc.RootElement.TryGetProperty("requested_repo", out _));
    }

    [Fact]
    public void ContentPreview_LongContent_TruncatesWithHint()
    {
        var longContent = new string('x', 5000);
        var preview = ConciseReadResponse.ContentPreview(longContent);
        var json = JsonSerializer.Serialize(preview);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("content_truncated").GetBoolean());
        Assert.Equal(5000, doc.RootElement.GetProperty("content_chars").GetInt32());
        var previewText = doc.RootElement.GetProperty("content_preview").GetString();
        Assert.NotNull(previewText);
        Assert.Equal(500, previewText.Length);
        Assert.True(doc.RootElement.TryGetProperty("deep_read_hint", out _));
    }

    [Fact]
    public void ContentPreview_ShortContent_NotTruncated()
    {
        var shortContent = "Hello, world!";
        var preview = ConciseReadResponse.ContentPreview(shortContent);
        var json = JsonSerializer.Serialize(preview);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("content_truncated").GetBoolean());
        Assert.Equal(13, doc.RootElement.GetProperty("content_chars").GetInt32());
        Assert.Equal("Hello, world!", doc.RootElement.GetProperty("content_preview").GetString());
        Assert.True(doc.RootElement.TryGetProperty("deep_read_hint", out var hint));
        Assert.Equal(JsonValueKind.Null, hint.ValueKind);
    }

    [Fact]
    public void Shrink_PoolMemberList_ProducesConciseProjection()
    {
        var members = new[]
        {
            new
            {
                worker_identity = "pi-01",
                worker_role = "coder",
                status = "available",
                profile_identity = "spawned-coder",
                last_heartbeat = "2026-06-06T00:00:00Z",
                updated_at = "2026-06-06T00:00:00Z",
                active_assignment_count = 2,
                // These should be stripped in concise mode
                extended_status = "ready",
                capabilities = new[] { "dotnet", "sqlite" },
            },
        };

        var payload = new { pool_members = members, count = 1 };
        var shrunk = ConciseReadResponse.Shrink(payload);
        var json = JsonSerializer.Serialize(shrunk);

        using var doc = JsonDocument.Parse(json);
        var pms = doc.RootElement.GetProperty("pool_members");
        Assert.Equal(1, pms.GetArrayLength());

        var first = pms[0];
        Assert.Equal("pi-01", first.GetProperty("worker_identity").GetString());
        Assert.True(first.TryGetProperty("deep_read_hint", out _));
        Assert.False(first.TryGetProperty("extended_status", out _));
        Assert.False(first.TryGetProperty("capabilities", out _));
    }

    [Fact]
    public void Shrink_MessageList_ProducesBoundedPreviews()
    {
        var messages = new List<object>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(new
            {
                id = i + 1,
                sender = $"agent-{i}",
                task_id = 100,
                thread_id = (int?)null,
                created_at = "2026-06-06T00:00:00Z",
                body = new string('m', 3000),
                full_metadata = new { delivery = "ok", raw_prompt = new string('p', 10000) },
            });
        }

        var payload = new { items = messages, count = 10 };
        var shrunk = ConciseReadResponse.Shrink(payload);
        var json = JsonSerializer.Serialize(shrunk);

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        // Should cap at MaxRecentMessages (5)
        Assert.True(items.GetArrayLength() <= 5);

        var first = items[0];
        Assert.Equal(1, first.GetProperty("id").GetInt32());
        Assert.True(first.GetProperty("content_truncated").GetBoolean());
        Assert.Equal(3000, first.GetProperty("content_chars").GetInt32());

        var preview = first.GetProperty("content_preview").GetString();
        Assert.NotNull(preview);
        Assert.Equal(500, preview!.Length);

        // Full metadata absent
        Assert.False(first.TryGetProperty("full_metadata", out _));
    }

    [Fact]
    public void Shrink_SearchResults_HasDeepReadHint()
    {
        var results = new[]
        {
            new
            {
                project_id = "test",
                slug = "test-doc",
                title = "Test Document",
                doc_type = "spec",
                snippet = "matching text...",
                full_content = new string('x', 10000),
                raw_metadata = new { is_internal = true },
            },
        };

        var payload = new { results, count = 1 };
        var shrunk = ConciseReadResponse.Shrink(payload);
        var json = JsonSerializer.Serialize(shrunk);

        using var doc = JsonDocument.Parse(json);
        var res = doc.RootElement.GetProperty("results");
        Assert.Equal(1, res.GetArrayLength());

        var first = res[0];
        Assert.Equal("test", first.GetProperty("project_id").GetString());
        Assert.Equal("test-doc", first.GetProperty("slug").GetString());
        Assert.True(first.TryGetProperty("deep_read_hint", out _));
        Assert.False(first.TryGetProperty("full_content", out _));
    }

    [Fact]
    public void Shrink_DocumentLists_PreserveMetadataFromPascalCaseModels()
    {
        var docs = new[]
        {
            new DocumentSummary
            {
                Id = 7,
                ProjectId = "proj",
                Slug = "doc-slug",
                Title = "Document Slug",
                DocType = DocType.Reference,
                Visibility = DocumentVisibility.Normal,
                Summary = "Visible summary",
                Tags = ["postgres", "documents"],
                UpdatedAt = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc)
            },
        };

        var payload = new { documents = docs, count = docs.Length };
        var shrunk = ConciseReadResponse.Shrink(payload);
        var json = JsonSerializer.Serialize(shrunk);

        using var doc = JsonDocument.Parse(json);
        var documents = doc.RootElement.GetProperty("documents");
        var first = documents[0];

        Assert.Equal("proj", first.GetProperty("project_id").GetString());
        Assert.Equal("doc-slug", first.GetProperty("slug").GetString());
        Assert.Equal("Document Slug", first.GetProperty("title").GetString());
        Assert.Equal("reference", first.GetProperty("doc_type").GetString());
        Assert.Equal("normal", first.GetProperty("visibility").GetString());
        Assert.Equal("Visible summary", first.GetProperty("summary").GetString());
        Assert.Equal("postgres", first.GetProperty("tags")[0].GetString());
        Assert.True(first.TryGetProperty("deep_read_hint", out _));
    }

    [Fact]
    public void Shrink_NullInput_HandlesGracefully()
    {
        var result = ConciseReadResponse.Shrink(null!);
        Assert.NotNull(result);
    }

    [Fact]
    public void Shrink_UnknownShape_ReturnsUnchanged()
    {
        var obj = new { custom_field = "value", data = 42 };
        var result = ConciseReadResponse.Shrink(obj);

        // Should not throw; unknown shapes pass through
        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("custom_field", json);
    }
}
