using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Tests.Services;

/// <summary>
/// Tests that legacy dispatch detection is a no-op after retirement.
/// See den-communication-surfaces-concept-map: dispatch has no unique live responsibility.
/// </summary>
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
        _detection = new DispatchDetectionService(routing, _dispatches, NoOpNotifications.Instance,
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

    /// <summary>
    /// Set a dispatch entry to 'approved' status directly via SQL.
    /// Used to set up approved entries for retirement tests after
    /// ApproveAsync was removed from the repository.
    /// </summary>
    private async Task SetApprovedDirectlyAsync(int dispatchId, string decidedBy = "user")
    {
        await using var conn = await _testDb.Db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = 'approved', decided_at = datetime('now'), decided_by = @decidedBy
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", dispatchId);
        cmd.Parameters.AddWithValue("@decidedBy", decidedBy);
        await cmd.ExecuteNonQueryAsync();
    }

    #region Retirement: no dispatch creation

    [Fact]
    public async Task OnMessageCreatedAsync_DoesNotCreateDispatch_EvenWithLegacyEnabled()
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
        Assert.Empty(pending);
    }

    [Fact]
    public async Task OnTaskStatusChangedAsync_DoesNotCreateDispatch_EvenWithLegacyEnabled()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Feature X" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "coder");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task OnTaskStatusChangedAsync_DoesNotExpireExistingDispatches()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Cleanup task" });
        var pending = await CreateDispatchAsync(triggerId: 10, taskId: task.Id, targetAgent: "codex");
        var approved = await CreateDispatchAsync(triggerId: 11, taskId: task.Id, targetAgent: "claude-code");
        await SetApprovedDirectlyAsync(approved.Id);

        await _detection.OnTaskStatusChangedAsync(task, "review", "done", "codex");

        Assert.Equal(DispatchStatus.Pending, (await _dispatches.GetByIdAsync(pending.Id))!.Status);
        Assert.Equal(DispatchStatus.Approved, (await _dispatches.GetByIdAsync(approved.Id))!.Status);
    }

    [Fact]
    public async Task OnMessageCreatedAsync_DoesNotExpireOlderTaskDispatches()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Review cleanup" });
        var older = await CreateDispatchAsync(triggerId: 10, taskId: task.Id, targetAgent: "pi");

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
        Assert.Equal(older.Id, pending[0].Id);
    }

    #endregion

    #region Retirement: pre-existing no-dispatch scenarios still no-op

    [Fact]
    public async Task TaskMovedToDone_NoDispatch()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Done task" });

        await _detection.OnTaskStatusChangedAsync(task, "review", "done", "codex");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task SameAgentAsSender_NoDispatch()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Self-review" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "pi");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending);
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
        Assert.Empty(pending);
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
        Assert.Empty(pending);
    }

    #endregion

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
}
