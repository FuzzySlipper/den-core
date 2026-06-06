using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Tests.Services;

public sealed class StaleAttentionRoutingServiceTests
{
    private readonly FakeMessageRepository _messages = new();
    private readonly FakeBindingRepository _bindings = new();
    private readonly FakeProjectRepository _projects = new();

    private IStaleAttentionRoutingService CreateService() =>
        new StaleAttentionRoutingService(_messages, _bindings, _projects,
            NullLogger<StaleAttentionRoutingService>.Instance);

    // ── Severity routing ────────────────────────────────────────────────

    [Fact]
    public async Task CriticalCondition_RoutesToPlanner_AndCreatesUserNotification()
    {
        var service = CreateService();
        _bindings.AddBinding("den-core", "runner-agent", "planner");
        _projects.SetOwner("den-core", "patch");

        var condition = NewCondition("den-core", severity: "critical");
        var result = await service.RouteSingleAsync(condition);

        Assert.Equal(1, result.PlannerRouted);
        Assert.Equal(1, result.UserNotified); // critical also gets user notification
        Assert.Equal(0, result.FallbackNotified);
        Assert.Equal(0, result.InfoLogged);

        var plannerMsg = _messages.Created.FirstOrDefault(m =>
            (m.Metadata?.ToString() ?? "").Contains("stale_worker_alert"));
        Assert.NotNull(plannerMsg);
        Assert.Contains("critical", plannerMsg.Content);
    }

    [Fact]
    public async Task WarningCondition_RoutesToPlanner_NoCriticalNotification()
    {
        var service = CreateService();
        _bindings.AddBinding("den-core", "runner-agent", "planner");

        var condition = NewCondition("den-core", severity: "warning");
        var result = await service.RouteSingleAsync(condition);

        Assert.Equal(1, result.PlannerRouted);
        Assert.Equal(0, result.UserNotified); // warning doesn't get extra critical notification
        Assert.Equal(0, result.FallbackNotified);
        Assert.Equal(0, result.InfoLogged);
    }

    [Fact]
    public async Task InfoCondition_LoggedOnly_NoMessages()
    {
        var service = CreateService();

        var condition = NewCondition("den-core", severity: "info");
        var result = await service.RouteSingleAsync(condition);

        Assert.Equal(0, result.PlannerRouted);
        Assert.Equal(0, result.UserNotified);
        Assert.Equal(0, result.FallbackNotified);
        Assert.Equal(1, result.InfoLogged);
        Assert.Empty(_messages.Created);
    }

    // ── Fallback routing ────────────────────────────────────────────────

    [Fact]
    public async Task NoPlannerBinding_FallbackNotified()
    {
        var service = CreateService();
        _projects.SetOwner("den-core", "patch");

        var condition = NewCondition("den-core", severity: "warning");
        var result = await service.RouteSingleAsync(condition);

        Assert.Equal(0, result.PlannerRouted);
        Assert.Equal(0, result.UserNotified);
        Assert.Equal(1, result.FallbackNotified);
        Assert.Equal(0, result.InfoLogged);

        var fallbackMsg = _messages.Created.FirstOrDefault(m =>
            (m.Metadata?.ToString() ?? "").Contains("stale_worker_alert_no_owner"));
        Assert.NotNull(fallbackMsg);
        Assert.Contains("no planner reachable", fallbackMsg.Content);
    }

    // ── Owner resolution ────────────────────────────────────────────────

    [Fact]
    public async Task RoutesWithOwnerIdentity_InMetadataAndContent()
    {
        var service = CreateService();
        _bindings.AddBinding("den-core", "runner-agent", "planner");
        _projects.SetOwner("den-core", "patch");

        var condition = NewCondition("den-core", severity: "warning");
        var result = await service.RouteSingleAsync(condition);

        var msg = _messages.Created.First();
        var metadataJson = msg.Metadata?.ToString() ?? "";
        Assert.Contains("patch", metadataJson);
        Assert.Contains("patch", msg.Content);
    }

