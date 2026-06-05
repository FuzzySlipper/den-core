using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Tools;
using Thread = DenCore.Models.Thread;

namespace DenCore.Service.Tests;

public class WorkerObservabilityToolsTests
{
    [Fact]
    public async Task RegisterWorkerRun_CreatesRuntimeNeutralAssignmentWithoutLaunchState()
    {
        var pool = new FakeWorkerPoolRepository();

        var json = await WorkerTools.RegisterWorkerRun(
            pool,
            project_id: "proj",
            task_id: 1245,
            requested_by: "runner",
            role: "coder",
            substrate: "external",
            run_id: "run-1",
            branch: "task/1245-demo",
            base_branch: "main",
            base_commit: "abc123",
            head_commit: "def456",
            profile: "den-worker",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var worker = doc.RootElement.GetProperty("worker_run");
        Assert.Equal("created", doc.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal("run-1", worker.GetProperty("run_id").GetString());
        Assert.Equal("ack", worker.GetProperty("status").GetString());
        Assert.Equal("external", worker.GetProperty("substrate").GetString());
        Assert.Equal("task/1245-demo", worker.GetProperty("requested_repo").GetProperty("branch").GetString());
        Assert.False(worker.TryGetProperty("session", out _));
        Assert.False(worker.TryGetProperty("startup_contract", out _));
        Assert.Equal(1, pool.UpsertMemberCalls);
        Assert.Equal(1, pool.LeaseCalls);
    }

    [Fact]
    public async Task RegisterWorkerRun_IsIdempotentByRunId()
    {
        var pool = new FakeWorkerPoolRepository();

        var first = await WorkerTools.RegisterWorkerRun(pool, "proj", 1245, "runner", "coder", run_id: "run-1", verbose: true);
        var second = await WorkerTools.RegisterWorkerRun(pool, "proj", 1245, "runner", "coder", run_id: "run-1", verbose: true);

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        Assert.Equal("created", firstDoc.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal("existing", secondDoc.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal(1, pool.UpsertMemberCalls);
        Assert.Equal(1, pool.LeaseCalls);
    }


    [Fact]
    public async Task WorkerRunLookups_RejectRunIdFromOtherProject()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(state: WorkerPoolStates.Running, projectId: "other-proj"));
        var messages = new CapturingMessageRepository();

        var statusJson = await WorkerTools.GetWorkerRunStatus(pool, messages, "proj", "run-1", verbose: true);
        using var statusDoc = JsonDocument.Parse(statusJson);
        Assert.Contains("not found in project proj", statusDoc.RootElement.GetProperty("error").GetString());

        var completionJson = await CompletionTools.PostWorkerCompletionPacket(
            pool,
            messages,
            project_id: "proj",
            run_id: "run-1",
            requested_by: "runner",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "done",
            verbose: true);
        using var completionDoc = JsonDocument.Parse(completionJson);
        Assert.Equal("missing_run", completionDoc.RootElement.GetProperty("completion_state").GetString());
        Assert.Empty(await messages.GetMessagesAsync("proj"));
    }

    [Fact]
    public async Task RegisteredWorkerRun_AcceptsCompletionPacketAndStatusUsesAssignmentState()
    {
        var pool = new FakeWorkerPoolRepository();
        var messages = new CapturingMessageRepository();

        await WorkerTools.RegisterWorkerRun(pool, "proj", 1245, "runner", "coder", run_id: "run-1", verbose: true);

        var completionJson = await CompletionTools.PostWorkerCompletionPacket(
            pool,
            messages,
            project_id: "proj",
            run_id: "run-1",
            requested_by: "runner",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "done",
            branch: "task/1245-demo",
            head_commit: "0123456789abcdef0123456789abcdef01234567",
            tests_run: "[\"dotnet test: passed\"]",
            dedupe_key: "run-1:completed",
            verbose: true);

        using var completionDoc = JsonDocument.Parse(completionJson);
        Assert.Equal("present", completionDoc.RootElement.GetProperty("completion_state").GetString());

        var statusJson = await WorkerTools.GetWorkerRunStatus(pool, messages, "proj", "run-1", task_id: 1245, verbose: true);
        using var statusDoc = JsonDocument.Parse(statusJson);
        Assert.Equal("completed", statusDoc.RootElement.GetProperty("worker_run").GetProperty("status").GetString());
        Assert.Equal("completed", statusDoc.RootElement.GetProperty("completion").GetProperty("status").GetString());
        Assert.Contains("completion=posted_completed", statusDoc.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task CleanupWorkerRun_ReleasesTerminalAssignment()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(state: WorkerPoolStates.Completed));

        var json = await WorkerTools.CleanupWorkerRun(pool, "proj", "run-1", "runner", reason: "synthetic done", verbose: true);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("cleaned_up", doc.RootElement.GetProperty("cleanup").GetProperty("status").GetString());
        Assert.Equal(1, pool.ReleaseCalls);
    }

    [Fact]
    public async Task AbortWorkerRun_ExpiresDurableAssignmentWithoutRuntimeTermination()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(state: WorkerPoolStates.Running));

        var json = await WorkerTools.AbortWorkerRun(pool, "proj", "run-1", "runner", reason: "operator abort", verbose: true);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("aborted", doc.RootElement.GetProperty("control").GetProperty("status").GetString());
        Assert.Equal(WorkerPoolStates.Expired, (await pool.GetAssignmentByRunIdAsync("run-1"))!.State);
    }

