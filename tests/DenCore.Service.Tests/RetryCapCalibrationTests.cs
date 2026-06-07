using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Tools;
using Thread = DenCore.Models.Thread;

namespace DenCore.Service.Tests;

public class RetryCapCalibrationTests
{
    private static FingerTaskRepository MakeTaskRepo(string projectId, params (int id, string status)[] tasks)
    {
        return new FingerTaskRepository(projectId, tasks);
    }

    private static FingerMessageRepository MakeMessageRepo(string projectId)
    {
        return new FingerMessageRepository(projectId);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // -----------------------------------------------------------------------
    // Fixture 1: cap hit and resolved by 4th retry (planner-authorized)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task CapHit_ResolvedByFourthRetry_ReportsCompletedAfterExtraRetry()
    {
        var tasks = MakeTaskRepo("den-core", (2001, "in_progress"));
        var messages = MakeMessageRepo("den-core");

        // 3 coder attempts (all failed/completed-but-validator-failed)
        messages.AddCompletion(2001, "coder", "implementation_packet", "completed", "run-1", "abc1");
        messages.AddCompletion(2001, "coder", "implementation_packet", "completed", "run-2", "abc2");
        messages.AddCompletion(2001, "validator", "validation_packet", "failed", "val-1", null, "product_acceptance_gap");
        messages.AddCompletion(2001, "coder", "implementation_packet", "completed", "run-3", "abc3");
        messages.AddCompletion(2001, "validator", "validation_packet", "failed", "val-2", null, "product_acceptance_gap");
        // Planner authorizes 4th retry
        messages.AddPlannerAuth(2001, "den-mcp-planner", "Planner authorized ONE EXTRA NARROW RETRY for den-core #2001");
        // 4th coder attempt succeeds, validator passes
        messages.AddCompletion(2001, "coder", "implementation_packet", "completed", "run-4", "abc4");
        messages.AddCompletion(2001, "validator", "validation_packet", "completed", "val-3", "abc4");
        messages.AddCompletion(2001, "drift_checker", "drift_check_packet", "completed", "drift-1", "abc4");
        messages.AddCompletion(2001, "packet_auditor", "packet_audit_packet", "completed", "audit-1", "abc4");

        var json = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "den-core", max_attempts: 3);

        var root = Parse(json);
        Assert.Equal(1, root.GetProperty("tasks_hitting_cap").GetInt32());
        Assert.Equal("completed_after_extra_retry",
            root.GetProperty("items")[0].GetProperty("outcome").GetString());
        Assert.True(root.GetProperty("items")[0].GetProperty("planner_authorized").GetBoolean());
        Assert.Equal(4, root.GetProperty("items")[0].GetProperty("attempts").GetProperty("coder").GetInt32());
        Assert.Equal(3, root.GetProperty("items")[0].GetProperty("attempts").GetProperty("validator").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Fixture 2: cap hit and remains blocked
    // -----------------------------------------------------------------------
    [Fact]
    public async Task CapHit_RemainsBlocked_ReportsBlockedAtCap()
    {
        var tasks = MakeTaskRepo("den-channels", (2022, "blocked"));
        var messages = MakeMessageRepo("den-channels");

        // 3 coder attempts, all validator-failed
        messages.AddCompletion(2022, "coder", "implementation_packet", "completed", "r1", "h1");
        messages.AddCompletion(2022, "validator", "validation_packet", "failed", "v1", null, "docs_contract_gap");
        messages.AddCompletion(2022, "coder", "implementation_packet", "completed", "r2", "h2");
        messages.AddCompletion(2022, "validator", "validation_packet", "failed", "v2", null, "docs_contract_gap");
        messages.AddCompletion(2022, "coder", "implementation_packet", "completed", "r3", "h3");
        messages.AddCompletion(2022, "validator", "validation_packet", "failed", "v3", null, "docs_contract_gap");
        // No planner authorization

        var json = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "den-channels", max_attempts: 3);

        var root = Parse(json);
        Assert.Equal(1, root.GetProperty("tasks_hitting_cap").GetInt32());
        Assert.Equal("blocked_at_retry_cap",
            root.GetProperty("items")[0].GetProperty("outcome").GetString());
        Assert.False(root.GetProperty("items")[0].GetProperty("planner_authorized").GetBoolean());
        Assert.Equal(3, root.GetProperty("items")[0].GetProperty("attempts").GetProperty("coder").GetInt32());
        Assert.Equal(3, root.GetProperty("items")[0].GetProperty("attempts").GetProperty("validator").GetInt32());
        Assert.Contains("docs_contract_gap",
            root.GetProperty("items")[0].GetProperty("blocker_categories")[0].GetString());
    }

    // -----------------------------------------------------------------------
    // Fixture 3: non-retry operational blocker should not count as cap pressure
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OperationalBlocker_ExcludedFromCapPressure()
    {
        var tasks = MakeTaskRepo("den-core", (2040, "blocked"));
        var messages = MakeMessageRepo("den-core");

        // Task blocked by deployment/auth/membership — not retry cap
        // Only 1 coder attempt, but task status is blocked for operational reasons
        messages.AddCompletion(2040, "coder", "implementation_packet", "blocked", "run-ops-1", null,
            failureCategory: "deployment_unavailable");

        var json = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "den-core", max_attempts: 3);

        var root = Parse(json);
        // 1 attempt < 3 cap — should not appear as cap hit
        Assert.Equal(0, root.GetProperty("tasks_hitting_cap").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Fixture 4: synthetic/superseding assignments should not inflate attempts
    // -----------------------------------------------------------------------
    [Fact]
    public async Task SupersededAssignments_NotInflatingAttemptCounts()
    {
        var tasks = MakeTaskRepo("den-hermes-bridge", (2071, "in_progress"));
        var messages = MakeMessageRepo("den-hermes-bridge");

        // Real attempts: 3 coder, 3 validator
        messages.AddCompletion(2071, "coder", "implementation_packet", "completed", "cr1", "h1");
        messages.AddCompletion(2071, "validator", "validation_packet", "failed", "cv1", null, "membership_not_active");
        messages.AddCompletion(2071, "coder", "implementation_packet", "completed", "cr2", "h2");
        messages.AddCompletion(2071, "validator", "validation_packet", "failed", "cv2", null, "membership_not_active");
        messages.AddCompletion(2071, "coder", "implementation_packet", "completed", "cr3", "h3");
        messages.AddCompletion(2071, "validator", "validation_packet", "failed", "cv3", null, "post_terminal_pool_state_leak");

        // Synthetic/status_update messages (not worker completions — sender is runner, not coder)
        messages.AddStatusUpdate(2071, "den-mcp-runner", "#2071 coder launched after closeout");
        messages.AddStatusUpdate(2071, "den-mcp-runner", "#2071 validator launched");

        var json = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "den-hermes-bridge", max_attempts: 3);

        var root = Parse(json);
        Assert.Equal(1, root.GetProperty("tasks_hitting_cap").GetInt32());
        // Attempts should be exactly 3 coder + 3 validator, NOT inflated by status updates
        Assert.Equal(3, root.GetProperty("items")[0].GetProperty("attempts").GetProperty("coder").GetInt32());
        Assert.Equal(3, root.GetProperty("items")[0].GetProperty("attempts").GetProperty("validator").GetInt32());
        Assert.Equal(0, root.GetProperty("items")[0].GetProperty("attempts").GetProperty("reviewer").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EmptyProject_ReturnsZeroItems()
    {
        var tasks = MakeTaskRepo("empty-proj");
        var messages = MakeMessageRepo("empty-proj");

        var json = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "empty-proj", max_attempts: 3);

        var root = Parse(json);
        Assert.Equal("empty-proj", root.GetProperty("project_id").GetString());
        Assert.Contains("active tasks", root.GetProperty("summary").GetString());
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TaskBelowRetryCap_NotReported()
    {
        var tasks = MakeTaskRepo("den-core", (2050, "in_progress"));
        var messages = MakeMessageRepo("den-core");

        // Only 2 coder attempts — below cap of 3
        messages.AddCompletion(2050, "coder", "implementation_packet", "completed", "r1", "h1");
        messages.AddCompletion(2050, "validator", "validation_packet", "completed", "v1", "h1");

        var json = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "den-core", max_attempts: 3);

        var root = Parse(json);
        Assert.Equal(0, root.GetProperty("tasks_hitting_cap").GetInt32());
    }

    [Fact]
    public async Task IncludeTerminalIncludesDoneTasks()
    {
        var tasks = MakeTaskRepo("den-core", (1990, "done"));
        var messages = MakeMessageRepo("den-core");

        // 4 coder attempts on a done task
        messages.AddCompletion(1990, "coder", "implementation_packet", "completed", "r1", "h1");
        messages.AddCompletion(1990, "validator", "validation_packet", "failed", "v1", null, "test_failure");
        messages.AddCompletion(1990, "coder", "implementation_packet", "completed", "r2", "h2");
        messages.AddCompletion(1990, "validator", "validation_packet", "failed", "v2", null, "test_failure");
        messages.AddCompletion(1990, "coder", "implementation_packet", "completed", "r3", "h3");
        messages.AddCompletion(1990, "validator", "validation_packet", "failed", "v3", null, "test_failure");

        // Without include_terminal — done task excluded, should return empty
        var json1 = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "den-core", max_attempts: 3, include_terminal: false);
        var root1 = Parse(json1);
        Assert.Contains("active tasks", root1.GetProperty("summary").GetString());
        Assert.Equal(0, root1.GetProperty("items").GetArrayLength());

        // With include_terminal
        var json2 = await RetryCapCalibrationTools.RetryCapReport(
            tasks, messages, "den-core", max_attempts: 3, include_terminal: true);
        var root2 = Parse(json2);
        Assert.Equal(1, root2.GetProperty("tasks_hitting_cap").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Test fakes
    // -----------------------------------------------------------------------

    private sealed class FingerTaskRepository : ITaskRepository
    {
        private readonly string _projectId;
        private readonly Dictionary<int, (DenCore.Models.TaskStatus status, string title)> _tasks;

        public FingerTaskRepository(string projectId, params (int id, string status)[] tasks)
        {
            _projectId = projectId;
            _tasks = tasks.ToDictionary(
                t => t.id,
                t => (Enum.TryParse<DenCore.Models.TaskStatus>(t.status, ignoreCase: true, out var s) ? s : DenCore.Models.TaskStatus.InProgress,
                      $"Task #{t.id}"));
        }

        public Task<List<TaskSummary>> ListAsync(string projectId, DenCore.Models.TaskStatus[]? statuses = null,
            string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null, bool includeAll = false)
        {
            if (projectId != _projectId) return Task.FromResult(new List<TaskSummary>());
            var summaries = _tasks
                .Where(kvp => statuses is null || statuses.Contains(kvp.Value.status))
                .Select(kvp => new TaskSummary
                {
                    Id = kvp.Key,
                    ProjectId = _projectId,
                    Title = kvp.Value.title,
                    Status = kvp.Value.status,
                    Priority = 3,
                })
                .ToList();
            return Task.FromResult(summaries);
        }

        public Task<TaskDetail> GetDetailAsync(int id)
        {
            if (!_tasks.TryGetValue(id, out var t))
                throw new InvalidOperationException($"Task {id} not found");
            return Task.FromResult(new TaskDetail
            {
                Task = new ProjectTask
                {
                    Id = id,
                    ProjectId = _projectId,
                    Title = t.title,
                    Status = t.status,
                    Priority = 3,
                },
                Dependencies = [],
                Subtasks = [],
                RecentMessages = [],
                ReviewRounds = [],
                OpenReviewFindings = [],
                ResolvedReviewFindings = [],
                ReviewWorkflow = new ReviewWorkflowSummary { Timeline = [], },
            });
        }

        // Unused interface members
        public Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null) => throw new NotSupportedException();
        public Task<ProjectTask?> GetByIdAsync(int id) => throw new NotSupportedException();
        public Task<TaskWorkflowSummary> GetWorkflowSummaryAsync(int id) => throw new NotSupportedException();
        public Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent) => throw new NotSupportedException();
        public Task AddDependencyAsync(int taskId, int dependsOn) => throw new NotSupportedException();
        public Task RemoveDependencyAsync(int taskId, int dependsOn) => throw new NotSupportedException();
        public Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null) => throw new NotSupportedException();
    }

