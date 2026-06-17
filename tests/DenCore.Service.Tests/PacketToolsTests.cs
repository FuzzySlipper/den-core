using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Tools;

namespace DenCore.Service.Tests;

public class PacketToolsTests
{
    // ─── RenderWorkerPrompt tests ───────────────────────────────────────

    [Fact]
    public async Task RenderWorkerPrompt_WorkerMcpTool_MentionsPostWorkerCompletion()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "worker_mcp_tool", verbose: true);
        Assert.Contains("post_worker_completion_packet", result);
        Assert.Contains("worker_mcp_tool", result);
    }

    [Fact]
    public async Task RenderWorkerPrompt_ArtifactReconciled_UsesArtifactWording()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "artifact_reconciled", verbose: true);
        Assert.Contains("artifact_reconciled", result);
        Assert.Contains("completion.json", result);
        Assert.Contains("Runner/orchestrator will verify", result);
    }

    [Fact]
    public async Task RenderWorkerPrompt_ArtifactReconciled_ForbidsRawHttpCurl()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "artifact_reconciled", verbose: true);
        Assert.Contains("Do NOT attempt raw MCP HTTP/curl", result);
    }

    [Fact]
    public async Task RenderWorkerPrompt_ArtifactReconciled_DoesNotMentionPostWorkerCompletion()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "artifact_reconciled", verbose: true);
        Assert.DoesNotContain("call MCP tool `post_worker_completion_packet`", result);
    }

    [Fact]
    public async Task RenderWorkerPrompt_PreservesIdentityGuidanceInBothModes()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var mcpResult = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "worker_mcp_tool", verbose: true);
        var artifactResult = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "artifact_reconciled", verbose: true);

        foreach (var result in new[] { mcpResult, artifactResult })
        {
            var parsed = JsonDocument.Parse(result);
            var prompt = parsed.RootElement.GetProperty("prompt").GetString()!;
            Assert.Contains("DEN_WORKER_RUN_ID", prompt);
            Assert.Contains("DEN_WORKER_PROJECT_ID", prompt);
            Assert.Contains("DEN_WORKER_ROLE", prompt);
            Assert.Contains("Never pass placeholder", prompt);
        }
    }

    [Fact]
    public async Task RenderWorkerPrompt_NoPlaceholderIdentityGuidance()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "artifact_reconciled", verbose: true);
        var parsed = JsonDocument.Parse(result);
        var prompt = parsed.RootElement.GetProperty("prompt").GetString()!;
        Assert.Contains("(literal DEN_WORKER_RUN_ID)", prompt);
        Assert.Contains("$DEN_WORKER_RUN_ID", prompt);
    }

    [Fact]
    public async Task RenderWorkerPrompt_DefaultMode_IsWorkerMcpTool()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", verbose: true);
        Assert.Contains("post_worker_completion_packet", result);
        Assert.Contains("worker_mcp_tool", result);
    }

    [Fact]
    public async Task RenderWorkerPrompt_UnknownMode_DefaultsToWorkerMcpTool()
    {
        var repo = new FakeMessageRepository(CreateFakeMessage(1, "test-proj", 42));
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 1, "coder", completion_reporting_mode: "some_random_mode", verbose: true);
        Assert.Contains("post_worker_completion_packet", result);
        Assert.Contains("worker_mcp_tool", result);
        Assert.DoesNotContain("artifact_reconciled", result);
    }

    [Fact]
    public async Task RenderWorkerPrompt_InvalidMessage_ReturnsError()
    {
        var repo = new FakeMessageRepository(null);
        var result = await PacketTools.RenderWorkerPrompt(repo, "test-proj", 999, "coder");
        Assert.Contains("error", result);
        Assert.Contains("not found", result);
    }

    // ─── PrepareCoderContextPacket tests ────────────────────────────────

    [Fact]
    public async Task PrepareCoderContextPacket_ArtifactReconciled_ContainsArtifactWording()
    {
        var tasks = new FakeTaskRepository();
        var messages = new FakeMessageRepository(null);
        var projectId = "test-proj";
        tasks.SetupTask(1, projectId);

        var result = await PacketTools.PrepareCoderContextPacket(
            tasks, messages, projectId, 1, "test-user",
            completion_reporting_mode: "artifact_reconciled", verbose: true);

        Assert.Contains("artifact_reconciled", result);
        Assert.Contains("completion.json", result);
        Assert.Contains("Do NOT attempt raw MCP HTTP/curl", result);
    }

    [Fact]
    public async Task PrepareCoderContextPacket_WorkerMcpTool_ContainsMcpWording()
    {
        var tasks = new FakeTaskRepository();
        var messages = new FakeMessageRepository(null);
        var projectId = "test-proj";
        tasks.SetupTask(1, projectId);

        var result = await PacketTools.PrepareCoderContextPacket(
            tasks, messages, projectId, 1, "test-user",
            completion_reporting_mode: "worker_mcp_tool", verbose: true);

        Assert.Contains("worker_mcp_tool", result);
        Assert.Contains("post_worker_completion_packet", result);
    }

    [Fact]
    public async Task PrepareCoderContextPacket_ArtifactReconciled_ForbidsRawHttpCurl()
    {
        var tasks = new FakeTaskRepository();
        var messages = new FakeMessageRepository(null);
        var projectId = "test-proj";
        tasks.SetupTask(1, projectId);

        var result = await PacketTools.PrepareCoderContextPacket(
            tasks, messages, projectId, 1, "test-user",
            completion_reporting_mode: "artifact_reconciled", verbose: true);

        Assert.Contains("Do NOT attempt raw MCP HTTP/curl", result);
    }

    // ─── Metadata tests ─────────────────────────────────────────────────

    [Fact]
    public async Task PrepareCoderContextPacket_Metadata_IncludesCompletionReportingMode()
    {
        var tasks = new FakeTaskRepository();
        var messages = new FakeMessageRepository(null);
        var projectId = "test-proj";
        tasks.SetupTask(1, projectId);

        var result = await PacketTools.PrepareCoderContextPacket(
            tasks, messages, projectId, 1, "test-user",
            completion_reporting_mode: "artifact_reconciled", verbose: true);

        using var doc = JsonDocument.Parse(result);
        var metadata = doc.RootElement.GetProperty("packet").GetProperty("metadata");
        Assert.True(metadata.TryGetProperty("completion_reporting_mode", out var mode));
        Assert.Equal("artifact_reconciled", mode.GetString());
    }

    // ─── Fake implementations ───────────────────────────────────────────

    private static Message CreateFakeMessage(int id, string projectId, int taskId)
    {
        return new Message
        {
            Id = id,
            ProjectId = projectId,
            TaskId = taskId,
            Sender = "test-user",
            Content = "# Coder Context Packet\n\nTask description here.",
            Intent = MessageIntent.Handoff,
            Metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "coder_context_packet",
                ["packet_kind"] = "coder_context_packet",
                ["role"] = "coder",
                ["task_id"] = taskId,
            }),
        };
    }

    private sealed class FakeMessageRepository : IMessageRepository
    {
        private readonly Message? _message;
        private readonly List<Message> _created = new();
        public IReadOnlyList<Message> Created => _created;

        public FakeMessageRepository(Message? message) => _message = message;

        public Task<Message?> GetByIdAsync(int id) => Task.FromResult(_message);

        public Task<Message> CreateAsync(Message message)
        {
            message.Id = _created.Count + 100;
            message.Sender ??= "test-user";
            _created.Add(message);
            return Task.FromResult(message);
        }

        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null,
            DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null) =>
            Task.FromResult(new List<Message>());

        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null) =>
            Task.FromResult(new List<MessageFeedItem>());

        public Task<Models.Thread> GetThreadAsync(int threadId) =>
            Task.FromResult(new Models.Thread
            {
                Root = new Message
                {
                    Id = 1,
                    ProjectId = "test",
                    Sender = "test-user",
                    Content = "thread root",
                },
                Replies = new List<Message>(),
            });

        public Task<int> MarkReadAsync(string agent, int[] messageIds) => Task.FromResult(messageIds.Length);

        public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(
            string? projectId = null, int? taskId = null, string? sender = null,
            string? metadataType = null, string? urgency = null, bool? isRead = null,
            string? readForAgent = null, int limit = 20, int offset = 0) =>
            Task.FromResult(new List<NotificationFeedItem>());

        public Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds) =>
            Task.FromResult(notificationIds?.Length ?? 0);

        public Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null) =>
            Task.FromResult(0);
        public Task<WaitForMessagesResult> WaitForMessagesAsync(string projectId, string unreadFor, int timeoutMs = 30000, int limit = 20, int? cursorMessageId = null) => throw new NotSupportedException();
    }

    private sealed class FakeTaskRepository : ITaskRepository
    {
        private TaskDetail? _detail;

        public void SetupTask(int taskId, string projectId)
        {
            _detail = new TaskDetail
            {
                Task = new ProjectTask
                {
                    Id = taskId,
                    ProjectId = projectId,
                    Title = "Test Task",
                    Description = "Test task description.",
                    Status = DenCore.Models.TaskStatus.InProgress,
                    Priority = 1,
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
        }

        public Task<TaskDetail> GetDetailAsync(int id) => Task.FromResult(_detail!);

        public Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null) => Task.FromResult(task);
        public Task<ProjectTask?> GetByIdAsync(int id) => Task.FromResult<ProjectTask?>(null);
        public Task<TaskWorkflowSummary> GetWorkflowSummaryAsync(int id) =>
            Task.FromResult(new TaskWorkflowSummary
            {
                Id = id,
                ProjectId = "test",
                Title = "Test",
                Status = "in_progress",
                Dependencies = new List<TaskDependencyInfo>(),
                Subtasks = new List<CompactSubtaskEntry>(),
                ReviewWorkflow = new CompactReviewWorkflow
                {
                    Timeline = new List<CompactReviewRoundRef>(),
                },
                RecentMessages = new List<CompactMessageHeader>(),
                UnresolvedFindings = new List<CompactFindingEntry>(),
                DeepReadHint = "Use get_task for full details.",
                Availability = "in_progress",
            });
        public Task<List<TaskSummary>> ListAsync(string projectId, DenCore.Models.TaskStatus[]? statuses = null,
            string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null,
            bool includeAll = false) => Task.FromResult(new List<TaskSummary>());
        public Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent) =>
            Task.FromResult(new ProjectTask
            {
                Id = id,
                ProjectId = "test",
                Title = "Updated",
                Status = DenCore.Models.TaskStatus.InProgress,
                Priority = 1,
            });
        public Task AddDependencyAsync(int taskId, int dependsOn) => Task.CompletedTask;
        public Task RemoveDependencyAsync(int taskId, int dependsOn) => Task.CompletedTask;
        public Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null) =>
            Task.FromResult<ProjectTask?>(null);
    }
}
