using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public class AttentionServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private ProjectRepository _projects = null!;
    private TaskRepository _tasks = null!;
    private MessageRepository _messages = null!;
    private ReviewRoundRepository _rounds = null!;
    private ReviewFindingRepository _findings = null!;
    private AgentStreamRepository _stream = null!;
    private AgentRunRepository _runs = null!;
    private DispatchRepository _dispatches = null!;
    private AttentionService _attention = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _projects = new ProjectRepository(_testDb.Db);
        _tasks = new TaskRepository(_testDb.Db);
        _messages = new MessageRepository(_testDb.Db);
        _rounds = new ReviewRoundRepository(_testDb.Db);
        _findings = new ReviewFindingRepository(_testDb.Db);
        _stream = new AgentStreamRepository(_testDb.Db);
        _runs = new AgentRunRepository(_testDb.Db);
        _dispatches = new DispatchRepository(_testDb.Db);
        _attention = new AttentionService(_testDb.Db)
        {
            UtcNow = () => new DateTime(2026, 4, 26, 10, 0, 0, DateTimeKind.Utc),
            StaleRunThreshold = TimeSpan.FromMinutes(15)
        };

        await _projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
        await _projects.CreateAsync(new Project { Id = "other", Name = "Other" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task ListAsync_DerivesAttentionItemsFromExistingDurableFacts()
    {
        var blockedTask = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Blocked migration",
            Status = DenMcp.Core.Models.TaskStatus.Blocked
        });
        var hostTask = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Run host"
        });
        var reviewTask = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Review target",
            Status = DenMcp.Core.Models.TaskStatus.Review
        });

        var question = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = hostTask.Id,
            Sender = "user",
            Content = "Which path should we take?",
            Intent = MessageIntent.Question
        });
        var answeredQuestion = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = hostTask.Id,
            Sender = "user",
            Content = "Already answered?",
            Intent = MessageIntent.Question
        });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = hostTask.Id,
            ThreadId = answeredQuestion.Id,
            Sender = "pi",
            Content = "Yes.",
            Intent = MessageIntent.Answer
        });

        var (dispatch, _) = await _dispatches.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = "proj",
            TargetAgent = "pi",
            TriggerType = DispatchTriggerType.Message,
            TriggerId = question.Id,
            TaskId = hostTask.Id,
            Summary = "Legacy dispatch still pending",
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, question.Id, "pi"),
            ExpiresAt = DateTime.Parse("2026-04-26T11:00:00Z")
        });

        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = reviewTask.Id,
            RequestedBy = "pi",
            Branch = "task/review-target",
            BaseBranch = "main",
            BaseCommit = "base-sha",
            HeadCommit = "head-sha"
        });
        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "reviewer",
            Category = ReviewFindingCategory.AcceptanceGap,
            Summary = "Missing required operator state"
        });

        await AppendAndProjectAsync(hostTask.Id, "failed-run", "coder", "subagent_started", """{"run_id":"failed-run","role":"coder","started_at":"2026-04-26T09:00:00Z"}""");
        await AppendAndProjectAsync(hostTask.Id, "failed-run", "coder", "subagent_failed", """{"run_id":"failed-run","role":"coder","ended_at":"2026-04-26T09:01:00Z","infrastructure_failure_reason":"child_error"}""");

        await AppendAndProjectAsync(hostTask.Id, "stale-run", "planner", "subagent_started", """{"run_id":"stale-run","role":"planner","started_at":"2026-04-26T09:30:00Z"}""");

        await _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_rerun_unavailable",
            ProjectId = "proj",
            TaskId = hostTask.Id,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "Cannot rerun from this live session.",
            Metadata = Metadata("""{"run_id":"rerun-source"}""")
        });

        await AppendAndProjectAsync(reviewTask.Id, "reviewer-failed", "reviewer", "subagent_started",
            $$"""{"run_id":"reviewer-failed","role":"reviewer","review_round_id":{{round.Id}},"started_at":"2026-04-26T09:10:00Z"}""");
        await AppendAndProjectAsync(reviewTask.Id, "reviewer-failed", "reviewer", "subagent_failed",
            $$"""{"run_id":"reviewer-failed","role":"reviewer","review_round_id":{{round.Id}},"ended_at":"2026-04-26T09:12:00Z"}""");

        var items = await _attention.ListAsync(new AttentionListOptions { ProjectId = "proj", Limit = 50 });

        Assert.Contains(items, item => item.Kind == "pending_dispatch" && item.DispatchId == dispatch.Id);
        Assert.Contains(items, item => item.Kind == "subagent_run_problem" && item.RunId == "failed-run");
        Assert.Contains(items, item => item.Kind == "stale_subagent_run" && item.RunId == "stale-run");
        Assert.Contains(items, item => item.Kind == "subagent_rerun_unavailable" && item.RunId == "rerun-source");
        Assert.Contains(items, item => item.Kind == "blocked_task" && item.TaskId == blockedTask.Id);
        Assert.Contains(items, item => item.Kind == "question_message" && item.MessageId == question.Id);
        Assert.DoesNotContain(items, item => item.MessageId == answeredQuestion.Id);
        Assert.Contains(items, item => item.Kind == "open_review_finding" && item.ReviewRoundId == round.Id && item.Summary == finding.Summary);
        Assert.Contains(items, item => item.Kind == "pending_review_reviewer_failed" && item.ReviewRoundId == round.Id && item.RunId == "reviewer-failed");
        Assert.All(items, item => Assert.Equal("proj", item.ProjectId));
        Assert.True(items.TakeWhile(item => item.Severity == "critical").Any(), "critical items should sort before lower severity items");
    }

    [Fact]
    public async Task ListAsync_FiltersByProjectTaskKindAndSeverity()
    {
        var projTask = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Blocked here",
            Status = DenMcp.Core.Models.TaskStatus.Blocked
        });
        var otherTask = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "other",
            Title = "Blocked elsewhere",
            Status = DenMcp.Core.Models.TaskStatus.Blocked
        });

        var projectItems = await _attention.ListAsync(new AttentionListOptions { ProjectId = "proj" });
        Assert.Contains(projectItems, item => item.TaskId == projTask.Id);
        Assert.DoesNotContain(projectItems, item => item.TaskId == otherTask.Id);

        var filtered = await _attention.ListAsync(new AttentionListOptions
        {
            ProjectId = "proj",
            TaskId = projTask.Id,
            Kind = "blocked_task",
            Severity = "warning",
            Limit = 5
        });

        var item = Assert.Single(filtered);
        Assert.Equal($"task:{projTask.Id}:blocked", item.Id);
        Assert.Equal("Resolve dependencies, update the task with the blocker, or unblock it if the blocker is gone.", item.SuggestedAction);
    }

    private async Task AppendAndProjectAsync(int taskId, string runId, string role, string eventType, string metadataJson)
    {
        var entry = await _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = "proj",
            TaskId = taskId,
            Sender = "pi",
            SenderInstanceId = $"pi-{role}-{runId}",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = $"{role} {eventType}",
            Metadata = Metadata(metadataJson)
        });
        Assert.True(await _runs.UpsertFromStreamEntryAsync(entry));
    }

    private static JsonElement Metadata(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