    // ── RouteAsync batch ────────────────────────────────────────────────

    [Fact]
    public async Task RouteAsync_ProcessesAllConditions()
    {
        var service = CreateService();
        _bindings.AddBinding("den-core", "runner-agent", "planner");

        var result = new StaleReconciliationResult
        {
            NewConditions =
            [
                NewCondition("den-core", severity: "critical"),
                NewCondition("den-core", severity: "warning"),
                NewCondition("den-core", severity: "info"),
            ],
            ReconciledAt = DateTime.UtcNow.ToString("o"),
        };

        var routingResult = await service.RouteAsync(result);

        Assert.Equal(2, routingResult.PlannerRouted); // critical + warning
        Assert.Equal(1, routingResult.UserNotified);  // critical only
        Assert.Equal(1, routingResult.InfoLogged);     // info only
        Assert.Equal(0, routingResult.FallbackNotified);
        Assert.Equal(3, _messages.Created.Count); // 2 planner + 1 critical user
    }

    // ── No-project condition ────────────────────────────────────────────

    [Fact]
    public async Task NoProjectId_LoggedOnly()
    {
        var service = CreateService();

        var condition = NewCondition("", severity: "critical");
        var result = await service.RouteSingleAsync(condition);

        Assert.Equal(0, result.PlannerRouted);
        Assert.Equal(0, result.UserNotified);
        Assert.Equal(1, result.InfoLogged);
    }

    // ── Metadata integrity ──────────────────────────────────────────────

    [Fact]
    public async Task RoutedMessage_HasExpectedMetadata()
    {
        var service = CreateService();
        _bindings.AddBinding("den-core", "runner-agent", "planner");
        _projects.SetOwner("den-core", "patch");

        var condition = NewCondition("den-core", taskId: 42, runId: "run-1",
            workerIdentity: "worker-1", severity: "critical");
        var result = await service.RouteSingleAsync(condition);

        Assert.Contains(_messages.Created, m =>
            (m.Metadata?.ToString() ?? "").Contains("stale_worker_alert"));

        var plannerMsg = _messages.Created.First(m =>
            (m.Metadata?.ToString() ?? "").Contains("stale_worker_alert"));
        Assert.Equal("den-core", plannerMsg.ProjectId);
        Assert.Equal(42, plannerMsg.TaskId);
        Assert.Equal("den-core", plannerMsg.Sender);
        Assert.Equal(MessageIntent.Notification, plannerMsg.Intent);

        var criticalMsg = _messages.Created.First(m =>
            (m.Metadata?.ToString() ?? "").Contains("stale_worker_critical"));
        Assert.Equal("den-core", criticalMsg.ProjectId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static StaleWorkerCondition NewCondition(string projectId,
        string severity = "warning", int? taskId = null, string? runId = null,
        string? workerIdentity = null) => new()
    {
        StaleSignature = $"test:{projectId}:sig",
        Classification = "stale_ack",
        ProjectId = projectId,
        TaskId = taskId,
        RunId = runId,
        WorkerIdentity = workerIdentity,
        CurrentState = "ack",
        Severity = severity,
        StateReason = "Test condition",
        SuggestedNextAction = "Investigate",
    };

    // ── Fakes ───────────────────────────────────────────────────────────

    private sealed class FakeMessageRepository : IMessageRepository
    {
        public readonly List<Message> Created = [];
        private int _nextId = 1;

        public Task<Message> CreateAsync(Message message)
        {
            message.Id = _nextId++;
            message.CreatedAt = DateTime.UtcNow;
            Created.Add(message);
            return Task.FromResult(message);
        }

        public Task<Message?> GetByIdAsync(int id) =>
            Task.FromResult<Message?>(Created.FirstOrDefault(m => m.Id == id));

        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null,
            DateTime? since = null, string? unreadFor = null, int limit = 20,
            MessageIntent? intent = null) =>
            Task.FromResult(Created.Where(m => m.ProjectId == projectId).Take(limit).ToList());

        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20,
            MessageIntent? intent = null) =>
            Task.FromResult(new List<MessageFeedItem>());

        public Task<Models.Thread> GetThreadAsync(int threadId) =>
            throw new NotSupportedException();

        public Task<int> MarkReadAsync(string agent, int[] messageIds) =>
            Task.FromResult(messageIds.Length);

        public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(
            string? projectId = null, int? taskId = null, string? sender = null,
            string? metadataType = null, string? urgency = null, bool? isRead = null,
            string? readForAgent = null, int limit = 20, int offset = 0) =>
            Task.FromResult(new List<NotificationFeedItem>());

        public Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds) =>
            Task.FromResult(notificationIds?.Length ?? 0);

