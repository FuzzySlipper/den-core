using System.Text.Json;
using DenCore.Models;
using DenCore.Service.Tools;
using TaskStatus = DenCore.Models.TaskStatus;

namespace DenCore.Service.Tests;

/// <summary>
/// Direct tool-level tests for ConciseReadResponse type-aware projections
/// and the named surfaces that validator #11319 flagged.
/// </summary>
public class McpConciseReadResponseTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ── ShrinkTask ──────────────────────────────────────────────────────

    [Fact]
    public void ShrinkTask_PreservesActionableTaskFields()
    {
        var task = new ProjectTask
        {
            Id = 42,
            ProjectId = "den-mcp",
            Title = "Implement concise defaults",
            Status = TaskStatus.InProgress,
            Priority = 2,
            AssignedTo = "spawned-coder",
            Tags = new List<string> { "core", "mcp" },
            ParentId = 10,
            CreatedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 6, 5, 14, 30, 0, DateTimeKind.Utc),
        };

        var result = ConciseReadResponse.ShrinkTask(task);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.Equal("den-mcp", root.GetProperty("project_id").GetString());
        Assert.Equal("Implement concise defaults", root.GetProperty("title").GetString());
        Assert.Equal("InProgress", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("priority").GetInt32());
        Assert.Equal("spawned-coder", root.GetProperty("assigned_to").GetString());
        Assert.Equal(10, root.GetProperty("parent_id").GetInt32());
        Assert.True(root.TryGetProperty("tags", out var tags));
        Assert.Equal(2, tags.GetArrayLength());
        Assert.True(root.GetProperty("deep_read_hint").GetString()?.Contains("verbose=true") == true);

        // Verify description (full detail) is NOT present
        Assert.False(root.TryGetProperty("description", out _));
        Assert.False(root.TryGetProperty("recent_messages", out _));
    }

    [Fact]
    public void ShrinkTaskList_ReturnsWrappedItemsAndCount()
    {
        var tasks = new List<ProjectTask>
        {
            new() { Id = 1, ProjectId = "p", Title = "T1", Status = TaskStatus.Planned },
            new() { Id = 2, ProjectId = "p", Title = "T2", Status = TaskStatus.InProgress },
        };

        var result = ConciseReadResponse.ShrinkTaskList(tasks);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(1, items[0].GetProperty("id").GetInt32());
        Assert.Equal("T1", items[0].GetProperty("title").GetString());
        Assert.Equal("T2", items[1].GetProperty("title").GetString());

        // Must NOT have message-specific fields (content_preview)
        Assert.False(items[0].TryGetProperty("content_preview", out _));
        Assert.False(items[0].TryGetProperty("sender", out _));
    }

    // ── ShrinkReviewRoundList ───────────────────────────────────────────

    [Fact]
    public void ShrinkReviewRoundList_PreservesBranchCommitVerdictFields()
    {
        var rounds = new List<ReviewRound>
        {
            new()
            {
                Id = 1, TaskId = 2001, RoundNumber = 1,
                RequestedBy = "runner",
                Branch = "task/2001-concise", BaseBranch = "main",
                BaseCommit = "abc123", HeadCommit = "def456",
                LastReviewedHeadCommit = null,
                Verdict = ReviewVerdict.LooksGood,
                RequestedAt = DateTime.UtcNow, VerdictAt = DateTime.UtcNow,
            },
            new()
            {
                Id = 2, TaskId = 2001, RoundNumber = 2,
                RequestedBy = "runner",
                Branch = "task/2001-concise", BaseBranch = "main",
                BaseCommit = "def456", HeadCommit = "fff999",
                LastReviewedHeadCommit = "def456",
                RequestedAt = DateTime.UtcNow,
            },
        };

        var result = ConciseReadResponse.ShrinkReviewRoundList(rounds);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        var r1 = items[0];
        Assert.Equal(1, r1.GetProperty("id").GetInt32());
        Assert.Equal("LooksGood", r1.GetProperty("verdict").GetString());
        Assert.Equal("abc123", r1.GetProperty("base_commit").GetString());
        Assert.Equal("def456", r1.GetProperty("head_commit").GetString());
        Assert.Equal("task/2001-concise", r1.GetProperty("branch").GetString());
        Assert.True(r1.GetProperty("deep_read_hint").GetString()?.Contains("verbose=true") == true);

        // Must NOT have message-specific fields
        Assert.False(r1.TryGetProperty("content_preview", out _));
        Assert.False(r1.TryGetProperty("sender", out _));

        // Second round has no verdict
        var r2 = items[1];
        Assert.Equal(JsonValueKind.Null, r2.GetProperty("verdict").ValueKind);
    }

    // ── ShrinkReviewFindingList ─────────────────────────────────────────

    [Fact]
    public void ShrinkReviewFindingList_PreservesCategorySummaryStatusFields()
    {
        var findings = new List<ReviewFinding>
        {
            new()
            {
                Id = 1, FindingKey = "F-2001-1-1", TaskId = 2001,
                ReviewRoundId = 10, ReviewRoundNumber = 1, FindingNumber = 1,
                CreatedBy = "pool-validator-03",
                Category = ReviewFindingCategory.AcceptanceGap,
                Summary = "Generic items shrink is message-specific",
                Status = ReviewFindingStatus.Open,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = 2, FindingKey = "F-2001-1-2", TaskId = 2001,
                ReviewRoundId = 10, ReviewRoundNumber = 1, FindingNumber = 2,
                CreatedBy = "pool-validator-03",
                Category = ReviewFindingCategory.TestWeakness,
                Summary = "Direct tool-level tests absent",
                Status = ReviewFindingStatus.ClaimedFixed,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
        };

        var result = ConciseReadResponse.ShrinkReviewFindingList(findings);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        var f1 = items[0];
        Assert.Equal(1, f1.GetProperty("id").GetInt32());
        Assert.Equal("F-2001-1-1", f1.GetProperty("finding_key").GetString());
        Assert.Equal("AcceptanceGap", f1.GetProperty("category").GetString());
        Assert.Equal("Generic items shrink is message-specific", f1.GetProperty("summary").GetString());
        Assert.Equal("Open", f1.GetProperty("status").GetString());
        Assert.True(f1.GetProperty("deep_read_hint").GetString()?.Contains("verbose=true") == true);

        // Must NOT have message-specific fields
        Assert.False(f1.TryGetProperty("content_preview", out _));
        Assert.False(f1.TryGetProperty("sender", out _));
        // Must NOT have full-detail fields (notes)
        Assert.False(f1.TryGetProperty("notes", out _));
        Assert.False(f1.TryGetProperty("file_references", out _));
    }

    // ── ShrinkMessage fallback to PascalCase ────────────────────────────

    [Fact]
    public void ShrinkMessage_ReadsContentPropertyFromCSharpMessage()
    {
        var msg = new Message
        {
            Id = 99,
            ProjectId = "p",
            Sender = "agent-1",
            Content = "Hello world! This is a test message with enough content to check truncation.",
            TaskId = 2001,
            ThreadId = 50,
            CreatedAt = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc),
        };

        var result = ConciseReadResponse.ShrinkMessage(msg);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        Assert.Equal(99, root.GetProperty("id").GetInt32());
        Assert.Equal("agent-1", root.GetProperty("sender").GetString());
        Assert.Equal(2001, root.GetProperty("task_id").GetInt32());
        Assert.Equal(50, root.GetProperty("thread_id").GetInt32());
        Assert.True(root.GetProperty("content_preview").GetString()?.Length > 0);
        Assert.True(root.GetProperty("content_chars").GetInt32() > 0);
        Assert.False(root.GetProperty("content_truncated").GetBoolean()); // short message
    }

    [Fact]
    public void ShrinkMessage_TruncatesLongContent()
    {
        var longContent = new string('x', 1200);
        var msg = new Message
        {
            Id = 1, ProjectId = "p", Sender = "a",
            Content = longContent,
            CreatedAt = DateTime.UtcNow,
        };

        var result = ConciseReadResponse.ShrinkMessage(msg);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        Assert.True(root.GetProperty("content_truncated").GetBoolean());
        var preview = root.GetProperty("content_preview").GetString();
        Assert.NotNull(preview);
        Assert.True(preview.Length <= 500);
    }

    // ── ShrinkMessages bounds to MaxRecentMessages ──────────────────────

    [Fact]
    public void ShrinkMessages_BoundsToFiveMax()
    {
        var msgs = new List<Message>();
        for (int i = 0; i < 10; i++)
        {
            msgs.Add(new Message
            {
                Id = i + 1, ProjectId = "p", Sender = "a",
                Content = $"Message {i + 1}",
                CreatedAt = DateTime.UtcNow,
            });
        }

        var result = ConciseReadResponse.ShrinkMessages(msgs);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(5, doc.RootElement.GetArrayLength());
    }

    // ── GetThread root bounded ──────────────────────────────────────────

    [Fact]
    public void ShrinkMessage_OnThreadRoot_PreservesIdAndContentPreview()
    {
        var root = new Message
        {
            Id = 11315,
            ProjectId = "den-mcp",
            Sender = "den-mcp-runner",
            Content = "# Coder Context Packet\n\nLong content here...",
            TaskId = 2001,
            CreatedAt = DateTime.UtcNow,
        };

        var result = ConciseReadResponse.ShrinkMessage(root);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var r = doc.RootElement;
        Assert.Equal(11315, r.GetProperty("id").GetInt32());
        Assert.Equal("den-mcp-runner", r.GetProperty("sender").GetString());
        Assert.True(r.GetProperty("content_preview").GetString()?.Length > 0);
        // Full body is NOT present
        Assert.False(r.TryGetProperty("content", out _));
        Assert.False(r.TryGetProperty("body", out _));
    }

    // ── Deep read hint presence ─────────────────────────────────────────

    [Fact]
    public void ShrinkTask_HasDeepReadHint()
    {
        var task = new ProjectTask
        {
            Id = 1, ProjectId = "p", Title = "T",
            Status = TaskStatus.Planned,
        };
        var result = ConciseReadResponse.ShrinkTask(task);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("deep_read_hint").GetString()?.Length > 0);
    }

    [Fact]
    public void ShrinkTaskList_EveryItemHasDeepReadHint()
    {
        var tasks = new List<ProjectTask>
        {
            new() { Id = 1, ProjectId = "p", Title = "T1", Status = TaskStatus.Planned },
            new() { Id = 2, ProjectId = "p", Title = "T2", Status = TaskStatus.Planned },
        };
        var result = ConciseReadResponse.ShrinkTaskList(tasks);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.True(item.GetProperty("deep_read_hint").GetString()?.Length > 0);
        }
    }

    [Fact]
    public void ShrinkReviewRoundList_EveryItemHasDeepReadHint()
    {
        var rounds = new List<ReviewRound>
        {
            new()
            {
                Id = 1, TaskId = 1, RoundNumber = 1,
                RequestedBy = "r", Branch = "b", BaseBranch = "main",
                BaseCommit = "abc", HeadCommit = "def",
                RequestedAt = DateTime.UtcNow,
            },
        };
        var result = ConciseReadResponse.ShrinkReviewRoundList(rounds);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.True(item.GetProperty("deep_read_hint").GetString()?.Length > 0);
    }

    [Fact]
    public void ShrinkReviewFindingList_EveryItemHasDeepReadHint()
    {
        var findings = new List<ReviewFinding>
        {
            new()
            {
                Id = 1, FindingKey = "K", TaskId = 1,
                ReviewRoundId = 1, ReviewRoundNumber = 1, FindingNumber = 1,
                CreatedBy = "v", Category = ReviewFindingCategory.AcceptanceGap,
                Summary = "S", Status = ReviewFindingStatus.Open,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
        };
        var result = ConciseReadResponse.ShrinkReviewFindingList(findings);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.True(item.GetProperty("deep_read_hint").GetString()?.Length > 0);
    }

    // ── Null safety ─────────────────────────────────────────────────────

    [Fact]
    public void ShrinkTask_Null_ReturnsEmptyObject()
    {
        var result = ConciseReadResponse.ShrinkTask(null!);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        Assert.Contains("{}", json);
    }

    [Fact]
    public void ShrinkMessage_Null_ReturnsEmptyObject()
    {
        var result = ConciseReadResponse.ShrinkMessage(null!);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        Assert.Contains("{}", json);
    }

    [Fact]
    public void ShrinkTaskList_EmptyList_ReturnsZeroCount()
    {
        var result = ConciseReadResponse.ShrinkTaskList(new List<ProjectTask>());
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    // ── Concise output is bounded (no full bodies) ──────────────────────

    [Fact]
    public void ShrinkTask_DoesNotExposeDescription()
    {
        var task = new ProjectTask
        {
            Id = 1, ProjectId = "p", Title = "T",
            Description = "A very long description that should not appear in concise output...",
            Status = TaskStatus.Planned,
        };
        var result = ConciseReadResponse.ShrinkTask(task);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("description", out _));
    }

    [Fact]
    public void ShrinkReviewFindingList_DoesNotExposeNotes()
    {
        var findings = new List<ReviewFinding>
        {
            new()
            {
                Id = 1, FindingKey = "K", TaskId = 1,
                ReviewRoundId = 1, ReviewRoundNumber = 1, FindingNumber = 1,
                CreatedBy = "v", Category = ReviewFindingCategory.AcceptanceGap,
                Summary = "S", Status = ReviewFindingStatus.Open,
                Notes = "Detailed notes that should not appear in concise output",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
        };
        var result = ConciseReadResponse.ShrinkReviewFindingList(findings);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.False(item.TryGetProperty("notes", out _));
    }
}
