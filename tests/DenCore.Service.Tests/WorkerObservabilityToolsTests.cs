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

    // ─── Scope accounting tests ─────────────────────────────────────────

    [Fact]
    public async Task PostWorkerCompletionPacket_WithScopeAccounting_RendersSection()
    {
        var pool = new FakeWorkerPoolRepository();
        var messages = new CapturingMessageRepository();
        await WorkerTools.RegisterWorkerRun(pool, "proj", 1245, "runner", "coder", run_id: "run-scope-1", verbose: true);

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-scope-1",
            requested_by: "coder",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "Implemented scope accounting.",
            branch: "task/1245-foo",
            head_commit: "0123456789abcdef0123456789abcdef01234567",
            tests_run: "[\"dotnet test: passed\"]",
            scope_acceptance: "All core acceptance criteria met.",
            scope_deferred: "Polish items deferred to follow-up #2000.",
            scope_follow_ups: "[{\"task_id\":2000,\"title\":\"Scope polish\",\"classification\":\"polish\"}]",
            scope_parent_closable: "Yes — follow-ups are polish only.",
            verbose: true);

        using var doc = JsonDocument.Parse(completion);
        var content = doc.RootElement.GetProperty("completion").GetProperty("content").GetString()!;
        Assert.Contains("Scope accounting", content);
        Assert.Contains("All core acceptance criteria met.", content);
        Assert.Contains("Intentionally narrowed/deferred", content);
        Assert.Contains("Follow-up tasks:", content);
        Assert.Contains("Parent closeable:", content);
        Assert.Contains("Yes", content);

        var metadata = doc.RootElement.GetProperty("completion").GetProperty("metadata");
        Assert.True(metadata.TryGetProperty("scope_acceptance", out _));
        Assert.True(metadata.TryGetProperty("scope_follow_ups", out _));
    }

    [Fact]
    public async Task PostWorkerCompletionPacket_WithoutScopeAccounting_RendersNotReported()
    {
        var pool = new FakeWorkerPoolRepository();
        var messages = new CapturingMessageRepository();
        await WorkerTools.RegisterWorkerRun(pool, "proj", 1245, "runner", "coder", run_id: "run-noscope-1", verbose: true);

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-noscope-1",
            requested_by: "coder",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "Simple change.",
            branch: "task/1245-foo",
            head_commit: "0123456789abcdef0123456789abcdef01234567",
            tests_run: "[\"dotnet test: passed\"]",
            verbose: true);

        using var doc = JsonDocument.Parse(completion);
        var content = doc.RootElement.GetProperty("completion").GetProperty("content").GetString()!;
        Assert.Contains("Scope accounting", content);
        Assert.Contains("Not reported", content);
        Assert.Contains("prepare_coder_context_packet", content);
    }



    [Fact]
    public async Task PostImplementationPacket_RiskyScope_CreatesScopeAuditTrigger()
    {
        var pool = new FakeWorkerPoolRepository();
        var messages = new CapturingMessageRepository();
        await WorkerTools.RegisterWorkerRun(pool, "proj", 1245, "runner", "coder", run_id: "run-trigger-1", verbose: true);

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-trigger-1",
            requested_by: "coder",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "Implemented an observability projection foundation with follow-up tasks recorded.",
            branch: "task/1245-foo",
            head_commit: "0123456789abcdef0123456789abcdef01234567",
            tests_run: "[\"dotnet test: passed\"]",
            scope_follow_ups: "[{\"task_id\":2000,\"title\":\"Use live current-work producers\",\"classification\":\"acceptance_gap_candidate\"}]",
            scope_parent_closable: "Uncertain — follow-up may be required for parent acceptance.",
            verbose: true);

        using var doc = JsonDocument.Parse(completion);
        var sourceMessageId = doc.RootElement.GetProperty("completion").GetProperty("message_id").GetInt32();
        var stored = await messages.GetMessagesAsync("proj", taskId: 1245, limit: 10);
        var trigger = Assert.Single(stored, IsScopeAuditTrigger);
        Assert.Equal(sourceMessageId, trigger.Metadata!.Value.GetProperty("source_completion_message_id").GetInt32());
        Assert.Equal("acceptance_gap_candidate", trigger.Metadata!.Value.GetProperty("trigger_reason").GetString());
        Assert.Equal("scope_auditor", trigger.Metadata!.Value.GetProperty("target_role").GetString());
        Assert.Contains("Scope audit trigger", trigger.Content);
    }

    [Theory]
    [InlineData("Simple localized-patch typo fix.", "Yes — tiny localized patch, no parent acceptance follow-up.")]
    [InlineData("Built simple fix.", "No deferred work.")]
    [InlineData("Updated guidance wording.", "No deferred work.")]
    [InlineData("Rapid cleanup of stale comment.", "No deferred work.")]
    public async Task PostImplementationPacket_OrdinaryScope_DoesNotCreateScopeAuditTrigger(string summary, string parentClosable)
    {
        var pool = new FakeWorkerPoolRepository();
        var messages = new CapturingMessageRepository();
        await WorkerTools.RegisterWorkerRun(pool, "proj", 1245, "runner", "coder", run_id: "run-trigger-skip", verbose: true);

        await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-trigger-skip",
            requested_by: "coder",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: summary,
            branch: "task/1245-foo",
            head_commit: "0123456789abcdef0123456789abcdef01234567",
            tests_run: "[\"dotnet test: passed\"]",
            scope_parent_closable: parentClosable,
            verbose: true);

        var stored = await messages.GetMessagesAsync("proj", taskId: 1245, limit: 10);
        Assert.DoesNotContain(stored, IsScopeAuditTrigger);
    }

    [Fact]
    public async Task PostScopeAuditPacket_AcceptanceGapSuspected_RoutesPlannerNotification()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(WorkerPoolStates.Running, role: "scope_auditor", runId: "run-audit-route"));
        var messages = new CapturingMessageRepository();

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-audit-route",
            requested_by: "scope-auditor",
            status: "completed",
            role: "scope_auditor",
            packet_type: "scope_audit_packet",
            summary: "Audited task #1245: acceptance gap suspected.",
            audit_verdict: "acceptance_gap_suspected",
            audit_evidence_checked: "implementation packet, review findings, live projection smoke",
            audit_recommended_route: "planner",
            audited_head_commit: "0123456789abcdef0123456789abcdef01234567",
            audited_review_round_id: 12,
            verbose: true);

        using var doc = JsonDocument.Parse(completion);
        var sourceMessageId = doc.RootElement.GetProperty("completion").GetProperty("message_id").GetInt32();
        var stored = await messages.GetMessagesAsync("proj", taskId: 1245, limit: 10);
        var route = Assert.Single(stored, IsScopeAuditPlannerRoute);
        Assert.Equal(sourceMessageId, route.Metadata!.Value.GetProperty("source_completion_message_id").GetInt32());
        Assert.Equal("planner", route.Metadata!.Value.GetProperty("recipient_role").GetString());
        Assert.True(route.Metadata!.Value.GetProperty("fallback_notification").GetBoolean());
        Assert.Contains("Planner decision", route.Content);
    }

    [Fact]
    public async Task PrepareCoderContextPacket_IncludesScopeAccountingInstructions()
    {
        var tasks = new PacketToolsScopeFakeTaskRepository();
        tasks.SetupTask(1, "test-proj");
        var messages = new PacketToolsScopeFakeMessageRepository();

        var result = await PacketTools.PrepareCoderContextPacket(
            tasks, messages, "test-proj", 1, "test-user",
            completion_reporting_mode: "worker_mcp_tool", verbose: true);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("packet").GetProperty("content").GetString()!;
        Assert.Contains("Scope accounting", content);
        Assert.Contains("scope_acceptance", content);
        Assert.Contains("scope_deferred", content);
        Assert.Contains("scope_follow_ups", content);
        Assert.Contains("acceptance_gap_candidate", content);
        Assert.Contains("#1956", content);
    }

    // ─── Scope auditor tests ─────────────────────────────────────────────

    [Fact]
    public async Task PostScopeAuditPacket_StoresAndRetrievesAllAuditFields()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(WorkerPoolStates.Running, role: "scope_auditor", runId: "run-audit-1"));
        var messages = new CapturingMessageRepository();

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-audit-1",
            requested_by: "scope-auditor",
            status: "completed",
            role: "scope_auditor",
            packet_type: "scope_audit_packet",
            summary: "Audited task #1245: scope_ok.",
            branch: "task/1245-foo",
            head_commit: "0123456789abcdef0123456789abcdef01234567",
            tests_run: "[\"dotnet test: passed\"]",
            audit_verdict: "scope_ok",
            audit_evidence_checked: "implementation packet #99, review findings, follow-up #2000 (polish)",
            audit_recommended_route: "planner",
            audited_head_commit: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            audited_review_round_id: 1,
            scope_follow_ups: "[{\"task_id\":2000,\"title\":\"Polish\",\"classification\":\"polish\"}]",
            verbose: true);

        using var completionDoc = JsonDocument.Parse(completion);
        Assert.Equal("present", completionDoc.RootElement.GetProperty("completion_state").GetString());

        var content = completionDoc.RootElement.GetProperty("completion").GetProperty("content").GetString()!;
        Assert.Contains("Scope audit", content);
        Assert.Contains("scope_ok", content);
        Assert.Contains("implementation packet #99", content);
        Assert.Contains("planner", content);
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", content);
        Assert.Contains("#1", content);

        var metadata = completionDoc.RootElement.GetProperty("completion").GetProperty("metadata");
        Assert.Equal("scope_ok", metadata.GetProperty("audit_verdict").GetString());
        Assert.Equal("implementation packet #99, review findings, follow-up #2000 (polish)", metadata.GetProperty("audit_evidence_checked").GetString());
        Assert.Equal("planner", metadata.GetProperty("audit_recommended_route").GetString());
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", metadata.GetProperty("audited_head_commit").GetString());
        Assert.Equal(1, metadata.GetProperty("audited_review_round_id").GetInt32());
        Assert.True(metadata.TryGetProperty("scope_follow_ups", out _));

        // Retrieve via get_latest_worker_completion
        var latest = await CompletionTools.GetLatestWorkerCompletion(messages, "proj", run_id: "run-audit-1", verbose: true);
        using var latestDoc = JsonDocument.Parse(latest);
        var latestMeta = latestDoc.RootElement.GetProperty("completion").GetProperty("metadata");
        Assert.Equal("scope_ok", latestMeta.GetProperty("audit_verdict").GetString());
        Assert.Equal("planner", latestMeta.GetProperty("audit_recommended_route").GetString());
    }

    [Fact]
    public async Task PostScopeAuditPacket_WithoutVerdict_IsMalformed()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(WorkerPoolStates.Running, role: "scope_auditor", runId: "run-audit-nov"));
        var messages = new CapturingMessageRepository();

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-audit-nov",
            requested_by: "scope-auditor",
            status: "completed",
            role: "scope_auditor",
            packet_type: "scope_audit_packet",
            summary: "Missing verdict.",
            verbose: true);

        using var doc = JsonDocument.Parse(completion);
        Assert.Equal("malformed", doc.RootElement.GetProperty("completion_state").GetString());
        var content = doc.RootElement.GetProperty("completion").GetProperty("content").GetString()!;
        Assert.Contains("requires audit_verdict", content);
    }

    [Fact]
    public async Task PostScopeAuditPacket_WithInvalidVerdict_ReportsUnknownVerdict()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(WorkerPoolStates.Running, role: "scope_auditor", runId: "run-audit-badv"));
        var messages = new CapturingMessageRepository();

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-audit-badv",
            requested_by: "scope-auditor",
            status: "completed",
            role: "scope_auditor",
            packet_type: "scope_audit_packet",
            summary: "Bad verdict.",
            audit_verdict: "not_a_real_verdict",
            verbose: true);

        using var doc = JsonDocument.Parse(completion);
        Assert.Equal("malformed", doc.RootElement.GetProperty("completion_state").GetString());
        var content = doc.RootElement.GetProperty("completion").GetProperty("content").GetString()!;
        Assert.Contains("not_a_real_verdict", content);
        Assert.Contains("recognized verdict", content);
    }

    [Fact]
    public async Task PostScopeAuditPacket_AcceptanceGapSuspected_StoresCorrectly()
    {
        var pool = new FakeWorkerPoolRepository()
            .AddAssignment(NewAssignment(WorkerPoolStates.Running, role: "scope_auditor", runId: "run-audit-gap"));
        var messages = new CapturingMessageRepository();

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "proj",
            run_id: "run-audit-gap",
            requested_by: "scope-auditor",
            status: "completed",
            role: "scope_auditor",
            packet_type: "scope_audit_packet",
            summary: "Audited #1956-style task: lifecycle API foundation exists but live projection is empty.",
            audit_verdict: "acceptance_gap_suspected",
            audit_evidence_checked: "implementation packet, review findings, live /api/agent-work/current returns empty",
            audit_recommended_route: "runner",
            audited_head_commit: "1cf884ab6f614933ae4497aeb340f44b5eda15d0",
            audited_review_round_id: 1075,
            scope_follow_ups: "[{\"task_id\":1972,\"title\":\"Use producer deadlines in current projection\",\"classification\":\"acceptance_gap_candidate\"}]",
            scope_parent_closable: "No — follow-up #1972 is required for parent-task acceptance",
            verbose: true);

        using var doc = JsonDocument.Parse(completion);
        Assert.Equal("present", doc.RootElement.GetProperty("completion_state").GetString());

        var metadata = doc.RootElement.GetProperty("completion").GetProperty("metadata");
        Assert.Equal("acceptance_gap_suspected", metadata.GetProperty("audit_verdict").GetString());
        Assert.Equal("runner", metadata.GetProperty("audit_recommended_route").GetString());
        Assert.Equal("1cf884ab6f614933ae4497aeb340f44b5eda15d0", metadata.GetProperty("audited_head_commit").GetString());
        Assert.Equal(1075, metadata.GetProperty("audited_review_round_id").GetInt32());
    }

    [Fact]
    public async Task PrepareScopeAuditorContextPacket_ContainsExpectedInstructions()
    {
        var tasks = new PacketToolsScopeFakeTaskRepository();
        tasks.SetupTask(1, "test-proj");
        var messages = new PacketToolsScopeFakeMessageRepository();

        var result = await PacketTools.PrepareScopeAuditorContextPacket(
            tasks, messages, "test-proj", 1, "test-user",
            completion_reporting_mode: "worker_mcp_tool", verbose: true);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("packet").GetProperty("content").GetString()!;
        Assert.Contains("scope_auditor", content);
        Assert.Contains("completion semantics", content);
        Assert.Contains("acceptance_gap_candidate", content);
        Assert.Contains("unilaterally reopen or close tasks", content);
        Assert.Contains("#1956", content);
        Assert.Contains("audit_verdict", content);
        Assert.Contains("scope_ok", content);
        Assert.Contains("audit_inconclusive", content);
        Assert.Contains("code quality or product ownership", content);

        var metadata = doc.RootElement.GetProperty("packet").GetProperty("metadata");
        Assert.Equal("scope_auditor_context_packet", metadata.GetProperty("type").GetString());
        Assert.Equal("scope_auditor", metadata.GetProperty("role").GetString());
    }

    private static bool IsScopeAuditTrigger(Message message) =>
        MetadataString(message, "type") == "scope_audit_trigger";

    private static bool IsScopeAuditPlannerRoute(Message message) =>
        MetadataString(message, "type") == "scope_audit_planner_route";

    private static string? MetadataString(Message message, string key)
    {
        if (message.Metadata is JsonElement meta && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(key, out var prop))
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
        return null;
    }

    private static WorkerAssignment NewAssignment(string state, string projectId = "proj", string role = "coder", string runId = "run-1") => new()
    {
        Id = 1,
        WorkerIdentity = "worker-1",
        RunId = runId,
        ProjectId = projectId,
        TaskId = 1245,
        Role = role,
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

    private sealed class PacketToolsScopeFakeMessageRepository : IMessageRepository
    {
        private readonly List<Message> _messages = [];

        public Task<Message> CreateAsync(Message message)
        {
            message.Id = _messages.Count + 1;
            message.Sender ??= "test-user";
            message.CreatedAt = DateTime.UtcNow;
            _messages.Add(message);
            return Task.FromResult(message);
        }

        public Task<Message?> GetByIdAsync(int id) => Task.FromResult(_messages.FirstOrDefault(m => m.Id == id));
        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null, DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null)
            => Task.FromResult(new List<Message>());
        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null)
            => Task.FromResult(new List<MessageFeedItem>());
        public Task<Thread> GetThreadAsync(int threadId)
            => Task.FromResult(new Thread
            {
                Root = new Message { Id = 1, ProjectId = "test", Sender = "test-user", Content = "root", },
                Replies = new List<Message>(),
            });
        public Task<int> MarkReadAsync(string agent, int[] messageIds) => Task.FromResult(messageIds.Length);
        public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(string? projectId = null, int? taskId = null, string? sender = null, string? metadataType = null, string? urgency = null, bool? isRead = null, string? readForAgent = null, int limit = 20, int offset = 0)
            => Task.FromResult(new List<NotificationFeedItem>());
        public Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds) => Task.FromResult(notificationIds?.Length ?? 0);
        public Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null) => Task.FromResult(0);
    }

    private sealed class PacketToolsScopeFakeTaskRepository : ITaskRepository
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
                ReviewWorkflow = new ReviewWorkflowSummary { Timeline = new List<ReviewTimelineEntry>(), },
            };
        }

        public Task<TaskDetail> GetDetailAsync(int id) => Task.FromResult(_detail!);
        public Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null) => Task.FromResult(task);
        public Task<ProjectTask?> GetByIdAsync(int id) => Task.FromResult<ProjectTask?>(null);
        public Task<TaskWorkflowSummary> GetWorkflowSummaryAsync(int id) =>
            Task.FromResult(new TaskWorkflowSummary
            {
                Id = id, ProjectId = "test", Title = "Test", Status = "in_progress",
                Dependencies = new List<TaskDependencyInfo>(), Subtasks = new List<CompactSubtaskEntry>(),
                ReviewWorkflow = new CompactReviewWorkflow { Timeline = new List<CompactReviewRoundRef>(), },
                RecentMessages = new List<CompactMessageHeader>(),
                UnresolvedFindings = new List<CompactFindingEntry>(),
                DeepReadHint = "Use get_task for full details.", Availability = "in_progress",
            });
        public Task<List<TaskSummary>> ListAsync(string projectId, DenCore.Models.TaskStatus[]? statuses = null,
            string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null, bool includeAll = false)
            => Task.FromResult(new List<TaskSummary>());
        public Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent)
            => Task.FromResult(new ProjectTask { Id = id, ProjectId = "test", Title = "Updated", Status = DenCore.Models.TaskStatus.InProgress, Priority = 1, });
        public Task AddDependencyAsync(int taskId, int dependsOn) => Task.CompletedTask;
        public Task RemoveDependencyAsync(int taskId, int dependsOn) => Task.CompletedTask;
        public Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null) => Task.FromResult<ProjectTask?>(null);
    }
}