    private sealed class FingerMessageRepository : IMessageRepository
    {
        private readonly string _projectId;
        private readonly List<Message> _messages = [];
        private int _nextId = 1;

        public FingerMessageRepository(string projectId) => _projectId = projectId;

        public void AddCompletion(int taskId, string role, string packetType, string status, string runId,
            string? headCommit = null, string? failureCategory = null)
        {
            var metadata = new Dictionary<string, object>
            {
                ["type"] = packetType,
                ["packet_kind"] = packetType,
                ["schema"] = "den_worker_completion",
                ["schema_version"] = 1,
                ["completion_packet"] = true,
                ["role"] = role,
                ["supplied_role"] = role,
                ["status"] = status,
                ["run_id"] = runId,
                ["project_id"] = _projectId,
                ["task_id"] = taskId,
            };
            if (headCommit is not null) metadata["head_commit"] = headCommit;
            if (failureCategory is not null) metadata["failure_category"] = failureCategory;

            _messages.Add(new Message
            {
                Id = _nextId++,
                ProjectId = _projectId,
                TaskId = taskId,
                Sender = $"pool-{role}-01",
                Content = $"# {packetType.Replace('_', ' ')} — {status}",
                Metadata = JsonSerializer.SerializeToElement(metadata),
                CreatedAt = DateTime.UtcNow,
            });
        }

