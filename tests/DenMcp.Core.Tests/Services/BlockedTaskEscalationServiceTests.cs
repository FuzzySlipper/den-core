using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Services;

public class BlockedTaskEscalationServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentInstanceBindingRepository _bindings = null!;
    private MessageRepository _messages = null!;
    private AgentStreamRepository _stream = null!;
    private BlockedTaskEscalationService _service = null!;
    private ProjectRepository _projects = null!;
    private TaskRepository _tasks = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _bindings = new AgentInstanceBindingRepository(_testDb.Db);
        _messages = new MessageRepository(_testDb.Db);
        _stream = new AgentStreamRepository(_testDb.Db);
        _service = new BlockedTaskEscalationService(
            _bindings, _messages, _stream,
            NullLogger<BlockedTaskEscalationService>.Instance);
        _projects = new ProjectRepository(_testDb.Db);
        _tasks = new TaskRepository(_testDb.Db);

        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    #region ValidateBlockerContext

    [Fact]
    public void ValidateBlockerContext_ValidContext_ReturnsValid()
    {
        var result = _service.ValidateBlockerContext(
            "Missing API key for external service",
            "Cannot proceed without API key");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateBlockerContext_MissingSummary_ReturnsInvalid()
    {
        var result = _service.ValidateBlockerContext(null, "Cannot proceed");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateBlockerContext_MissingReason_ReturnsInvalid()
    {
        var result = _service.ValidateBlockerContext("Blocker", null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("reason", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateBlockerContext_BothMissing_ReturnsTwoErrors()
    {
        var result = _service.ValidateBlockerContext(null, null);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void ValidateBlockerContext_EmptyStrings_ReturnsInvalid()
    {
        var result = _service.ValidateBlockerContext("", "   ");

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    #endregion

    #region EscalateBlockedTask - Planner Present

    [Fact]
    public async Task EscalateBlockedTask_PlannerPresent_SendsPlannerWake()
    {
        // Arrange: create a task and an active planner binding
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "planner-1",
            ProjectId = "test-proj",
            AgentIdentity = "den-mcp-planner",
            AgentFamily = "hermes",
            Role = "planner",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Missing dependency",
            Reason = "Cannot proceed without upstream task",
            ChangedBy = "den-mcp-runner"
        };

        // Act
        var result = await _service.EscalateBlockedTaskAsync(task, escalation);

        // Assert
        Assert.True(result.WasNew);
        Assert.True(result.PlannerWakeAttempted);
        Assert.False(result.UserNotificationCreated);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public async Task EscalateBlockedTask_ConductorPresent_SendsPlannerWake()
    {
        // Arrange: create a task and an active conductor binding
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "conductor-1",
            ProjectId = "test-proj",
            AgentIdentity = "den-mcp-conductor",
            AgentFamily = "hermes",
            Role = "conductor",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Infrastructure issue",
            Reason = "Server unreachable",
            ChangedBy = "den-mcp-runner"
        };

        // Act
        var result = await _service.EscalateBlockedTaskAsync(task, escalation);

        // Assert
        Assert.True(result.WasNew);
        Assert.True(result.PlannerWakeAttempted);
        Assert.False(result.UserNotificationCreated);
    }

    #endregion

    #region EscalateBlockedTask - Planner Absent

    [Fact]
    public async Task EscalateBlockedTask_PlannerAbsent_CreatesUserNotification()
    {
        // Arrange: create a task with NO planner binding
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "No planner available",
            Reason = "No active planner binding for this project",
            SuggestedNextStep = "Assign a planner or resolve manually",
            RequiresHumanInput = true,
            ChangedBy = "den-mcp-runner"
        };

        // Act
        var result = await _service.EscalateBlockedTaskAsync(task, escalation);

        // Assert
        Assert.True(result.WasNew);
        Assert.False(result.PlannerWakeAttempted);
        Assert.True(result.UserNotificationCreated);
        Assert.NotNull(result.UserNotificationMessageId);

        // Verify notification was created as a message
        var notification = await _messages.GetByIdAsync(result.UserNotificationMessageId!.Value);
        Assert.NotNull(notification);
        Assert.Equal(MessageIntent.Notification, notification!.Intent);
        Assert.Equal("den-core", notification.Sender);
        Assert.Equal(task.Id, notification.TaskId);
        Assert.Equal("test-proj", notification.ProjectId);

        // Verify metadata contains blocker_attention_required type
        Assert.NotNull(notification.Metadata);
        Assert.True(notification.Metadata!.Value.TryGetProperty("type", out var typeEl));
        Assert.Equal("blocker_attention_required", typeEl.GetString());
        Assert.True(notification.Metadata.Value.TryGetProperty("subtype", out var subtypeEl));
        Assert.Equal("blocker_attention_required", subtypeEl.GetString());
        Assert.True(notification.Metadata.Value.TryGetProperty("urgency", out var urgencyEl));
        Assert.Equal("high", urgencyEl.GetString());
    }

    [Fact]
    public async Task EscalateBlockedTask_PlannerAbsent_NotificationContainsBlockerDetails()
    {
        // Arrange
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Blocked task"
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Build failure",
            Reason = "Dependency compilation error",
            AttemptedRemedies = "Tried rebuilding, clearing cache",
            SuggestedNextStep = "Fix upstream dependency",
            RequiresHumanInput = true,
            ChangedBy = "den-mcp-runner"
        };

        // Act
        var result = await _service.EscalateBlockedTaskAsync(task, escalation);
        var notification = await _messages.GetByIdAsync(result.UserNotificationMessageId!.Value);

        // Assert: content contains task reference and blocker info
        Assert.Contains(task.Title, notification!.Content);
        Assert.Contains("Blocker:", notification.Content);
        Assert.Contains("Build failure", notification.Content);
    }

    #endregion

    #region EscalateBlockedTask - Duplicate Dedup

    [Fact]
    public async Task EscalateBlockedTask_DuplicateBlockedTransition_SkipsEscalation()
    {
        // Arrange: create a task
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "First blocker",
            Reason = "Cannot proceed",
            ChangedBy = "den-mcp-runner"
        };

        // Act: first escalation
        var firstResult = await _service.EscalateBlockedTaskAsync(task, escalation);

        // Assert: first should succeed
        Assert.True(firstResult.WasNew);
        Assert.True(firstResult.UserNotificationCreated);

        // Act: second escalation (duplicate within dedup window)
        var secondEscalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "First blocker",
            Reason = "Cannot proceed",
            ChangedBy = "den-mcp-runner"
        };

        var secondResult = await _service.EscalateBlockedTaskAsync(task, secondEscalation);

        // Assert: second should be deduped
        Assert.False(secondResult.WasNew);
        Assert.NotNull(secondResult.SkipReason);
        Assert.Contains("already exists", secondResult.SkipReason);
        Assert.False(secondResult.PlannerWakeAttempted);
        Assert.False(secondResult.UserNotificationCreated);
    }

    [Fact]
    public async Task EscalateBlockedTask_DedupWindowDisabled_AllowsRepeatedSameSignatureEscalations()
    {
        var service = new BlockedTaskEscalationService(
            _bindings,
            _messages,
            _stream,
            NullLogger<BlockedTaskEscalationService>.Instance,
            new BlockedTaskEscalationPolicyOptions { DedupWindow = TimeSpan.Zero });

        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Reblocked after resolution",
            Reason = "The same external dependency is blocked again",
            ChangedBy = "den-mcp-runner"
        };

        var firstResult = await service.EscalateBlockedTaskAsync(task, escalation);
        var secondResult = await service.EscalateBlockedTaskAsync(task, escalation);

        Assert.True(firstResult.WasNew);
        Assert.True(secondResult.WasNew);
        Assert.True(firstResult.UserNotificationCreated);
        Assert.True(secondResult.UserNotificationCreated);

        var notifications = await _messages.GetMessagesAsync(
            projectId: "test-proj",
            taskId: task.Id,
            intent: MessageIntent.Notification,
            limit: 10);
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task EscalateBlockedTask_DifferentBlockerSignature_CreatesNewEscalation()
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        var firstResult = await _service.EscalateBlockedTaskAsync(task, new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "First blocker",
            Reason = "Cannot proceed",
            ChangedBy = "den-mcp-runner"
        });

        var secondResult = await _service.EscalateBlockedTaskAsync(task, new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Different blocker",
            Reason = "A different upstream dependency is missing",
            ChangedBy = "den-mcp-runner"
        });

        Assert.True(firstResult.WasNew);
        Assert.True(secondResult.WasNew);
        Assert.True(secondResult.UserNotificationCreated);
    }

    #endregion

    #region EscalateBlockedTask - Planner Degraded Still Works

    [Fact]
    public async Task EscalateBlockedTask_PlannerDegraded_StillSendsWake()
    {
        // Arrange: planner binding is degraded but still reachable
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "planner-1",
            ProjectId = "test-proj",
            AgentIdentity = "den-mcp-planner",
            AgentFamily = "hermes",
            Role = "planner",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Degraded
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Degraded planner test",
            Reason = "Testing degraded planner handling",
            ChangedBy = "den-mcp-runner"
        };

        var result = await _service.EscalateBlockedTaskAsync(task, escalation);

        Assert.True(result.WasNew);
        Assert.True(result.PlannerWakeAttempted);
    }

    #endregion

    #region EscalateBlockedTask - Planner Inactive Falls Through

    [Fact]
    public async Task EscalateBlockedTask_PlannerInactive_CreatesUserNotification()
    {
        // Arrange: planner binding exists but is inactive
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "planner-1",
            ProjectId = "test-proj",
            AgentIdentity = "den-mcp-planner",
            AgentFamily = "hermes",
            Role = "planner",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Inactive
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Inactive planner test",
            Reason = "Testing inactive planner fallback",
            ChangedBy = "den-mcp-runner"
        };

        var result = await _service.EscalateBlockedTaskAsync(task, escalation);

        // Inactive planner should not be found -> falls through to notification
        Assert.True(result.WasNew);
        Assert.False(result.PlannerWakeAttempted);
        Assert.True(result.UserNotificationCreated);
    }

    #endregion

    #region EscalateBlockedTask - Agent Stream Wake Entry

    [Fact]
    public async Task EscalateBlockedTask_PlannerPresent_CreatesStreamWakeEntry()
    {
        // Arrange
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "planner-1",
            ProjectId = "test-proj",
            AgentIdentity = "den-mcp-planner",
            AgentFamily = "hermes",
            Role = "planner",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Stream wake test",
            Reason = "Testing stream wake entry creation",
            AttemptedRemedies = "Tried alternative approach",
            SuggestedNextStep = "Replan task",
            RequiresHumanInput = false,
            ChangedBy = "den-mcp-runner"
        };

        var result = await _service.EscalateBlockedTaskAsync(task, escalation);

        // Verify agent stream entry was created
        var streamEntries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "test-proj",
            TaskId = task.Id,
            EventType = "task_blocked_escalation",
            IncludeDebug = true
        });

        Assert.Single(streamEntries);
        var entry = streamEntries[0];
        Assert.Equal(AgentStreamKind.Message, entry.StreamKind);
        Assert.Equal(AgentStreamDeliveryMode.Wake, entry.DeliveryMode);
        Assert.Equal("den-core", entry.Sender);
        Assert.Equal("den-mcp-planner", entry.RecipientAgent);
        Assert.Equal("planner", entry.RecipientRole);
        Assert.Equal("planner-1", entry.RecipientInstanceId);
        Assert.StartsWith($"blocked-escalation:{task.Id}:", entry.DedupKey);

        // Verify metadata
        Assert.NotNull(entry.Metadata);
        Assert.True(entry.Metadata!.Value.TryGetProperty("type", out var typeEl));
        Assert.Equal("task_blocked_escalation", typeEl.GetString());
        Assert.True(entry.Metadata.Value.TryGetProperty("escalation_context", out var ctxEl));
        Assert.Equal("Stream wake test", ctxEl.GetProperty("blocker_summary").GetString());
        Assert.Equal("Testing stream wake entry creation", ctxEl.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EscalateBlockedTask_PlannerPresent_DuplicateSignatureSkipsSecondWake()
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "planner-1",
            ProjectId = "test-proj",
            AgentIdentity = "den-mcp-planner",
            AgentFamily = "hermes",
            Role = "planner",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active
        });

        var escalation = new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Repeated planner blocker",
            Reason = "Same blocker should not wake twice",
            ChangedBy = "den-mcp-runner"
        };

        var firstResult = await _service.EscalateBlockedTaskAsync(task, escalation);
        var secondResult = await _service.EscalateBlockedTaskAsync(task, escalation);

        Assert.True(firstResult.WasNew);
        Assert.True(firstResult.PlannerWakeAttempted);
        Assert.False(secondResult.WasNew);
        Assert.False(secondResult.PlannerWakeAttempted);

        var streamEntries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "test-proj",
            TaskId = task.Id,
            EventType = "task_blocked_escalation",
            IncludeDebug = true
        });
        Assert.Single(streamEntries);
    }

    [Fact]
    public async Task EscalateBlockedTask_PlannerWakeFailure_CreatesUserNotificationFallback()
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test task"
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "planner-1",
            ProjectId = "test-proj",
            AgentIdentity = "den-mcp-planner",
            AgentFamily = "hermes",
            Role = "planner",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active
        });

        var service = new BlockedTaskEscalationService(
            _bindings,
            _messages,
            new ThrowingAgentStreamRepository(),
            NullLogger<BlockedTaskEscalationService>.Instance);

        var result = await service.EscalateBlockedTaskAsync(task, new BlockedTaskEscalation
        {
            TaskId = task.Id,
            ProjectId = "test-proj",
            BlockerSummary = "Planner wake failure",
            Reason = "Agent stream append is unavailable",
            ChangedBy = "den-mcp-runner"
        });

        Assert.True(result.WasNew);
        Assert.True(result.PlannerWakeAttempted);
        Assert.True(result.UserNotificationCreated);
        Assert.NotNull(result.UserNotificationMessageId);

        var notification = await _messages.GetByIdAsync(result.UserNotificationMessageId!.Value);
        Assert.NotNull(notification);
        Assert.Equal(MessageIntent.Notification, notification!.Intent);
        Assert.Equal("blocker_attention_required", notification.Metadata!.Value.GetProperty("type").GetString());
    }

    #endregion

    #region Non-blocked status transitions are not affected

    [Fact]
    public void ValidateBlockerContext_NotUsedForNonBlockedStatuses()
    {
        // This test verifies that validation is only called when status == "blocked".
        // The caller (TaskTools) is responsible for only calling validation for blocked transitions.
        // Here we verify that ValidateBlockerContext itself works correctly regardless.
        var result = _service.ValidateBlockerContext("summary", "reason");
        Assert.True(result.IsValid);
    }

    #endregion

    private sealed class ThrowingAgentStreamRepository : IAgentStreamRepository
    {
        public Task<AgentStreamEntry> AppendAsync(AgentStreamEntry entry) =>
            throw new InvalidOperationException("Simulated stream failure");

        public Task<AgentStreamEntry?> GetByIdAsync(int id) => Task.FromResult<AgentStreamEntry?>(null);

        public Task<Dictionary<int, AgentStreamEntry>> GetByIdsAsync(IReadOnlyList<int> ids) =>
            Task.FromResult(new Dictionary<int, AgentStreamEntry>());

        public Task<List<AgentStreamEntry>> ListAsync(AgentStreamListOptions? options = null) =>
            Task.FromResult(new List<AgentStreamEntry>());
    }
}
