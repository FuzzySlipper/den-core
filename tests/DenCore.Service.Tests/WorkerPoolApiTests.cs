using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenCore.Service.Tests;

public sealed class WorkerPoolApiTests : IAsyncLifetime
{
    private readonly string _projectId = $"wp-api-test-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private WorkerPoolAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WorkerPoolAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(_projectId) is null)
            await projects.CreateAsync(new Project { Id = _projectId, Name = "Worker Pool API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<string> SeedMemberAsync(string identity, string profileIdentity = "")
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var member = await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            ProfileIdentity = profileIdentity,
            DisplayName = "API Test Worker",
            Capabilities = """["coder","dotnet"]""",
            Status = WorkerPoolStates.MemberAvailable,
        });
        return member.WorkerIdentity;
    }

    private async Task<string> SeedProjectOrchestratorMemberAsync(string identity, string profileIdentity = "")
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var member = await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            ProfileIdentity = profileIdentity,
            WorkerRole = "project_orchestrator",
            DisplayName = "API Test Orchestrator",
            Capabilities = """["planning","den-coordination"]""",
            Status = WorkerPoolStates.MemberAvailable,
            AgentInstanceId = $"hermes:test:{identity}:live",
            AdapterInstanceId = $"adapter:{identity}",
            SessionId = $"session-{identity}",
        });
        return member.WorkerIdentity;
    }

    /// <summary>Create a worker and lease it immediately, returning the assignment for follow-up ops.</summary>
    private async Task<(string workerIdentity, int assignmentId, string runId)> SeedAndLeaseAsync(string prefix)
    {
        var workerId = $"{prefix}-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        var runId = $"run-{prefix}-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var lease = await repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = _projectId,
            Role = "coder",
            AssignedBy = "runner",
            RunId = runId,
            PreferredWorkerIdentity = workerId,
        });
        Assert.NotNull(lease);
        return (workerId, lease.Id, runId);
    }

    private async Task<ProjectTask> CreateInProgressTaskAsync(string title)
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = _projectId,
            Title = title,
            Status = DenCore.Models.TaskStatus.InProgress,
            Description = "Task description"
        });
    }

    private async Task CreateWorkerFailurePacketAsync(int taskId, string role, string? failureCategory = null)
    {
        using var scope = _factory.Services.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "worker_failure_packet",
            ["packet_kind"] = "worker_failure_packet",
            ["role"] = role,
            ["project_id"] = _projectId,
            ["task_id"] = taskId,
            ["run_id"] = $"run-failure-{role}-{Guid.NewGuid():N}",
            ["status"] = "failed",
            ["failure_category"] = failureCategory,
        }, JsonOpts);

        await messages.CreateAsync(new Message
        {
            ProjectId = _projectId,
            TaskId = taskId,
            Sender = role,
            Intent = MessageIntent.StatusUpdate,
            Content = "# worker_failure_packet",
            Metadata = metadata
        });
    }

    private async Task<Message> CreateWorkerCompletionPacketAsync(
        int taskId,
        string role,
        string packetType,
        string status,
        string branch,
        string headCommit,
        string? runId = null,
        string? testsRun = "dotnet test --no-restore: passed")
    {
        using var scope = _factory.Services.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = packetType,
            ["packet_kind"] = packetType,
            ["schema"] = "den_worker_completion",
            ["completion_packet"] = true,
            ["role"] = role,
            ["project_id"] = _projectId,
            ["task_id"] = taskId,
            ["run_id"] = runId ?? $"run-{role}-{Guid.NewGuid():N}",
            ["status"] = status,
            ["branch"] = branch,
            ["head_commit"] = headCommit,
            ["tests_run"] = testsRun,
        }, JsonOpts);

        return await messages.CreateAsync(new Message
        {
            ProjectId = _projectId,
            TaskId = taskId,
            Sender = role,
            Intent = MessageIntent.StatusUpdate,
            Content = $"# {packetType}",
            Metadata = metadata
        });
    }

    private async Task<Message> CreateWorkerContextPacketAsync(
        int taskId,
        string role,
        string packetType,
        string branch,
        string headCommit,
        string runId)
    {
        using var scope = _factory.Services.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = packetType,
            ["packet_kind"] = packetType,
            ["schema"] = "den_worker_packet",
            ["role"] = role,
            ["project_id"] = _projectId,
            ["task_id"] = taskId,
            ["run_id"] = runId,
            ["branch"] = branch,
            ["head_commit"] = headCommit,
            ["reference_only_launch"] = true,
        }, JsonOpts);

        return await messages.CreateAsync(new Message
        {
            ProjectId = _projectId,
            TaskId = taskId,
            Sender = "runner",
            Intent = MessageIntent.Handoff,
            Content = $"# {packetType}",
            Metadata = metadata
        });
    }

    private async Task<WorkerAssignment> SeedGateAssignmentAsync(int taskId, string role, string runId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var workerId = $"wp-{role}-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = workerId,
            WorkerRole = role,
            Status = WorkerPoolStates.MemberAvailable,
        });

        var assignment = await repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = _projectId,
            TaskId = taskId,
            Role = role,
            AssignedBy = "runner",
            RunId = runId,
            PreferredWorkerIdentity = workerId,
        });
        Assert.NotNull(assignment);
        return assignment;
    }

    private async Task<AgentStreamEntry> CreateGateWakeAsync(int taskId, string role, string runId, string headCommit)
    {
        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["run_id"] = runId,
            ["role"] = role,
            ["head_commit"] = headCommit,
            ["gate"] = role,
        }, JsonOpts);

        return await stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "worker_wake_requested",
            ProjectId = _projectId,
            TaskId = taskId,
            Sender = "den-host",
            RecipientRole = role,
            DeliveryMode = AgentStreamDeliveryMode.Wake,
            Body = $"wake {role}",
            Metadata = metadata,
            DedupKey = $"test-wake:{taskId}:{runId}",
        });
    }

    private async Task<JsonDocument> DetermineNextActionAsync(int taskId, int maxAttempts = 4)
    {
        using var scope = _factory.Services.CreateScope();
        var resultJson = await OrchestratorStateMachineTools.DetermineOrchestratorNextAction(
            scope.ServiceProvider.GetRequiredService<ITaskRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessageRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>(),
            scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>(),
            scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>(),
            _projectId,
            taskId,
            max_attempts: maxAttempts,
            verbose: true);

        return JsonDocument.Parse(resultJson);
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_OldFailedValidationNewHeadActiveValidator_WaitsInFlight()
    {
        var task = await CreateInProgressTaskAsync("Head-aware validation waits for active current-head gate");
        var headA = $"a{Guid.NewGuid():N}";
        var headB = $"b{Guid.NewGuid():N}";
        var runId = $"run-validator-{Guid.NewGuid():N}";

        var oldValidation = await CreateWorkerCompletionPacketAsync(
            task.Id,
            "validator",
            "validation_packet",
            "failed",
            "task/head-aware-gates",
            headA);
        var implementation = await CreateWorkerCompletionPacketAsync(
            task.Id,
            "coder",
            "implementation_packet",
            "completed",
            "task/head-aware-gates",
            headB);
        var context = await CreateWorkerContextPacketAsync(
            task.Id,
            "validator",
            "validator_context_packet",
            "task/head-aware-gates",
            headB,
            runId);
        var assignment = await SeedGateAssignmentAsync(task.Id, "validator", runId);
        var wake = await CreateGateWakeAsync(task.Id, "validator", runId, headB);

        using var doc = await DetermineNextActionAsync(task.Id);
        var root = doc.RootElement;
        Assert.Equal("wait_in_flight", root.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("validation_in_flight", root.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(headB, root.GetProperty("workflow_head").GetProperty("head_commit").GetString());
        Assert.Equal(implementation.Id, root.GetProperty("workflow_head").GetProperty("implementation_packet_id").GetInt32());

        var validationGate = root.GetProperty("gate_projection").GetProperty("validation");
        Assert.Equal("in_flight", validationGate.GetProperty("state").GetString());
        Assert.Equal(oldValidation.Id, validationGate.GetProperty("superseded_packets")[0].GetProperty("message_id").GetInt32());
        var active = validationGate.GetProperty("active_runs")[0];
        Assert.Equal(runId, active.GetProperty("run_id").GetString());
        Assert.Equal(assignment.Id, active.GetProperty("assignment_id").GetInt32());
        Assert.Equal(context.Id, active.GetProperty("context_packet_id").GetInt32());
        Assert.Equal(wake.Id, active.GetProperty("wake_event_id").GetInt32());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_OldFailedValidationNewHeadNoActiveGate_LaunchesValidatorWithHeadDedupeKey()
    {
        var task = await CreateInProgressTaskAsync("Head-aware validation launches only for current head");
        var headA = $"a{Guid.NewGuid():N}";
        var headB = $"b{Guid.NewGuid():N}";

        var oldValidation = await CreateWorkerCompletionPacketAsync(
            task.Id,
            "validator",
            "validation_packet",
            "failed",
            "task/head-aware-gates",
            headA);
        await CreateWorkerCompletionPacketAsync(
            task.Id,
            "coder",
            "implementation_packet",
            "completed",
            "task/head-aware-gates",
            headB);

        using var doc = await DetermineNextActionAsync(task.Id);
        var root = doc.RootElement;
        Assert.Equal("launch_validator", root.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("validation_missing_for_current_head", root.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal($"gate:{task.Id}:{headB}:validator:gate-policy-v1", root.GetProperty("decision").GetProperty("dedupe_key").GetString());

        var validationGate = root.GetProperty("gate_projection").GetProperty("validation");
        Assert.Equal("missing", validationGate.GetProperty("state").GetString());
        Assert.Equal(oldValidation.Id, validationGate.GetProperty("superseded_packets")[0].GetProperty("message_id").GetInt32());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_MultipleActiveValidatorRunsForCurrentHead_NeedsReconcile()
    {
        var task = await CreateInProgressTaskAsync("Head-aware validation fails closed on ambiguous active gates");
        var headB = $"b{Guid.NewGuid():N}";
        var runIdOne = $"run-validator-{Guid.NewGuid():N}";
        var runIdTwo = $"run-validator-{Guid.NewGuid():N}";

        await CreateWorkerCompletionPacketAsync(
            task.Id,
            "coder",
            "implementation_packet",
            "completed",
            "task/head-aware-gates",
            headB);
        await CreateWorkerContextPacketAsync(
            task.Id,
            "validator",
            "validator_context_packet",
            "task/head-aware-gates",
            headB,
            runIdOne);
        await CreateWorkerContextPacketAsync(
            task.Id,
            "validator",
            "validator_context_packet",
            "task/head-aware-gates",
            headB,
            runIdTwo);
        var assignmentOne = await SeedGateAssignmentAsync(task.Id, "validator", runIdOne);
        var assignmentTwo = await SeedGateAssignmentAsync(task.Id, "validator", runIdTwo);
        await CreateGateWakeAsync(task.Id, "validator", runIdOne, headB);
        await CreateGateWakeAsync(task.Id, "validator", runIdTwo, headB);

        using var doc = await DetermineNextActionAsync(task.Id);
        var root = doc.RootElement;
        Assert.Equal("needs_reconcile", root.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("validation_ambiguous_in_flight", root.GetProperty("decision").GetProperty("reason").GetString());
        Assert.True(root.GetProperty("fail_closed").GetBoolean());

        var activeRuns = root.GetProperty("gate_projection").GetProperty("validation").GetProperty("active_runs").EnumerateArray().ToList();
        Assert.Equal(2, activeRuns.Count);
        var assignmentIds = activeRuns.Select(run => run.GetProperty("assignment_id").GetInt32()).ToHashSet();
        Assert.Contains(assignmentOne.Id, assignmentIds);
        Assert.Contains(assignmentTwo.Id, assignmentIds);
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_NewerFailedSameHeadValidationWinsOverOlderPass()
    {
        var task = await CreateInProgressTaskAsync("Current-head validation uses newest same-head packet");
        var head = $"b{Guid.NewGuid():N}";

        var oldPass = await CreateWorkerCompletionPacketAsync(
            task.Id,
            "validator",
            "validation_packet",
            "completed",
            "task/head-aware-gates",
            head);
        var newerFailure = await CreateWorkerCompletionPacketAsync(
            task.Id,
            "validator",
            "validation_packet",
            "failed",
            "task/head-aware-gates",
            head);
        await CreateWorkerCompletionPacketAsync(
            task.Id,
            "coder",
            "implementation_packet",
            "completed",
            "task/head-aware-gates",
            head);

        using var doc = await DetermineNextActionAsync(task.Id);
        var root = doc.RootElement;
        Assert.Equal("launch_coder", root.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("validation_failed", root.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(newerFailure.Id, root.GetProperty("gate_projection").GetProperty("validation").GetProperty("terminal_packet").GetProperty("message_id").GetInt32());
        Assert.NotEqual(oldPass.Id, root.GetProperty("latest_packets").GetProperty("validation").GetProperty("message_id").GetInt32());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_ActiveValidatorRerunForFailedCurrentHead_WaitsInFlight()
    {
        var task = await CreateInProgressTaskAsync("Current-head active validator rerun suppresses stale failure action");
        var head = $"b{Guid.NewGuid():N}";
        var runId = $"run-validator-{Guid.NewGuid():N}";

        await CreateWorkerCompletionPacketAsync(
            task.Id,
            "validator",
            "validation_packet",
            "failed",
            "task/head-aware-gates",
            head);
        await CreateWorkerCompletionPacketAsync(
            task.Id,
            "coder",
            "implementation_packet",
            "completed",
            "task/head-aware-gates",
            head);
        await CreateWorkerContextPacketAsync(
            task.Id,
            "validator",
            "validator_context_packet",
            "task/head-aware-gates",
            head,
            runId);
        var assignment = await SeedGateAssignmentAsync(task.Id, "validator", runId);
        await CreateGateWakeAsync(task.Id, "validator", runId, head);

        using var doc = await DetermineNextActionAsync(task.Id);
        var root = doc.RootElement;
        Assert.Equal("wait_in_flight", root.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("validation_in_flight", root.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(assignment.Id, root.GetProperty("gate_projection").GetProperty("validation").GetProperty("active_runs")[0].GetProperty("assignment_id").GetInt32());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_CorrelatedAndUncorrelatedActiveValidatorRuns_NeedsReconcile()
    {
        var task = await CreateInProgressTaskAsync("Current-head validation treats mixed active assignments as ambiguous");
        var head = $"b{Guid.NewGuid():N}";
        var correlatedRunId = $"run-validator-{Guid.NewGuid():N}";
        var uncorrelatedRunId = $"run-validator-{Guid.NewGuid():N}";

        await CreateWorkerCompletionPacketAsync(
            task.Id,
            "coder",
            "implementation_packet",
            "completed",
            "task/head-aware-gates",
            head);
        await CreateWorkerContextPacketAsync(
            task.Id,
            "validator",
            "validator_context_packet",
            "task/head-aware-gates",
            head,
            correlatedRunId);
        var correlatedAssignment = await SeedGateAssignmentAsync(task.Id, "validator", correlatedRunId);
        var uncorrelatedAssignment = await SeedGateAssignmentAsync(task.Id, "validator", uncorrelatedRunId);
        await CreateGateWakeAsync(task.Id, "validator", correlatedRunId, head);

        using var doc = await DetermineNextActionAsync(task.Id);
        var root = doc.RootElement;
        Assert.Equal("needs_reconcile", root.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("validation_ambiguous_in_flight", root.GetProperty("decision").GetProperty("reason").GetString());
        var activeRuns = root.GetProperty("gate_projection").GetProperty("validation").GetProperty("active_runs").EnumerateArray().ToList();
        Assert.Equal(2, activeRuns.Count);
        var assignmentIds = activeRuns.Select(run => run.GetProperty("assignment_id").GetInt32()).ToHashSet();
        Assert.Contains(correlatedAssignment.Id, assignmentIds);
        Assert.Contains(uncorrelatedAssignment.Id, assignmentIds);
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_DoneTask_HoldsWithoutWorkerLaunch()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var reviewRounds = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        var reviewFindings = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        var terminalTask = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = _projectId,
            Title = "Terminal task replay guard",
            Status = DenCore.Models.TaskStatus.Done,
        });

        var resultJson = await OrchestratorStateMachineTools.DetermineOrchestratorNextAction(
            tasks,
            messages,
            reviewRounds,
            reviewFindings,
            _projectId,
            terminalTask.Id,
            verbose: true);

        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal("hold", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("terminal_or_blocked_task", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.True(doc.RootElement.GetProperty("fail_closed").GetBoolean());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_DefaultRetryCap_AllowsFourthAttemptAfterThreeFailures()
    {
        var task = await CreateInProgressTaskAsync("Default cap allows fourth coder attempt");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");

        using var scope = _factory.Services.CreateScope();
        var resultJson = await OrchestratorStateMachineTools.DetermineOrchestratorNextAction(
            scope.ServiceProvider.GetRequiredService<ITaskRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessageRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>(),
            _projectId,
            task.Id,
            verbose: true);

        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal("launch_coder", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("missing_implementation", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("attempts").GetProperty("coder").GetInt32());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_InfrastructureFailures_DoNotConsumeRetryBudget()
    {
        var task = await CreateInProgressTaskAsync("Infrastructure failures do not spend coder attempts");
        await CreateWorkerFailurePacketAsync(task.Id, "coder", failureCategory: "infrastructure");
        await CreateWorkerFailurePacketAsync(task.Id, "coder", failureCategory: "no_capacity");
        await CreateWorkerFailurePacketAsync(task.Id, "coder", failureCategory: "auth_expired");
        await CreateWorkerFailurePacketAsync(task.Id, "coder", failureCategory: "channel_membership_missing");

        using var scope = _factory.Services.CreateScope();
        var resultJson = await OrchestratorStateMachineTools.DetermineOrchestratorNextAction(
            scope.ServiceProvider.GetRequiredService<ITaskRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessageRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>(),
            _projectId,
            task.Id,
            verbose: true);

        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal("launch_coder", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("missing_implementation", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("attempts").GetProperty("coder").GetInt32());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_DefaultRetryCap_EscalatesAfterFourFailures()
    {
        var task = await CreateInProgressTaskAsync("Default cap escalates at fourth coder failure");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");

        using var scope = _factory.Services.CreateScope();
        var resultJson = await OrchestratorStateMachineTools.DetermineOrchestratorNextAction(
            scope.ServiceProvider.GetRequiredService<ITaskRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessageRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>(),
            _projectId,
            task.Id,
            verbose: true);

        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal("escalate", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("missing_implementation_retry_cap", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("attempts").GetProperty("coder").GetInt32());
    }

    [Fact]
    public async Task DetermineOrchestratorNextAction_ExplicitRetryCapThree_PreservesOldEscalationBoundary()
    {
        var task = await CreateInProgressTaskAsync("Explicit cap three remains honored");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");
        await CreateWorkerFailurePacketAsync(task.Id, "coder");

        using var scope = _factory.Services.CreateScope();
        var resultJson = await OrchestratorStateMachineTools.DetermineOrchestratorNextAction(
            scope.ServiceProvider.GetRequiredService<ITaskRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessageRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>(),
            _projectId,
            task.Id,
            max_attempts: 3,
            verbose: true);

        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal("escalate", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("missing_implementation_retry_cap", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("attempts").GetProperty("coder").GetInt32());
    }

    // ── Member CRUD via REST ───────────────────────────────────────────

    [Fact]
    public async Task PostMember_CreatesAndReturns()
    {
        var identity = $"rest-create-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync("/api/worker-pool/members", new
        {
            worker_identity = identity,
            display_name = "Rest Created",
            status = "available",
            capabilities = """["coder"]""",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(identity, doc.RootElement.GetProperty("worker_identity").GetString());
        Assert.Equal("available", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostMember_BadRequest_WhenMissingIdentity()
    {
        var response = await _client.PostAsJsonAsync("/api/worker-pool/members", new
        {
            status = "available",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetMembers_ReturnsList()
    {
        var id1 = $"wp-list-1-{Guid.NewGuid():N}";
        var id2 = $"wp-list-2-{Guid.NewGuid():N}";
        await SeedMemberAsync(id1);
        await SeedMemberAsync(id2);

        var response = await _client.GetAsync("/api/worker-pool/members");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var members = doc.RootElement.GetProperty("members").EnumerateArray().ToList();
        Assert.Contains(members, m => m.GetProperty("worker_identity").GetString() == id1);
        Assert.Contains(members, m => m.GetProperty("worker_identity").GetString() == id2);
    }

    [Fact]
    public async Task GetMembers_FiltersByStatus()
    {
        var availId = $"wp-filter-avail-{Guid.NewGuid():N}";
        var busyId = $"wp-filter-busy-{Guid.NewGuid():N}";
        await SeedMemberAsync(availId);
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
            await repo.UpsertMemberAsync(new WorkerPoolMember
            {
                WorkerIdentity = busyId,
                Status = WorkerPoolStates.MemberBusy,
            });
        }

        var response = await _client.GetAsync($"/api/worker-pool/members?status=busy&workerIdentity={busyId}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var members = doc.RootElement.GetProperty("members").EnumerateArray().ToList();
        Assert.Single(members);
        Assert.Equal(busyId, members[0].GetProperty("worker_identity").GetString());
    }

    [Fact]
    public async Task GetMemberByIdentity_ReturnsMember()
    {
        var identity = $"wp-get-{Guid.NewGuid():N}";
        await SeedMemberAsync(identity);

        var response = await _client.GetAsync($"/api/worker-pool/members/{identity}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(identity, doc.RootElement.GetProperty("worker_identity").GetString());
    }

    [Fact]
    public async Task GetMemberByIdentity_NotFound_Returns404()
    {
        var response = await _client.GetAsync($"/api/worker-pool/members/nonexistent-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Lease workflow via REST ────────────────────────────────────────

    [Fact]
    public async Task LeaseWorker_CreatesAssignment()
    {
        var workerId = $"wp-lease-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        var response = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = $"run-lease-{Guid.NewGuid():N}",
            preferred_worker_identity = workerId,
        });
        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var assignment = doc.RootElement;
        Assert.Equal(workerId, assignment.GetProperty("worker_identity").GetString());
        Assert.Equal(_projectId, assignment.GetProperty("project_id").GetString());
        Assert.Equal("coder", assignment.GetProperty("role").GetString());
        Assert.Equal("ack", assignment.GetProperty("state").GetString());
        Assert.NotNull(assignment.GetProperty("acquired_at").GetString());
    }

    [Fact]
    public async Task LeaseWorker_NoAvailable_ReturnsConflict()
    {
        // Use a preferred worker that doesn't exist to ensure conflict
        var response = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = $"run-noavail-{Guid.NewGuid():N}",
            preferred_worker_identity = $"nonexistent-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task LeaseWorker_WithPreferredWorker_Success()
    {
        var prefId = $"wp-pref-{Guid.NewGuid():N}";
        await SeedMemberAsync(prefId);

        var response = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "reviewer",
            assigned_by = "runner",
            run_id = $"run-pref-{Guid.NewGuid():N}",
            preferred_worker_identity = prefId,
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(prefId, doc.RootElement.GetProperty("worker_identity").GetString());
    }

    [Fact]
    public async Task LeaseWorker_BadRequest_WhenMissingFields()
    {
        // Send empty JSON — the route handler should return 400
        var response = await _client.PostAsJsonAsync("/api/worker-pool/leases", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Assignments ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignments_ReturnsList()
    {
        var (w1, a1, _) = await SeedAndLeaseAsync("asgn-list-a");
        var (w2, a2, _) = await SeedAndLeaseAsync("asgn-list-b");

        var response = await _client.GetAsync("/api/worker-pool/assignments");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var assignments = doc.RootElement.GetProperty("assignments").EnumerateArray().ToList();
        var ids = assignments.Select(a => a.GetProperty("id").GetInt32()).ToHashSet();
        Assert.Contains(a1, ids);
        Assert.Contains(a2, ids);
    }

    [Fact]
    public async Task GetAssignmentById_ReturnsAssignment()
    {
        var (workerId, assignmentId, _) = await SeedAndLeaseAsync("asgn-id");

        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();

        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal(assignmentId, getDoc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task GetAssignmentByRun_ReturnsAssignment()
    {
        var workerId = $"wp-asgn-run-{Guid.NewGuid():N}";
        var runId = $"run-by-run-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        var leaseResp = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = runId,
            preferred_worker_identity = workerId,
        });
        leaseResp.EnsureSuccessStatusCode();

        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/by-run/{runId}");
        getResp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal(runId, doc.RootElement.GetProperty("run_id").GetString());
    }

    [Fact]
    public async Task TransitionAssignment_ValidTransition()
    {
        var (workerId, assignmentId, _) = await SeedAndLeaseAsync("trans");

        var transResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/transition", new
        {
            state = "running",
        });
        transResp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await transResp.Content.ReadAsStringAsync());
        Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
    }

    // ── Checkpoint workflow via REST ───────────────────────────────────

    [Fact]
    public async Task AppendCheckpoint_CreatesAndReturns()
    {
        var (workerId, assignmentId, cpRunId) = await SeedAndLeaseAsync("cp-rest");

        var runId = cpRunId;
        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = """{"progress":"50%"}""",
        });
        cpResp.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, cpResp.StatusCode);

        using var doc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        Assert.Equal("progress", doc.RootElement.GetProperty("checkpoint_type").GetString());
        Assert.Equal(assignmentId, doc.RootElement.GetProperty("assignment_id").GetInt32());
    }

    [Fact]
    public async Task AppendCheckpoint_Completion_SetsStateAndReturnsMember()
    {
        var workerId = $"wp-cp-done-{Guid.NewGuid():N}";
        var runId = $"run-cp-done-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        // Lease via REST
        var leaseResp = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = runId,
            preferred_worker_identity = workerId,
        });
        leaseResp.EnsureSuccessStatusCode();
        using var leaseDoc = JsonDocument.Parse(await leaseResp.Content.ReadAsStringAsync());
        var assignmentId = leaseDoc.RootElement.GetProperty("id").GetInt32();

        // Append completion checkpoint
        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = """{"result":"success"}""",
        });
        cpResp.EnsureSuccessStatusCode();

        // Verify assignment state is completed via API
        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("completed", getDoc.RootElement.GetProperty("state").GetString());

        // Verify member is available again
        var memResp = await _client.GetAsync($"/api/worker-pool/members/{workerId}");
        memResp.EnsureSuccessStatusCode();
        using var memDoc = JsonDocument.Parse(await memResp.Content.ReadAsStringAsync());
        Assert.Equal("available", memDoc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AppendCheckpoint_ProgressAfterTerminal_ReturnsConflictAndKeepsCompletionLatest()
    {
        var (_, assignmentId, runId) = await SeedAndLeaseAsync("cp-terminal-guard");

        var completionResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = """{"result":"success"}""",
        });
        completionResp.EnsureSuccessStatusCode();
        using var completionDoc = JsonDocument.Parse(await completionResp.Content.ReadAsStringAsync());
        var completionCheckpointId = completionDoc.RootElement.GetProperty("id").GetInt32();

        var cleanupResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/cleanup", new
        {
            evidence = """{"reason":"terminal replay guard regression"}""",
        });
        cleanupResp.EnsureSuccessStatusCode();

        var releaseResp = await _client.PostAsync($"/api/worker-pool/assignments/{assignmentId}/release", null);
        releaseResp.EnsureSuccessStatusCode();

        var staleResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = """{"stage":"stale plan replay"}""",
        });
        Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);
        using var staleDoc = JsonDocument.Parse(await staleResp.Content.ReadAsStringAsync());
        Assert.Contains("terminal", staleDoc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("completed", getDoc.RootElement.GetProperty("state").GetString());
        Assert.Equal(completionCheckpointId, getDoc.RootElement.GetProperty("latest_checkpoint_id").GetInt32());

        var listResp = await _client.GetAsync($"/api/worker-pool/checkpoints?assignmentId={assignmentId}");
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        Assert.Equal(1, listDoc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task AppendCheckpoint_IdempotentCompletionAfterTerminal_DoesNotReopenAssignment()
    {
        var (_, assignmentId, runId) = await SeedAndLeaseAsync("cp-terminal-repost");

        var firstResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = """{"result":"success"}""",
        });
        firstResp.EnsureSuccessStatusCode();
        using var firstDoc = JsonDocument.Parse(await firstResp.Content.ReadAsStringAsync());
        var firstCheckpointId = firstDoc.RootElement.GetProperty("id").GetInt32();

        var repostResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = """{"result":"success","idempotent":true}""",
        });
        repostResp.EnsureSuccessStatusCode();

        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("completed", getDoc.RootElement.GetProperty("state").GetString());
        Assert.Equal(firstCheckpointId, getDoc.RootElement.GetProperty("latest_checkpoint_id").GetInt32());
    }

    [Fact]
    public async Task AppendCheckpoint_Failure_SetsState()
    {
        var (workerId, assignmentId, cpFailRunId) = await SeedAndLeaseAsync("cp-fail");
        var runId = cpFailRunId;

        await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "failure",
            payload = """{"error":"timeout"}""",
        });

        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("failed", getDoc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ListCheckpoints_Filters()
    {
        var (workerId, assignmentId, cpListRunId) = await SeedAndLeaseAsync("cp-list");
        var runId = cpListRunId;

        await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = "{}",
        });
        await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "checkpoint",
            payload = "{}",
        });

        var listResp = await _client.GetAsync($"/api/worker-pool/checkpoints?assignmentId={assignmentId}");
        listResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    // ── Checkpoint Responses via REST ──────────────────────────────────

    [Fact]
    public async Task AppendCheckpointResponse_Ack_RestoresRunning()
    {
        var (workerId, assignmentId, respAckRunId) = await SeedAndLeaseAsync("resp-ack");
        var runId = respAckRunId;

        // Append a checkpoint (moves to checkpoint_waiting)
        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = "{}",
        });
        cpResp.EnsureSuccessStatusCode();
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var checkpointId = cpDoc.RootElement.GetProperty("id").GetInt32();

        // Respond with ack
        var respResp = await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses", new
        {
            assignment_id = assignmentId,
            run_id = runId,
            response_type = "ack",
            payload = "{}",
        });
        respResp.EnsureSuccessStatusCode();

        // Verify state is running again
        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("running", getDoc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AppendCheckpointResponse_Abort_ExpiresAssignment()
    {
        var (workerId, assignmentId, respAbortRunId) = await SeedAndLeaseAsync("resp-abort");
        var runId = respAbortRunId;

        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = "{}",
        });
        cpResp.EnsureSuccessStatusCode();
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var checkpointId = cpDoc.RootElement.GetProperty("id").GetInt32();

        await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses", new
        {
            assignment_id = assignmentId,
            run_id = runId,
            response_type = "abort",
            payload = """{"reason":"cancelled"}""",
        });

        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("expired", getDoc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AppendCheckpointResponse_AckAfterTerminal_ReturnsConflictAndAbortIsAuditOnly()
    {
        var (_, assignmentId, runId) = await SeedAndLeaseAsync("resp-terminal-guard");

        var completionResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = """{"result":"success"}""",
        });
        completionResp.EnsureSuccessStatusCode();
        using var cpDoc = JsonDocument.Parse(await completionResp.Content.ReadAsStringAsync());
        var checkpointId = cpDoc.RootElement.GetProperty("id").GetInt32();

        var ackResp = await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses", new
        {
            assignment_id = assignmentId,
            run_id = runId,
            response_type = "ack",
            payload = """{"instruction":"stale plan replay"}""",
        });
        Assert.Equal(HttpStatusCode.Conflict, ackResp.StatusCode);
        using var ackDoc = JsonDocument.Parse(await ackResp.Content.ReadAsStringAsync());
        Assert.Contains("terminal", ackDoc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var abortResp = await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses", new
        {
            assignment_id = assignmentId,
            run_id = runId,
            response_type = "abort",
            payload = """{"reason":"stale terminal checkpoint response suppressed"}""",
        });
        abortResp.EnsureSuccessStatusCode();

        var getResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("completed", getDoc.RootElement.GetProperty("state").GetString());

        var responsesResp = await _client.GetAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses");
        responsesResp.EnsureSuccessStatusCode();
        using var responsesDoc = JsonDocument.Parse(await responsesResp.Content.ReadAsStringAsync());
        Assert.Equal(1, responsesDoc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("abort", responsesDoc.RootElement.GetProperty("responses")[0].GetProperty("response_type").GetString());
    }

    [Fact]
    public async Task ListCheckpointResponses_ReturnsResponses()
    {
        var (workerId, assignmentId, respListRunId) = await SeedAndLeaseAsync("resp-list");
        var runId = respListRunId;

        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = "{}",
        });
        cpResp.EnsureSuccessStatusCode();
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var checkpointId = cpDoc.RootElement.GetProperty("id").GetInt32();

        await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses", new
        {
            assignment_id = assignmentId,
            run_id = runId,
            response_type = "ack",
            payload = "{}",
        });
        await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses", new
        {
            assignment_id = assignmentId,
            run_id = runId,
            response_type = "guidance",
            payload = """{"msg":"continue"}""",
        });

        var listResp = await _client.GetAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses");
        listResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ListResponsesByRunId_ReturnsResponses()
    {
        var (workerId, assignmentId, runId) = await SeedAndLeaseAsync("resp-run");

        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = "{}",
        });
        cpResp.EnsureSuccessStatusCode();
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var checkpointId = cpDoc.RootElement.GetProperty("id").GetInt32();

        await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/{checkpointId}/responses", new
        {
            assignment_id = assignmentId,
            run_id = runId,
            response_type = "ack",
            payload = "{}",
        });

        var byRunResp = await _client.GetAsync($"/api/worker-pool/responses/by-run/{runId}");
        byRunResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await byRunResp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    // ── Cleanup & Release ──────────────────────────────────────────────

    [Fact]
    public async Task CleanupAndRelease_FullFlow()
    {
        var workerId = $"wp-clean-{Guid.NewGuid():N}";
        var runId = $"run-clean-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        var leaseResp = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = runId,
            preferred_worker_identity = workerId,
        });
        leaseResp.EnsureSuccessStatusCode();
        using var leaseDoc = JsonDocument.Parse(await leaseResp.Content.ReadAsStringAsync());
        var assignmentId = leaseDoc.RootElement.GetProperty("id").GetInt32();

        // Complete assignment via checkpoint
        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = """{"result":"ok"}""",
        });
        cpResp.EnsureSuccessStatusCode();

        // Record cleanup evidence
        var cleanResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/cleanup", new
        {
            evidence = """{"log":"/tmp/test.log"}""",
        });
        cleanResp.EnsureSuccessStatusCode();
        using var cleanDoc = JsonDocument.Parse(await cleanResp.Content.ReadAsStringAsync());
        Assert.NotNull(cleanDoc.RootElement.GetProperty("cleanup_recorded_at").GetString());

        // Release
        var releaseResp = await _client.PostAsync($"/api/worker-pool/assignments/{assignmentId}/release", null);
        releaseResp.EnsureSuccessStatusCode();
        using var releaseDoc = JsonDocument.Parse(await releaseResp.Content.ReadAsStringAsync());
        Assert.NotNull(releaseDoc.RootElement.GetProperty("released_at").GetString());
    }

    [Fact]
    public async Task Cleanup_NonTerminal_ReturnsBadRequest()
    {
        var (workerId, assignmentId, _) = await SeedAndLeaseAsync("clean-no");

        // Assignment is still in 'ack' — not terminal
        var cleanResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/cleanup", new
        {
            evidence = """{"log":"test"}""",
        });
        Assert.Equal(HttpStatusCode.BadRequest, cleanResp.StatusCode);
    }

    [Fact]
    public async Task Release_WithoutCleanup_ReturnsBadRequest()
    {
        var workerId = $"wp-rel-nc-{Guid.NewGuid():N}";
        var runId = $"run-rel-nc-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        var leaseResp = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = runId,
            preferred_worker_identity = workerId,
        });
        leaseResp.EnsureSuccessStatusCode();
        using var leaseDoc = JsonDocument.Parse(await leaseResp.Content.ReadAsStringAsync());
        var assignmentId = leaseDoc.RootElement.GetProperty("id").GetInt32();

        await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = "{}",
        });

        // Release without cleanup — should fail
        var releaseResp = await _client.PostAsync($"/api/worker-pool/assignments/{assignmentId}/release", null);
        Assert.Equal(HttpStatusCode.BadRequest, releaseResp.StatusCode);
    }

    // ── Quarantine ─────────────────────────────────────────────────────

    [Fact]
    public async Task Quarantine_SetsStatus()
    {
        var identity = $"wp-quar-{Guid.NewGuid():N}";
        await SeedMemberAsync(identity);

        var resp = await _client.PostAsJsonAsync($"/api/worker-pool/members/{identity}/quarantine", new
        {
            quarantined_by = "admin",
            reason = "misbehavior",
        });
        resp.EnsureSuccessStatusCode();

        var getResp = await _client.GetAsync($"/api/worker-pool/members/{identity}");
        getResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal("quarantined", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Quarantine_MissingMember_ReturnsNotFound()
    {
        var resp = await _client.PostAsJsonAsync($"/api/worker-pool/members/nonexistent-{Guid.NewGuid():N}/quarantine", new
        {
            quarantined_by = "admin",
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Summary ────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_ReturnsCounts()
    {
        var id1 = $"wp-sum-1-{Guid.NewGuid():N}";
        var id2 = $"wp-sum-2-{Guid.NewGuid():N}";
        await SeedMemberAsync(id1);
        await SeedMemberAsync(id2);

        var resp = await _client.GetAsync("/api/worker-pool/summary");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("total_members").GetInt32() >= 2);
        Assert.True(doc.RootElement.GetProperty("available_members").GetInt32() >= 2);
    }

    // ── MCP Tool equivalents (via DI) ──────────────────────────────────

    [Fact]
    public async Task MCP_UpsertPoolMember_RoundTrips()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var identity = $"mcp-upsert-{Guid.NewGuid():N}";

        var result = await WorkerPoolTools.UpsertPoolMember(repo, identity, profile_identity: "MCP Upsert Profile", display_name: "MCP Upsert", capabilities: """["coder"]""", status: "available");
        using var doc = JsonDocument.Parse(result);
        Assert.Contains(identity, doc.RootElement.GetProperty("summary").GetString());
        Assert.Equal(identity, doc.RootElement.GetProperty("worker_identity").GetString());

        var member = await repo.GetMemberAsync(identity);
        Assert.NotNull(member);
        Assert.Equal("MCP Upsert", member.DisplayName);
    }

    [Fact]
    public async Task MCP_ListPoolMembers_Filters()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var availId = $"mcp-list-avail-{Guid.NewGuid():N}";
        var busyId = $"mcp-list-busy-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = availId,
            Status = WorkerPoolStates.MemberAvailable,
        });
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = busyId,
            Status = WorkerPoolStates.MemberBusy,
        });

        var result = await WorkerPoolTools.ListPoolMembers(repo, status: "busy", worker_identity: busyId);
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task MCP_LeaseWorker_CreatesAssignment()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var identity = $"mcp-lease-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            Status = WorkerPoolStates.MemberAvailable,
            Capabilities = """["coder","dotnet"]""",
        });

        var result = await WorkerPoolTools.LeaseWorker(repo, _projectId, "coder", "runner", $"run-mcp-lease-{Guid.NewGuid():N}",
            required_capabilities: """["coder"]""", preferred_worker_identity: identity, verbose: true);
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(identity, doc.RootElement.GetProperty("worker_identity").GetString());
        Assert.Equal("ack", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task MCP_LeaseWorker_NoAvailable_ReturnsError()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        var result = await WorkerPoolTools.LeaseWorker(repo, _projectId, "coder", "runner", $"run-mcp-noavail-{Guid.NewGuid():N}",
            preferred_worker_identity: $"nonexistent-{Guid.NewGuid():N}");
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
    }

    [Fact]
    public async Task MCP_AppendCheckpointAndRespond_FullCycle()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var identity = $"mcp-cp-resp-{Guid.NewGuid():N}";
        var runId = $"run-mcp-cp-resp-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            Status = WorkerPoolStates.MemberAvailable,
        });

        // Lease
        var leaseJson = await WorkerPoolTools.LeaseWorker(repo, _projectId, "coder", "runner", runId,
            preferred_worker_identity: identity, verbose: true);
        using var leaseDoc = JsonDocument.Parse(leaseJson);
        var assignmentId = leaseDoc.RootElement.GetProperty("assignment_id").GetInt32();

        // Append checkpoint
        var cpJson = await WorkerPoolTools.AppendCheckpoint(repo, assignmentId, runId, "progress", """{"progress":"30%"}""");
        using var cpDoc = JsonDocument.Parse(cpJson);
        var checkpointId = cpDoc.RootElement.GetProperty("checkpoint_id").GetInt32();

        // Respond with ack
        var respJson = await WorkerPoolTools.RespondToCheckpoint(repo, checkpointId, runId, "ack", "{}", assignmentId);
        using var respDoc = JsonDocument.Parse(respJson);
        Assert.Equal("ack", respDoc.RootElement.GetProperty("response_type").GetString());

        // Verify assignment is running again
        var assignment = await repo.GetAssignmentAsync(assignmentId);
        Assert.NotNull(assignment);
        Assert.Equal("running", assignment.State);
    }

    [Fact]
    public async Task MCP_QuarantinePoolMember_SetsStatus()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var identity = $"mcp-quar-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            Status = WorkerPoolStates.MemberAvailable,
        });

        var result = await WorkerPoolTools.QuarantinePoolMember(repo, identity, "admin", "violation");
        using var doc = JsonDocument.Parse(result);
        Assert.Contains("quarantined", doc.RootElement.GetProperty("summary").GetString());

        var member = await repo.GetMemberAsync(identity);
        Assert.Equal("quarantined", member!.Status);
    }

    [Fact]
    public async Task MCP_RecordCleanupAndRelease_FullFlow()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var identity = $"mcp-rel-{Guid.NewGuid():N}";
        var runId = $"run-mcp-rel-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            Status = WorkerPoolStates.MemberAvailable,
        });

        // Lease + complete via checkpoint
        var leaseJson = await WorkerPoolTools.LeaseWorker(repo, _projectId, "coder", "runner", runId,
            preferred_worker_identity: identity, verbose: true);
        using var leaseDoc = JsonDocument.Parse(leaseJson);
        var assignmentId = leaseDoc.RootElement.GetProperty("assignment_id").GetInt32();

        await WorkerPoolTools.AppendCheckpoint(repo, assignmentId, runId, "completion", """{"result":"done"}""");

        // Record cleanup evidence
        var cleanJson = await WorkerPoolTools.RecordCleanupEvidence(repo, assignmentId, """{"log":"/tmp/out.log"}""");
        using var cleanDoc = JsonDocument.Parse(cleanJson);
        Assert.Contains("recorded cleanup", cleanDoc.RootElement.GetProperty("summary").GetString());

        // Release
        var releaseJson = await WorkerPoolTools.ReleaseAssignment(repo, assignmentId);
        using var releaseDoc = JsonDocument.Parse(releaseJson);
        Assert.Contains("released", releaseDoc.RootElement.GetProperty("summary").GetString());

        var assignment = await repo.GetAssignmentAsync(assignmentId);
        Assert.NotNull(assignment);
        Assert.NotNull(assignment.ReleasedAt);
    }

    [Fact]
    public async Task MCP_GetWorkerPoolSummary_ReturnsAggregates()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var identity = $"mcp-sum-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            Status = WorkerPoolStates.MemberAvailable,
        });

        var result = await WorkerPoolTools.GetWorkerPoolSummary(repo);
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("members").GetProperty("total").GetInt32() >= 1);
        Assert.True(doc.RootElement.GetProperty("members").GetProperty("available").GetInt32() >= 1);
    }

    [Fact]
    public async Task MCP_GetAssignment_ByIdAndRun()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var identity = $"mcp-get-asgn-{Guid.NewGuid():N}";
        var runId = $"run-mcp-get-asgn-{Guid.NewGuid():N}";
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            Status = WorkerPoolStates.MemberAvailable,
        });

        var leaseJson = await WorkerPoolTools.LeaseWorker(repo, _projectId, "coder", "runner", runId,
            preferred_worker_identity: identity, verbose: true);
        using var leaseDoc = JsonDocument.Parse(leaseJson);
        var assignmentId = leaseDoc.RootElement.GetProperty("assignment_id").GetInt32();

        // By id
        var byId = await WorkerPoolTools.GetAssignment(repo, assignment_id: assignmentId, verbose: true);
        using var byIdDoc = JsonDocument.Parse(byId);
        Assert.Equal(assignmentId, byIdDoc.RootElement.GetProperty("assignment_id").GetInt32());

        // By run_id
        var byRun = await WorkerPoolTools.GetAssignment(repo, run_id: runId, verbose: true);
        using var byRunDoc = JsonDocument.Parse(byRun);
        Assert.Equal(assignmentId, byRunDoc.RootElement.GetProperty("assignment_id").GetInt32());
    }

    // ── Lane/capacity REST ─────────────────────────────────────────────

    [Fact]
    public async Task LaneCapacityRoutes_ExposeSharedProfileUsage()
    {
        var profile = $"spawned-coder-api-{Guid.NewGuid():N}";
        var laneResp = await _client.PostAsJsonAsync("/api/worker-pool/lanes", new
        {
            profile_identity = profile,
            worker_role = "coder",
            capacity = 2,
            status = "active",
        });
        laneResp.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
            for (var i = 1; i <= 3; i++)
            {
                await repo.UpsertMemberAsync(new WorkerPoolMember
                {
                    WorkerIdentity = $"lane-api-{i}-{Guid.NewGuid():N}",
                    ProfileIdentity = profile,
                    WorkerRole = "coder",
                    Status = WorkerPoolStates.MemberAvailable,
                    Capabilities = """["coder"]""",
                });
            }

            for (var i = 1; i <= 2; i++)
            {
                var lease = await repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
                {
                    ProjectId = _projectId,
                    Role = "coder",
                    AssignedBy = "runner",
                    RunId = $"run-lane-api-{i}-{Guid.NewGuid():N}",
                    ProfileIdentity = profile,
                    WorkerRole = "coder",
                });
                Assert.NotNull(lease);
            }
        }

        var capacityResp = await _client.GetAsync($"/api/worker-pool/capacity/{profile}");
        capacityResp.EnsureSuccessStatusCode();
        using var capacityDoc = JsonDocument.Parse(await capacityResp.Content.ReadAsStringAsync());
        Assert.Equal(2, capacityDoc.RootElement.GetProperty("total_capacity").GetInt32());
        Assert.Equal(2, capacityDoc.RootElement.GetProperty("active_leases").GetInt32());
        Assert.Equal(0, capacityDoc.RootElement.GetProperty("available_slots").GetInt32());

        var thirdLease = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = $"run-lane-api-over-{Guid.NewGuid():N}",
            profile_identity = profile,
            worker_role = "coder",
        });
        Assert.Equal(HttpStatusCode.Conflict, thirdLease.StatusCode);
    }

    [Fact]
    public async Task LaneStatusRoute_QuarantineBlocksNewLeasesWithoutMemberMutation()
    {
        var profile = $"spawned-quarantine-api-{Guid.NewGuid():N}";
        var laneResp = await _client.PostAsJsonAsync("/api/worker-pool/lanes", new
        {
            profile_identity = profile,
            worker_role = "coder",
            capacity = 1,
            status = "active",
        });
        laneResp.EnsureSuccessStatusCode();

        string workerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
            workerId = $"lane-quarantine-api-{Guid.NewGuid():N}";
            await repo.UpsertMemberAsync(new WorkerPoolMember
            {
                WorkerIdentity = workerId,
                ProfileIdentity = profile,
                WorkerRole = "coder",
                Status = WorkerPoolStates.MemberAvailable,
                Capabilities = """["coder"]""",
            });
        }

        var statusResp = await _client.PostAsJsonAsync($"/api/worker-pool/lanes/{profile}/coder/status", new
        {
            status = "quarantined",
        });
        statusResp.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
            var member = await repo.GetMemberAsync(workerId);
            Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);
        }

        var leaseResp = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = $"run-lane-quarantined-{Guid.NewGuid():N}",
            profile_identity = profile,
            worker_role = "coder",
        });
        Assert.Equal(HttpStatusCode.Conflict, leaseResp.StatusCode);
    }

    // ── Orchestrator lease REST ──────────────────────────────────────────

    [Fact]
    public async Task OrchestratorLeaseRoutes_CreateReadTransitionAndProjectResidency()
    {
        var workerId = $"orch-api-{Guid.NewGuid():N}";
        await SeedProjectOrchestratorMemberAsync(workerId, profileIdentity: "pooled-orchestrator");

        var createResp = await _client.PostAsJsonAsync("/api/worker-pool/orchestrator-leases", new
        {
            project_id = _projectId,
            lease_owner = "runner",
            scope_type = "project",
            objective = "Temporary project orchestration",
            preferred_orchestrator_identity = workerId,
            profile_identity = "pooled-orchestrator",
            requested_duration_seconds = 3600,
        });
        createResp.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var leaseId = createDoc.RootElement.GetProperty("id").GetInt32();
        var publicLeaseId = createDoc.RootElement.GetProperty("lease_id").GetString();
        Assert.Equal("project_orchestrator", createDoc.RootElement.GetProperty("lease_kind").GetString());
        Assert.Equal("project", createDoc.RootElement.GetProperty("scope_type").GetString());
        Assert.Equal("leased", createDoc.RootElement.GetProperty("state").GetString());
        Assert.Equal(workerId, createDoc.RootElement.GetProperty("orchestrator_identity").GetString());
        Assert.Equal(3600, createDoc.RootElement.GetProperty("requested_duration_seconds").GetInt32());
        Assert.True(createDoc.RootElement.TryGetProperty("lease_expires_at", out var leaseExpiresAt));
        Assert.NotEqual(JsonValueKind.Null, leaseExpiresAt.ValueKind);

        var getResp = await _client.GetAsync($"/api/worker-pool/orchestrator-leases/{leaseId}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        Assert.Equal(publicLeaseId, getDoc.RootElement.GetProperty("lease_id").GetString());

        var projectionResp = await _client.GetAsync($"/api/worker-pool/residency/{_projectId}");
        projectionResp.EnsureSuccessStatusCode();
        using var projectionDoc = JsonDocument.Parse(await projectionResp.Content.ReadAsStringAsync());
        var projection = Assert.Single(projectionDoc.RootElement.GetProperty("projections").EnumerateArray());
        Assert.Equal("orchestrator_lease", projection.GetProperty("residency_kind").GetString());
        Assert.Equal("leased", projection.GetProperty("state").GetString());

        var releaseResp = await _client.PostAsJsonAsync($"/api/worker-pool/orchestrator-leases/{leaseId}/transition", new
        {
            state = "released",
        });
        releaseResp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var member = await repo.GetMemberAsync(workerId);
        Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);

        var released = await repo.GetOrchestratorLeaseAsync(leaseId);
        Assert.NotNull(released);
        Assert.Equal("released", released.State);
        Assert.NotNull(released.ActualDurationSeconds);
    }

    [Fact]
    public async Task OrchestratorLeaseRoutes_RejectPreferredNonOrchestratorWorker()
    {
        var workerId = $"orch-wrong-role-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId, profileIdentity: "pooled-orchestrator");

        var resp = await _client.PostAsJsonAsync("/api/worker-pool/orchestrator-leases", new
        {
            project_id = _projectId,
            lease_owner = "runner",
            preferred_orchestrator_identity = workerId,
            profile_identity = "pooled-orchestrator",
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task OrchestratorLeaseRoutes_RejectInvalidDuration()
    {
        var resp = await _client.PostAsJsonAsync("/api/worker-pool/orchestrator-leases", new
        {
            project_id = _projectId,
            lease_owner = "runner",
            requested_duration_seconds = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task OrchestratorLeaseRoutes_RejectInvalidScope()
    {
        var resp = await _client.PostAsJsonAsync("/api/worker-pool/orchestrator-leases", new
        {
            project_id = _projectId,
            lease_owner = "runner",
            scope_type = "permanent_staff",
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Bad request handling ───────────────────────────────────────────

    [Fact]
    public async Task AppendCheckpoint_MissingFields_ReturnsBadRequest()
    {
        var (workerId, assignmentId, cpBadRunId) = await SeedAndLeaseAsync("cp-bad");

        var badResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = $"run-cp-bad-{Guid.NewGuid():N}",
            // Missing checkpoint_type and payload
        });
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
    }

    [Fact]
    public async Task AppendCheckpointResponse_MissingFields_ReturnsBadRequest()
    {
        var badResp = await _client.PostAsJsonAsync($"/api/worker-pool/checkpoints/1/responses", new
        {
            // Missing required fields
        });
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
    }

    [Fact]
    public async Task Cleanup_MissingEvidence_ReturnsBadRequest()
    {
        var workerId = $"wp-clean-bad-{Guid.NewGuid():N}";
        var runId = $"run-clean-bad-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        var leaseResp = await _client.PostAsJsonAsync("/api/worker-pool/leases", new
        {
            project_id = _projectId,
            role = "coder",
            assigned_by = "runner",
            run_id = runId,
            preferred_worker_identity = workerId,
        });
        leaseResp.EnsureSuccessStatusCode();
        using var leaseDoc = JsonDocument.Parse(await leaseResp.Content.ReadAsStringAsync());
        var assignmentId = leaseDoc.RootElement.GetProperty("id").GetInt32();

        await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = "{}",
        });

        var badResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/cleanup", new
        {
            // Missing evidence
        });
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
    }

    // ── AppFactory ─────────────────────────────────────────────────────

    private sealed class WorkerPoolAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-worker-pool-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // UseSetting makes config values available during Program.Main execution
            builder.UseSetting("db-path", _dbPath);
            builder.UseSetting("llm-endpoint", "http://localhost/fake");
            builder.UseSetting("llm-api-key", "test-key");
            builder.UseSetting("llm-model", "fake");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
