using System.Text.Json;
using DenCore.Data;
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

    // ── ShrinkTaskDetail (get_task concise path) ────────────────────────

    [Fact]
    public void ShrinkTaskDetail_BoundsDescriptionToContentPreview()
    {
        var longDesc = new string('D', 1200);
        var detail = new TaskDetail
        {
            Task = new ProjectTask
            {
                Id = 1, ProjectId = "den-mcp", Title = "Long-desc task",
                Status = TaskStatus.InProgress, Priority = 2,
                Description = longDesc,
            },
            Dependencies = new List<TaskDependencyInfo>(),
            Subtasks = new List<TaskSummary>(),
            RecentMessages = new List<Message>(),
            ReviewRounds = new List<ReviewRound>(),
            OpenReviewFindings = new List<ReviewFinding>(),
            ResolvedReviewFindings = new List<ReviewFinding>(),
            ReviewWorkflow = new ReviewWorkflowSummary
            {
                Timeline = new List<ReviewTimelineEntry>(),
            },
        };

        var result = ConciseReadResponse.ShrinkTaskDetail(detail);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;

        // Description is NOT a raw string — it's a preview object
        Assert.Equal(JsonValueKind.Object, root.GetProperty("description").ValueKind);
        var desc = root.GetProperty("description");
        Assert.True(desc.GetProperty("content_truncated").GetBoolean());
        Assert.Equal(longDesc.Length, desc.GetProperty("content_chars").GetInt32());
        var preview = desc.GetProperty("content_preview").GetString();
        Assert.NotNull(preview);
        Assert.True(preview.Length <= 500);

        // Full description text should NOT be accessible
        Assert.False(root.TryGetProperty("Description", out _));
    }

    [Fact]
    public void ShrinkTaskDetail_NoDescription_ReturnsNullDescription()
    {
        var detail = new TaskDetail
        {
            Task = new ProjectTask
            {
                Id = 2, ProjectId = "p", Title = "No desc",
                Status = TaskStatus.Planned,
                Description = null,
            },
            Dependencies = new List<TaskDependencyInfo>(),
            Subtasks = new List<TaskSummary>(),
            RecentMessages = new List<Message>(),
            ReviewRounds = new List<ReviewRound>(),
            OpenReviewFindings = new List<ReviewFinding>(),
            ResolvedReviewFindings = new List<ReviewFinding>(),
            ReviewWorkflow = new ReviewWorkflowSummary
            {
                Timeline = new List<ReviewTimelineEntry>(),
            },
        };

        var result = ConciseReadResponse.ShrinkTaskDetail(detail);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("description").ValueKind);
    }

    [Fact]
    public void ShrinkTaskDetail_BoundsRecentMessages()
    {
        var msgs = new List<Message>();
        for (int i = 0; i < 10; i++)
        {
            msgs.Add(new Message
            {
                Id = i + 100, ProjectId = "p", Sender = "agent",
                Content = $"Long message body {i}: " + new string('x', 800),
                CreatedAt = DateTime.UtcNow,
            });
        }

        var detail = new TaskDetail
        {
            Task = new ProjectTask
            {
                Id = 3, ProjectId = "p", Title = "T",
                Status = TaskStatus.InProgress,
            },
            Dependencies = new List<TaskDependencyInfo>(),
            Subtasks = new List<TaskSummary>(),
            RecentMessages = msgs,
            ReviewRounds = new List<ReviewRound>(),
            OpenReviewFindings = new List<ReviewFinding>(),
            ResolvedReviewFindings = new List<ReviewFinding>(),
            ReviewWorkflow = new ReviewWorkflowSummary
            {
                Timeline = new List<ReviewTimelineEntry>(),
            },
        };

        var result = ConciseReadResponse.ShrinkTaskDetail(detail);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var recentMessages = doc.RootElement.GetProperty("recent_messages");
        Assert.True(recentMessages.GetArrayLength() <= 5);

        foreach (var msg in recentMessages.EnumerateArray())
        {
            // Each message should have content_preview, not raw Content
            Assert.True(msg.TryGetProperty("content_preview", out _));
            Assert.True(msg.GetProperty("content_truncated").GetBoolean());
        }
    }

    [Fact]
    public void ShrinkTaskDetail_IncludesDependenciesAndSubtasks()
    {
        var detail = new TaskDetail
        {
            Task = new ProjectTask
            {
                Id = 4, ProjectId = "p", Title = "Parent",
                Status = TaskStatus.InProgress,
            },
            Dependencies = new List<TaskDependencyInfo>
            {
                new() { TaskId = 10, Title = "Blocking task", Status = TaskStatus.InProgress },
                new() { TaskId = 11, Title = "Other dep", Status = TaskStatus.Done },
            },
            Subtasks = new List<TaskSummary>
            {
                new() { Id = 20, Title = "Sub A", Status = TaskStatus.InProgress, Priority = 1, ProjectId = "p" },
                new() { Id = 21, Title = "Sub B", Status = TaskStatus.Planned, Priority = 2, ProjectId = "p" },
            },
            RecentMessages = new List<Message>(),
            ReviewRounds = new List<ReviewRound>(),
            OpenReviewFindings = new List<ReviewFinding>(),
            ResolvedReviewFindings = new List<ReviewFinding>(),
            ReviewWorkflow = new ReviewWorkflowSummary
            {
                Timeline = new List<ReviewTimelineEntry>(),
            },
        };

        var result = ConciseReadResponse.ShrinkTaskDetail(detail);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var deps = doc.RootElement.GetProperty("dependencies");
        Assert.Equal(2, deps.GetArrayLength());
        Assert.Equal(10, deps[0].GetProperty("task_id").GetInt32());
        Assert.Equal("Blocking task", deps[0].GetProperty("title").GetString());

        var subs = doc.RootElement.GetProperty("subtasks");
        Assert.Equal(2, subs.GetArrayLength());
        Assert.Equal(20, subs[0].GetProperty("id").GetInt32());
        Assert.Equal("Sub A", subs[0].GetProperty("title").GetString());
    }

    [Fact]
    public void ShrinkTaskDetail_IncludesReviewWorkflowCounts()
    {
        var detail = new TaskDetail
        {
            Task = new ProjectTask
            {
                Id = 5, ProjectId = "p", Title = "T",
                Status = TaskStatus.Review,
            },
            Dependencies = new List<TaskDependencyInfo>(),
            Subtasks = new List<TaskSummary>(),
            RecentMessages = new List<Message>(),
            ReviewRounds = new List<ReviewRound>
            {
                new()
                {
                    Id = 1, TaskId = 5, RoundNumber = 1,
                    RequestedBy = "runner", Branch = "b", BaseBranch = "main",
                    BaseCommit = "abc", HeadCommit = "def",
                    Verdict = ReviewVerdict.ChangesRequested,
                    RequestedAt = DateTime.UtcNow,
                },
            },
            OpenReviewFindings = new List<ReviewFinding>
            {
                new()
                {
                    Id = 1, FindingKey = "F-1", TaskId = 5,
                    ReviewRoundId = 1, ReviewRoundNumber = 1, FindingNumber = 1,
                    CreatedBy = "v", Category = ReviewFindingCategory.AcceptanceGap,
                    Summary = "Gap", Status = ReviewFindingStatus.Open,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                },
                new()
                {
                    Id = 2, FindingKey = "F-2", TaskId = 5,
                    ReviewRoundId = 1, ReviewRoundNumber = 1, FindingNumber = 2,
                    CreatedBy = "v", Category = ReviewFindingCategory.TestWeakness,
                    Summary = "Weak", Status = ReviewFindingStatus.Open,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                },
            },
            ResolvedReviewFindings = new List<ReviewFinding>
            {
                new()
                {
                    Id = 3, FindingKey = "F-3", TaskId = 5,
                    ReviewRoundId = 1, ReviewRoundNumber = 1, FindingNumber = 3,
                    CreatedBy = "v", Category = ReviewFindingCategory.BlockingBug,
                    Summary = "Bug", Status = ReviewFindingStatus.VerifiedFixed,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                },
            },
            ReviewWorkflow = new ReviewWorkflowSummary
            {
                ReviewRoundCount = 1,
                CurrentVerdict = ReviewVerdict.ChangesRequested,
                UnresolvedFindingCount = 2,
                ResolvedFindingCount = 1,
                AddressedFindingCount = 0,
                Timeline = new List<ReviewTimelineEntry>(),
            },
        };

        var result = ConciseReadResponse.ShrinkTaskDetail(detail);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("open_review_findings_count").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("resolved_review_findings_count").GetInt32());

        var rw = doc.RootElement.GetProperty("review_workflow");
        Assert.Equal(1, rw.GetProperty("review_round_count").GetInt32());
        Assert.Equal("ChangesRequested", rw.GetProperty("current_verdict").GetString());
        Assert.Equal(2, rw.GetProperty("unresolved_finding_count").GetInt32());

        var rounds = doc.RootElement.GetProperty("review_rounds");
        Assert.Equal(1, rounds.GetArrayLength());
        Assert.Equal("ChangesRequested", rounds[0].GetProperty("verdict").GetString());
    }

    [Fact]
    public void ShrinkTaskDetail_HasDeepReadHint()
    {
        var detail = new TaskDetail
        {
            Task = new ProjectTask
            {
                Id = 6, ProjectId = "p", Title = "T",
                Status = TaskStatus.Planned,
            },
            Dependencies = new List<TaskDependencyInfo>(),
            Subtasks = new List<TaskSummary>(),
            RecentMessages = new List<Message>(),
            ReviewRounds = new List<ReviewRound>(),
            OpenReviewFindings = new List<ReviewFinding>(),
            ResolvedReviewFindings = new List<ReviewFinding>(),
            ReviewWorkflow = new ReviewWorkflowSummary
            {
                Timeline = new List<ReviewTimelineEntry>(),
            },
        };

        var result = ConciseReadResponse.ShrinkTaskDetail(detail);
        var json = JsonSerializer.Serialize(result, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        var hint = doc.RootElement.GetProperty("deep_read_hint").GetString();
        Assert.NotNull(hint);
        Assert.Contains("verbose=true", hint);
    }

    // ── Direct TaskTools.GetTask tests (fake repo, end-to-end) ──────────

    private sealed class FakeTaskRepo : ITaskRepository
    {
        private readonly TaskDetail _detail;
        public FakeTaskRepo(TaskDetail detail) => _detail = detail;

        public Task<TaskDetail> GetDetailAsync(int id) => Task.FromResult(_detail);
        public Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null) =>
            Task.FromResult(task);
        public Task<ProjectTask?> GetByIdAsync(int id) =>
            Task.FromResult<ProjectTask?>(null);
        public Task<TaskWorkflowSummary> GetWorkflowSummaryAsync(int id) =>
            Task.FromResult(new TaskWorkflowSummary
            {
                Id = id, ProjectId = "test", Title = "Summary", Status = "planned",
                Dependencies = [], Subtasks = [],
                ReviewWorkflow = new CompactReviewWorkflow { Timeline = [] },
                RecentMessages = [], UnresolvedFindings = [],
                DeepReadHint = "", Availability = "available",
            });
        public Task<List<TaskSummary>> ListAsync(string projectId, DenCore.Models.TaskStatus[]? statuses = null,
            string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null,
            bool includeAll = false) => Task.FromResult(new List<TaskSummary>());
        public Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent) =>
            Task.FromResult(new ProjectTask { Id = id, ProjectId = "test", Title = "Updated", Status = DenCore.Models.TaskStatus.InProgress, Priority = 1 });
        public Task AddDependencyAsync(int taskId, int dependsOn) => Task.CompletedTask;
        public Task RemoveDependencyAsync(int taskId, int dependsOn) => Task.CompletedTask;
        public Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null) =>
            Task.FromResult<ProjectTask?>(null);
    }

    private static TaskDetail CreateSeededTaskDetail(string? desc, List<Message> msgs)
    {
        return new TaskDetail
        {
            Task = new ProjectTask
            {
                Id = 42, ProjectId = "den-mcp",
                Title = "Test task with long description",
                Status = DenCore.Models.TaskStatus.InProgress,
                Priority = 2,
                Description = desc,
            },
            Dependencies = new List<TaskDependencyInfo>(),
            Subtasks = new List<TaskSummary>
            {
                new() { Id = 1, Title = "Sub A", Status = DenCore.Models.TaskStatus.Planned, Priority = 3, ProjectId = "den-mcp" },
            },
            RecentMessages = msgs,
            ReviewRounds = new List<ReviewRound>
            {
                new()
                {
                    Id = 1, TaskId = 42, RoundNumber = 1,
                    RequestedBy = "runner", Branch = "task/2001-concise",
                    BaseBranch = "main", BaseCommit = "abc", HeadCommit = "def",
                    Verdict = ReviewVerdict.ChangesRequested,
                    RequestedAt = DateTime.UtcNow,
                },
            },
            OpenReviewFindings = new List<ReviewFinding>(),
            ResolvedReviewFindings = new List<ReviewFinding>(),
            ReviewWorkflow = new ReviewWorkflowSummary
            {
                ReviewRoundCount = 1,
                CurrentVerdict = ReviewVerdict.ChangesRequested,
                UnresolvedFindingCount = 0,
                ResolvedFindingCount = 0,
                AddressedFindingCount = 0,
                Timeline = new List<ReviewTimelineEntry>(),
            },
        };
    }

    [Fact]
    public async Task GetTask_ConciseDefault_BoundsRecentMessagesAndDescriptions()
    {
        var sentinelDesc = "SENTINEL_DESCRIPTION_" + new string('X', 1200);
        var sentinelBody = "SENTINEL_MESSAGE_BODY_" + new string('Y', 800);
        var msgs = new List<Message>();
        for (int i = 0; i < 10; i++)
            msgs.Add(new Message
            {
                Id = 100 + i, ProjectId = "den-mcp", Sender = "agent",
                Content = $"{sentinelBody} #{i}",
                TaskId = 42, CreatedAt = DateTime.UtcNow,
            });

        var detail = CreateSeededTaskDetail(sentinelDesc, msgs);
        var repo = new FakeTaskRepo(detail);

        var resultJson = await TaskTools.GetTask(repo, task_id: 42, verbose: false);
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        // Task identity preserved
        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.Equal("den-mcp", root.GetProperty("project_id").GetString());

        // Description is a preview object, NOT the raw sentinel string
        Assert.Equal(JsonValueKind.Object, root.GetProperty("description").ValueKind);
        var desc = root.GetProperty("description");
        Assert.True(desc.GetProperty("content_truncated").GetBoolean());
        Assert.True(desc.GetProperty("content_chars").GetInt32() >= sentinelDesc.Length);

        // Raw Description field (PascalCase from C# model) is absent —
        // description is only the preview object
        Assert.False(root.TryGetProperty("Description", out _));

        // Recent messages bounded (max 5), each truncated
        var recentMsgs = root.GetProperty("recent_messages");
        Assert.True(recentMsgs.GetArrayLength() <= 5);
        foreach (var msg in recentMsgs.EnumerateArray())
        {
            Assert.True(msg.TryGetProperty("content_preview", out _));
            Assert.True(msg.GetProperty("content_truncated").GetBoolean());
            // Raw Content/Ccontent fields are absent (only content_preview)
            Assert.False(msg.TryGetProperty("Content", out _));
            Assert.False(msg.TryGetProperty("content", out _));
        }

        // Deep read hint present
        var hint = root.GetProperty("deep_read_hint").GetString();
        Assert.NotNull(hint);
        Assert.Contains("verbose=true", hint);

        // Subtasks included
        var subs = root.GetProperty("subtasks");
        Assert.Equal(1, subs.GetArrayLength());
        Assert.Equal("Sub A", subs[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetTask_VerboseTrue_ReturnsFullDetail()
    {
        var sentinelDesc = "SENTINEL_FULL_DESC_" + new string('Z', 200);
        var sentinelBody = "SENTINEL_FULL_BODY_" + new string('W', 300);
        var msgs = new List<Message>
        {
            new()
            {
                Id = 200, ProjectId = "den-mcp", Sender = "agent",
                Content = sentinelBody,
                TaskId = 42, CreatedAt = DateTime.UtcNow,
            },
        };

        var detail = CreateSeededTaskDetail(sentinelDesc, msgs);
        var repo = new FakeTaskRepo(detail);

        var resultJson = await TaskTools.GetTask(repo, task_id: 42, verbose: true);
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        // Full detail: the outer key is "task" (TaskDetail serialization shape)
        var task = root.GetProperty("task");
        Assert.Equal(42, task.GetProperty("id").GetInt32());

        // Full Description is present as a raw string (not a preview object)
        var rawDesc = task.GetProperty("description").GetString();
        Assert.NotNull(rawDesc);
        Assert.Contains("SENTINEL_FULL_DESC_", rawDesc);

        // Full RecentMessages with raw Content
        var recentMsgs = root.GetProperty("recent_messages");
        Assert.Equal(1, recentMsgs.GetArrayLength());
        var msgContent = recentMsgs[0].GetProperty("content").GetString();
        Assert.NotNull(msgContent);
        Assert.Contains("SENTINEL_FULL_BODY_", msgContent);
    }
}