        public Task<int> MarkAllNotificationsReadAsync(string agent, string projectId,
            int? taskId = null) => Task.FromResult(0);
    }

    private sealed class FakeBindingRepository : IAgentInstanceBindingRepository
    {
        private readonly List<AgentInstanceBinding> _bindings = [];

        public void AddBinding(string projectId, string agentIdentity, string role)
        {
            _bindings.Add(new AgentInstanceBinding
            {
                ProjectId = projectId,
                AgentIdentity = agentIdentity,
                Role = role,
                Status = AgentInstanceBindingStatus.Active,
                InstanceId = "inst-1",
                AgentFamily = "worker",
                TransportKind = "den-channels"
            });
        }

        public Task<List<AgentInstanceBinding>> ListAsync(AgentInstanceBindingListOptions? options = null)
        {
            var results = _bindings.AsEnumerable();
            if (options?.ProjectId is not null)
                results = results.Where(b => b.ProjectId == options.ProjectId);
            if (options?.Statuses is { Length: > 0 })
                results = results.Where(b => options.Statuses.Contains(b.Status));
            return Task.FromResult(results.ToList());
        }

        public Task<AgentInstanceBinding?> GetAsync(int id) =>
            Task.FromResult<AgentInstanceBinding?>(null);

        public Task<AgentInstanceBinding> UpsertAsync(AgentInstanceBinding binding) =>
            Task.FromResult(binding);

        public Task<AgentInstanceBinding?> GetActiveByInstanceIdAsync(string instanceId, int limit = 1) =>
            Task.FromResult<AgentInstanceBinding?>(null);
        public Task<bool> HeartbeatAsync(string instanceId) => Task.FromResult(true);
        public Task<bool> CheckOutAsync(string instanceId) => Task.FromResult(false);
        public Task<int> CheckOutBySessionAsync(string sessionKey) => Task.FromResult(0);
        public Task<int> CleanupStaleAsync(int staleMinutes) => Task.FromResult(0);
    }

    private sealed class FakeProjectRepository : IProjectRepository
    {
        private readonly Dictionary<string, Project> _projects = new(StringComparer.Ordinal);

        public void SetOwner(string projectId, string owner)
        {
            _projects[projectId] = new Project
            {
                Id = projectId, Name = projectId, Owner = owner,
            };
        }

        public Task<Project?> GetByIdAsync(string id) =>
            Task.FromResult(_projects.GetValueOrDefault(id));

        public Task<Project> CreateAsync(Project project) => Task.FromResult(project);
        public Task<List<Project>> GetAllAsync() =>
            Task.FromResult(_projects.Values.ToList());
        public Task<List<Project>> ListAsync(string? kind = null, bool includeHidden = false,
            bool includeArchived = false) => Task.FromResult(new List<Project>());
        public Task<ProjectWithStats> GetWithStatsAsync(string id, string? agent = null) =>
            throw new NotSupportedException();
        public Task<Project> UpdateVisibilityAsync(string id, string visibility) =>
            throw new NotSupportedException();
        public Task<Project> UpdateProjectAsync(string id, ProjectUpdateRequest update) =>
            throw new NotSupportedException();
        public Task<Dictionary<string, int>> GetDependentRecordCountsAsync(string id) =>
            Task.FromResult(new Dictionary<string, int>());
        public Task DeleteSpaceAsync(string id) => Task.CompletedTask;
    }
}
