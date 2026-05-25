using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Services;

/// <summary>
/// Tests that legacy dispatch creation and mutation are fully retired.
/// See den-communication-surfaces-concept-map: dispatch has no unique live responsibility.
/// </summary>
public class DispatchRetirementTests : IAsyncLifetime
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
        // Even with legacy dispatch explicitly enabled in a stale routing doc,
        // creation must be hard-disabled.
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
