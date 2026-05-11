using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Services;

public class DispatchDetectionServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private DispatchDetectionService _detection = null!;
    private DispatchRepository _dispatches = null!;
    private TaskRepository _tasks = null!;
    private MessageRepository _messages = null!;
    private DocumentRepository _docs = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tasks = new TaskRepository(_testDb.Db);
        _messages = new MessageRepository(_testDb.Db);
        _dispatches = new DispatchRepository(_testDb.Db);
        _docs = new DocumentRepository(_testDb.Db);
        var routing = new RoutingService(_docs);
        var prompts = new PromptGenerationService(_tasks, _messages, routing);
        var contexts = new DispatchContextService(_dispatches, _messages, _tasks, routing,
            NullLogger<DispatchContextService>.Instance);
        _detection = new DispatchDetectionService(routing, _dispatches, prompts, contexts, NoOpNotifications.Instance,
            NullLogger<DispatchDetectionService>.Instance);

        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
        await EnableLegacyDispatchRoutingAsync("proj");
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private static readonly JsonSerializerOptions RoutingJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private async Task EnableLegacyDispatchRoutingAsync(string projectId)
    {
        var config = RoutingService.CreateDefaultConfig();
        config.Defaults.LegacyDispatchEnabled = true;
        await _docs.UpsertAsync(new Document
        {
            ProjectId = projectId,
            Slug = "dispatch-routing",
            Title = "Legacy Dispatch Routing",
            Content = JsonSerializer.Serialize(config, RoutingJsonOptions),
            DocType = DocType.Convention
        });
    }

    private sealed class NoOpNotifications : INotificationChannel
    {
        public static NoOpNotifications Instance { get; } = new();

        public Task SendDispatchNotificationAsync(
            DispatchEntry dispatch,
            string summary,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAgentStatusAsync(
            string projectId,
            string agent,
            string status,
            int? taskId = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    #region Legacy dispatch mode

    [Fact]
    public async Task LegacyDispatchDisabledByDefault_SuppressesAutomaticDispatchCreation()
    {
        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "legacy-off", Name = "Legacy Off" });
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "legacy-off", Title = "Review me" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");

        var pending = await _dispatches.ListAsync("legacy-off", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task RoutingDocumentWithoutLegacyDispatchEnabled_SuppressesAutomaticDispatchCreation()
    {
        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "legacy-doc-off", Name = "Legacy Doc Off" });
        var config = RoutingService.CreateDefaultConfig();
        await _docs.UpsertAsync(new Document
        {
            ProjectId = "legacy-doc-off",
            Slug = "dispatch-routing",
            Title = "Dispatch Routing",
            Content = JsonSerializer.Serialize(config, RoutingJsonOptions),
            DocType = DocType.Convention
        });
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "legacy-doc-off", Title = "Review me" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");

        var pending = await _dispatches.ListAsync("legacy-doc-off", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending);
    }

    #endregion

    #region Task status change dispatch

    private async Task<DispatchEntry> CreateDispatchAsync(
        int triggerId,
        int taskId,
        string targetAgent,
        DispatchTriggerType triggerType = DispatchTriggerType.Message)
    {
        var (dispatch, _) = await _dispatches.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = "proj",
            TargetAgent = targetAgent,
            TriggerType = triggerType,
            TriggerId = triggerId,
            TaskId = taskId,
            Summary = $"Dispatch {triggerId}",
            ContextPrompt = "Context",
            DedupKey = DispatchEntry.BuildDedupKey(triggerType, triggerId, targetAgent),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });

        return dispatch;
    }

    [Fact]
    public async Task TaskMovedToReview_CreatesDispatchForReviewer()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Feature X" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "coder");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal("pi", pending[0].TargetAgent); // Default reviewer
        Assert.Equal(task.Id, pending[0].TaskId);
        Assert.Contains("Feature X", pending[0].Summary!);
        Assert.Contains("Feature X", pending[0].ContextPrompt!);
    }

    [Fact]
    public async Task TaskMovedToReview_StoresReviewingActivityHintInDispatchContext()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Feature X" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "coder");

        var pending = Assert.Single(await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]));
        var context = JsonSerializer.Deserialize<DispatchContextSnapshot>(pending.ContextJson!);

        Assert.NotNull(context);
        Assert.Equal("review_request", context!.ContextKind);
        Assert.Equal("reviewing", context.ActivityHint);
    }

    [Fact]
    public async Task TaskMovedFromReviewToPlanned_CreatesDispatchForImplementer()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Bug fix" });

        await _detection.OnTaskStatusChangedAsync(task, "review", "planned", "reviewer-agent");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal("pi", pending[0].TargetAgent); // Default implementer
        Assert.Contains("feedback", pending[0].Summary!);
    }

    [Fact]
    public async Task TaskMovedToDone_NoDispatch()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Done task" });

        await _detection.OnTaskStatusChangedAsync(task, "review", "done", "codex");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // No trigger for review→done in default config
    }

    [Fact]
    public async Task TaskMovedToDone_ExpiresOpenDispatchesForTask()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Cleanup task" });
        var pending = await CreateDispatchAsync(triggerId: 10, taskId: task.Id, targetAgent: "codex");
        var approved = await CreateDispatchAsync(triggerId: 11, taskId: task.Id, targetAgent: "claude-code");
        await _dispatches.ApproveAsync(approved.Id, "user");
        var otherTask = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Other task" });
        var untouched = await CreateDispatchAsync(triggerId: 12, taskId: otherTask.Id, targetAgent: "codex");

        await _detection.OnTaskStatusChangedAsync(task, "review", "done", "codex");

        Assert.Equal(DispatchStatus.Expired, (await _dispatches.GetByIdAsync(pending.Id))!.Status);
        Assert.Equal(DispatchStatus.Expired, (await _dispatches.GetByIdAsync(approved.Id))!.Status);
        Assert.Equal(DispatchStatus.Pending, (await _dispatches.GetByIdAsync(untouched.Id))!.Status);
    }

    [Fact]
    public async Task SameAgentAsSender_NoDispatch()
    {
        // If pi sets a task to review and the reviewer is also pi,
        // don't dispatch to yourself
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Self-review" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "pi");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // Pi is both sender and default reviewer
    }

    #endregion

    #region Message dispatch

    [Fact]
    public async Task MessageWithRecipient_CreatesDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Please review the routing config.",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"review_feedback","recipient":"claude-code"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal("claude-code", pending[0].TargetAgent);
        Assert.Contains("codex", pending[0].Summary!);
    }

    [Fact]
    public async Task MessageWithRecipientAndIntentOnly_CreatesDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Intent-driven feedback.",
            Intent = MessageIntent.ReviewFeedback,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"recipient":"claude-code","handoff_kind":"review_feedback"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal("claude-code", pending[0].TargetAgent);
        Assert.Contains("review feedback", pending[0].Summary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MessageWithTargetRole_CreatesDispatchForConfiguredRole()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "coder",
            Content = "Please review the latest pass.",
            Intent = MessageIntent.ReviewRequest,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"target_role":"reviewer","handoff_kind":"review_request"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = Assert.Single(await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]));
        Assert.Equal("pi", pending.TargetAgent);

        var context = JsonSerializer.Deserialize<DispatchContextSnapshot>(pending.ContextJson!);
        Assert.NotNull(context);
        Assert.Equal("target_role", context!.AddressedVia);
        Assert.Equal("reviewer", context.MessageTargetRole);
    }

    [Fact]
    public async Task MessageWithRecipientAndTargetRole_PrefersRecipient()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Please send this directly to Claude.",
            Intent = MessageIntent.Handoff,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"recipient":"claude-code","target_role":"reviewer","handoff_kind":"planning_summary"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = Assert.Single(await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]));
        Assert.Equal("claude-code", pending.TargetAgent);

        var context = JsonSerializer.Deserialize<DispatchContextSnapshot>(pending.ContextJson!);
        Assert.NotNull(context);
        Assert.Equal("recipient", context!.AddressedVia);
        Assert.Equal("claude-code", context.Recipient);
        Assert.Equal("reviewer", context.MessageTargetRole);
    }

    [Fact]
    public async Task MessageDispatch_ExpiresOlderTaskDispatchForSameTarget()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Review cleanup" });
        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "coder");
        var older = Assert.Single(await _dispatches.ListAsync("proj", "pi", [DispatchStatus.Pending]));

        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "patch-coder",
            Content = "Please review the latest pass.",
            Intent = MessageIntent.ReviewRequest,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"recipient":"pi","handoff_kind":"review_request"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", "pi", [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal(msg.Id, pending[0].TriggerId);

        var expiredOlder = await _dispatches.GetByIdAsync(older.Id);
        Assert.Equal(DispatchStatus.Expired, expiredOlder!.Status);
    }

    [Fact]
    public async Task MessageWithoutRecipientOrTargetRole_NoDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "General comment, no recipient.",
            Metadata = JsonSerializer.Deserialize<JsonElement>("""{"type":"comment"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // Default config requires has_recipient=true or has_target_role=true
    }

    [Fact]
    public async Task MessageWithUnknownTargetRole_NoDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "claude-code",
            Content = "This should not route.",
            Intent = MessageIntent.Handoff,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"target_role":"coordinator","handoff_kind":"planning_summary"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task MessageToSelf_NoDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "claude-code",
            Content = "Note to self",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"note","recipient":"claude-code"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // Don't dispatch to yourself
    }

    #endregion

    #region Dedup

    [Fact]
    public async Task DuplicateTaskEvent_SuppressedAtomically()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dup test" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");
        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending); // Second call deduped
    }

    [Fact]
    public async Task DuplicateMessageEvent_SuppressedAtomically()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Review this",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"review_feedback","recipient":"claude-code"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);
        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
    }

    #endregion

    #region Prompt content

    [Fact]
    public async Task DispatchPrompt_IncludesTaskMessages()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Prompt test" });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "claude-code",
            Content = "Implementation complete, 5 new tests added."
        });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Contains("5 new tests", pending[0].ContextPrompt!);
    }

    [Fact]
    public async Task MessageDispatch_IncludesIntentDrivenHandoffContent()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Here is the detailed plan for phase 2.",
            Intent = MessageIntent.Handoff,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"recipient":"claude-code","handoff_kind":"planning_summary"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Contains("detailed plan for phase 2", pending[0].ContextPrompt!);
    }

    [Fact]
    public async Task PlanningHandoff_StoresWorkingActivityHintInDispatchContext()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Implement review workflow follow-up.",
            Intent = MessageIntent.Handoff,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"recipient":"claude-code","handoff_kind":"planning_summary"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = Assert.Single(await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]));
        var context = JsonSerializer.Deserialize<DispatchContextSnapshot>(pending.ContextJson!);

        Assert.NotNull(context);
        Assert.Equal("planning_handoff", context!.ContextKind);
        Assert.Equal("working", context.ActivityHint);
    }

    #endregion
}