    [Fact]
    public async Task RerunWorkerRun_FailsClosedBecauseCoreDoesNotOwnRuntimeLaunch()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(state: WorkerPoolStates.Completed));

        var json = await WorkerTools.RerunWorkerRun(pool, "proj", "run-1", "runner", reason: "try again", verbose: true);
        using var doc = JsonDocument.Parse(json);

        Assert.Contains("den-host runtime substrate", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RenderWorkerPrompt_DoesNotUseDenPiWording()
    {
        var messages = new CapturingMessageRepository();
        var packetMessage = await messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = 1245,
            Sender = "runner",
            Content = "test content",
            Intent = MessageIntent.Handoff,
            Metadata = JsonSerializer.SerializeToElement(new
            {
                type = "coder_context_packet",
                role = "coder",
                task_id = 1245,
            }),
        });

        var json = await PacketTools.RenderWorkerPrompt(messages, "proj", packetMessage.Id, role: "coder", verbose: true);
        using var doc = JsonDocument.Parse(json);
        var prompt = doc.RootElement.GetProperty("prompt").GetString();

        Assert.NotNull(prompt);
        Assert.DoesNotContain("Den Pi", prompt, StringComparison.Ordinal);
        Assert.StartsWith("You are a tracked Den ", prompt.TrimStart(), StringComparison.Ordinal);
    }

    private static WorkerAssignment NewAssignment(string state, string projectId = "proj") => new()
    {
        Id = 1,
        WorkerIdentity = "worker-1",
        RunId = "run-1",
        ProjectId = projectId,
        TaskId = 1245,
        Role = "coder",
        AssignedBy = "runner",
        State = state,
        ProfileIdentity = "den-worker",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed class CapturingMessageRepository : IMessageRepository
    {
        private readonly List<Message> _messages = [];

        public Task<Message> CreateAsync(Message message)
        {
            message.Id = _messages.Count + 1;
            message.CreatedAt = DateTime.UtcNow;
            _messages.Insert(0, message);
            return Task.FromResult(message);
        }

        public Task<Message?> GetByIdAsync(int id) => Task.FromResult(_messages.FirstOrDefault(message => message.Id == id));
        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null, DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null)
            => Task.FromResult(_messages.Where(message => message.ProjectId == projectId && (taskId is null || message.TaskId == taskId)).Take(limit).ToList());
        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null) => throw new NotSupportedException();
        public Task<Thread> GetThreadAsync(int threadId) => throw new NotSupportedException();
        public Task<int> MarkReadAsync(string agent, int[] messageIds) => throw new NotSupportedException();
        public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(string? projectId = null, int? taskId = null, string? sender = null, string? metadataType = null, string? urgency = null, bool? isRead = null, string? readForAgent = null, int limit = 20, int offset = 0)
            => throw new NotSupportedException();
        public Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds) => throw new NotSupportedException();
        public Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null) => throw new NotSupportedException();
    }
}