        public void AddPlannerAuth(int taskId, string sender, string content)
        {
            _messages.Add(new Message
            {
                Id = _nextId++,
                ProjectId = _projectId,
                TaskId = taskId,
                Sender = sender,
                Content = content,
                Metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["type"] = "status_update",
                }),
                CreatedAt = DateTime.UtcNow,
            });
        }

        public void AddStatusUpdate(int taskId, string sender, string content)
        {
            _messages.Add(new Message
            {
                Id = _nextId++,
                ProjectId = _projectId,
                TaskId = taskId,
                Sender = sender,
                Content = content,
                Metadata = default,
                CreatedAt = DateTime.UtcNow,
            });
        }

        public Task<Message> CreateAsync(Message message)
        {
            message.Id = _nextId++;
            message.CreatedAt = DateTime.UtcNow;
            _messages.Add(message);
            return Task.FromResult(message);
        }

        public Task<Message?> GetByIdAsync(int id) =>
            Task.FromResult(_messages.FirstOrDefault(m => m.Id == id));

        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null,
            DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null) =>
            Task.FromResult(_messages
                .Where(m => m.ProjectId == projectId && (taskId is null || m.TaskId == taskId))
                .Take(limit)
                .ToList());

        // Unused interface members
        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null) =>
            throw new NotSupportedException();
        public Task<Thread> GetThreadAsync(int threadId) => throw new NotSupportedException();
        public Task<int> MarkReadAsync(string agent, int[] messageIds) => throw new NotSupportedException();
        public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(
            string? projectId = null, int? taskId = null, string? sender = null,
            string? metadataType = null, string? urgency = null, bool? isRead = null,
            string? readForAgent = null, int limit = 20, int offset = 0) =>
            throw new NotSupportedException();
        public Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds) => throw new NotSupportedException();
        public Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null) => throw new NotSupportedException();
    }
}
