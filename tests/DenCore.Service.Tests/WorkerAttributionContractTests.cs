using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = DenCore.Models.TaskStatus;

namespace DenCore.Service.Tests;

/// <summary>
/// Contract tests for worker target-vs-runtime attribution (#1844).
/// Verifies that Core projections carry target project/task/assignment/run
/// attribution correctly, independent of transport/channel context.
/// </summary>
public sealed class WorkerAttributionContractTests : IAsyncLifetime
{
    // Target project — the "job site"
    private const string TargetProjectId = "goblinbench";
    // Runtime/control project — the "bus depot" (would be den-hermes-bridge in production)
    private const string RuntimeProjectId = "den-hermes-bridge";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private AttributionTestAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new AttributionTestAppFactory();
        _client = _factory.CreateClient();

        // Seed both projects — target and runtime
        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(TargetProjectId) is null)
            await projects.CreateAsync(new Project { Id = TargetProjectId, Name = "GoblinBench (target)" });
        if (await projects.GetByIdAsync(RuntimeProjectId) is null)
            await projects.CreateAsync(new Project { Id = RuntimeProjectId, Name = "Hermes Bridge (runtime)" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<string> SeedCrossProjectWorkerAsync(
        string workerIdentity,
        string profileIdentity = "spawned-coder",
        string workerRole = "coder",
        string? channelId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = workerIdentity,
            ProfileIdentity = profileIdentity,
            WorkerRole = workerRole,
            DisplayName = $"Cross-project {profileIdentity}",
            Capabilities = """["coder","dotnet"]""",
            Status = WorkerPoolStates.MemberAvailable,
            ChannelId = channelId ?? $"channel-{RuntimeProjectId}",
        });
        return workerIdentity;
    }

    private async Task<(int assignmentId, string runId)> LeaseForTargetProjectAsync(
        string workerIdentity,
        string targetProjectId,
        int? taskId = null)
    {
        var runId = $"run-{targetProjectId}-{Guid.NewGuid():N}";
        var body = new Dictionary<string, object?>
        {
            ["project_id"] = targetProjectId,
            ["role"] = "coder",
            ["assigned_by"] = "runner",
            ["run_id"] = runId,
            ["preferred_worker_identity"] = workerIdentity,
        };
        if (taskId.HasValue)
            body["task_id"] = taskId.Value;

        var response = await _client.PostAsJsonAsync("/api/worker-pool/leases", body);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("id").GetInt32(), runId);
    }

    private async Task<int> SeedTaskAsync(string projectId, string title)
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = projectId,
            Title = title,
            Status = TaskStatus.InProgress,
        });
        return task.Id;
    }

    // ── Invariant 1: Target project/task on assignment, not inferred from channel ──

    [Fact]
    public async Task Assignment_ProjectId_IsTargetProject_NotChannelProject()
    {
        // Worker registered with a channel_id associated with the runtime project
        var workerId = $"xproj-worker-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId, channelId: $"channel-{RuntimeProjectId}");

        // Seed a real task in the target project
        var taskId = await SeedTaskAsync(TargetProjectId, "Attribution contract test task");

        // Lease to the TARGET project
        var (assignmentId, runId) = await LeaseForTargetProjectAsync(workerId, TargetProjectId, taskId: taskId);

        // Verify assignment carries TARGET project, not runtime project
        var response = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(TargetProjectId, root.GetProperty("project_id").GetString());
        Assert.Equal(taskId, root.GetProperty("task_id").GetInt32());
        Assert.Equal(workerId, root.GetProperty("worker_identity").GetString());
        Assert.Equal(runId, root.GetProperty("run_id").GetString());
        Assert.Equal("coder", root.GetProperty("role").GetString());

        // Channel_id is present for correlation but does not override project attribution
        Assert.Equal($"channel-{RuntimeProjectId}", root.GetProperty("channel_id").GetString());
    }

    [Fact]
    public async Task Assignment_ByRunId_CarriesTargetProject()
    {
        var workerId = $"xproj-run-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId);

        var (_, runId) = await LeaseForTargetProjectAsync(workerId, TargetProjectId);

        var response = await _client.GetAsync($"/api/worker-pool/assignments/by-run/{runId}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(TargetProjectId, doc.RootElement.GetProperty("project_id").GetString());
        Assert.Equal(runId, doc.RootElement.GetProperty("run_id").GetString());
    }

    // ── Invariant 2: Assignment lists filter by target project, not runtime ──

    [Fact]
    public async Task Assignments_FilterByTargetProject_ReturnsOnlyTargetWork()
    {
        var workerId1 = $"xproj-list1-{Guid.NewGuid():N}";
        var workerId2 = $"xproj-list2-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId1);
        await SeedCrossProjectWorkerAsync(workerId2);

        // Lease one to target project, one to runtime project
        var (targetAssignmentId, _) = await LeaseForTargetProjectAsync(workerId1, TargetProjectId);
        var (_, _) = await LeaseForTargetProjectAsync(workerId2, RuntimeProjectId);

        // Filter by target project
        var response = await _client.GetAsync($"/api/worker-pool/assignments?projectId={TargetProjectId}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var assignments = doc.RootElement.GetProperty("assignments").EnumerateArray().ToList();

        Assert.All(assignments, a =>
            Assert.Equal(TargetProjectId, a.GetProperty("project_id").GetString()));

        var ids = assignments.Select(a => a.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(targetAssignmentId, ids);
    }

    // ── Invariant 3: Completion checkpoint links to target project via assignment ──

    [Fact]
    public async Task CompletionCheckpoint_CarriesTargetProjectThroughAssignment()
    {
        var workerId = $"xproj-cp-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId);

        var (assignmentId, runId) = await LeaseForTargetProjectAsync(workerId, TargetProjectId);

        // Post completion checkpoint
        var cpResponse = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "completion",
            payload = """{"status":"completed","summary":"Implementation done"}""",
        });
        cpResponse.EnsureSuccessStatusCode();

        // Verify assignment state completed and still carries target project
        var getResponse = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        getResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());

        Assert.Equal("completed", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal(TargetProjectId, doc.RootElement.GetProperty("project_id").GetString());
    }

    // ── Invariant 5: Run-id-scoped readback is authoritative ──

    [Fact]
    public async Task RunIdLookup_ReturnsAuthoritativeTargetAttribution()
    {
        var workerId = $"xproj-auth-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId);

        var (assignmentId, runId) = await LeaseForTargetProjectAsync(workerId, TargetProjectId);

        // Transition to running
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        await repo.TransitionAssignmentStateAsync(assignmentId, "running");

        // Readback by run_id — must return target attribution
        var response = await _client.GetAsync($"/api/worker-pool/assignments/by-run/{runId}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal(TargetProjectId, doc.RootElement.GetProperty("project_id").GetString());
        Assert.Equal(runId, doc.RootElement.GetProperty("run_id").GetString());
    }

    // ── Invariant 6: Pool residency projection for target project ──

    [Fact]
    public async Task ResidencyProjection_ShowsTargetProjectAssignments()
    {
        var workerId = $"xproj-res-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId);

        var (assignmentId, _) = await LeaseForTargetProjectAsync(workerId, TargetProjectId);

        var response = await _client.GetAsync($"/api/worker-pool/residency/{TargetProjectId}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var projections = doc.RootElement.GetProperty("projections").EnumerateArray().ToList();
        Assert.Contains(projections, p =>
            p.GetProperty("project_id").GetString() == TargetProjectId &&
            p.GetProperty("residency_kind").GetString() == "task_worker_assignment");

        // Verify no runtime-project assignments leak into target-project residency
        Assert.All(projections, p =>
            Assert.Equal(TargetProjectId, p.GetProperty("project_id").GetString()));
    }

    // ── Invariant: Profile identity disambiguates but does not override target ──

    [Fact]
    public async Task ProfileIdentity_DoesNotAffectTargetProjectAttribution()
    {
        // Two workers with same profile_identity but different concrete identities
        var profile = "spawned-coder";
        var workerId1 = $"xprof-1-{Guid.NewGuid():N}";
        var workerId2 = $"xprof-2-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId1, profileIdentity: profile);
        await SeedCrossProjectWorkerAsync(workerId2, profileIdentity: profile);

        // Lease both to different target projects
        var (_, _) = await LeaseForTargetProjectAsync(workerId1, TargetProjectId);
        var (_, _) = await LeaseForTargetProjectAsync(workerId2, RuntimeProjectId);

        // Filter by target project — only worker1's assignment should appear
        var response = await _client.GetAsync($"/api/worker-pool/assignments?projectId={TargetProjectId}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var assignments = doc.RootElement.GetProperty("assignments").EnumerateArray().ToList();

        Assert.All(assignments, a =>
        {
            Assert.Equal(TargetProjectId, a.GetProperty("project_id").GetString());
            Assert.Equal(profile, a.GetProperty("profile_identity").GetString());
        });
    }

    // ── Invariant: Orchestrator lease carries target project ──

    [Fact]
    public async Task OrchestratorLease_CarriesTargetProject()
    {
        var workerId = $"xproj-orch-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        await repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = workerId,
            ProfileIdentity = "pooled-orchestrator",
            WorkerRole = "project_orchestrator",
            DisplayName = "Cross-project orchestrator",
            Capabilities = """["planning","den-coordination"]""",
            Status = WorkerPoolStates.MemberAvailable,
            AgentInstanceId = $"hermes:test:{workerId}:live",
            AdapterInstanceId = $"adapter:{workerId}",
            SessionId = $"session-{workerId}",
            ChannelId = $"channel-{RuntimeProjectId}",
        });

        var response = await _client.PostAsJsonAsync("/api/worker-pool/orchestrator-leases", new
        {
            project_id = TargetProjectId,
            lease_owner = "runner",
            scope_type = "project",
            objective = "Temporary orchestration for GoblinBench",
            preferred_orchestrator_identity = workerId,
            profile_identity = "pooled-orchestrator",
        });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Verify lease carries target project, not runtime project
        Assert.Equal(TargetProjectId, doc.RootElement.GetProperty("project_id").GetString());
        Assert.Equal(workerId, doc.RootElement.GetProperty("orchestrator_identity").GetString());
        Assert.Equal("pooled-orchestrator", doc.RootElement.GetProperty("profile_identity").GetString());
        Assert.Equal("project_orchestrator", doc.RootElement.GetProperty("lease_kind").GetString());
    }

    // ── Checkpoint list carries assignment linkage ──

    [Fact]
    public async Task Checkpoints_LinkToTargetProjectViaAssignment()
    {
        var workerId = $"xproj-cpl-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId);

        var (assignmentId, runId) = await LeaseForTargetProjectAsync(workerId, TargetProjectId);

        // Post progress checkpoint
        await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = """{"progress":"50%"}""",
        });

        var listResponse = await _client.GetAsync($"/api/worker-pool/checkpoints?assignmentId={assignmentId}");
        listResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());

        var checkpoints = doc.RootElement.GetProperty("checkpoints").EnumerateArray().ToList();
        Assert.Single(checkpoints);
        Assert.Equal(assignmentId, checkpoints[0].GetProperty("assignment_id").GetInt32());
        Assert.Equal(runId, checkpoints[0].GetProperty("run_id").GetString());
        Assert.Equal("progress", checkpoints[0].GetProperty("checkpoint_type").GetString());
    }

    // ── Cross-project worker with target task: full attribution chain ──

    [Fact]
    public async Task CrossProjectWorker_FullAttributionChain_TargetFieldsCorrect()
    {
        var workerId = $"xproj-chain-{Guid.NewGuid():N}";
        await SeedCrossProjectWorkerAsync(workerId, channelId: "shared-control-channel");

        var targetTaskId = await SeedTaskAsync(TargetProjectId, "Cross-project attribution target task");
        var (assignmentId, runId) = await LeaseForTargetProjectAsync(workerId, TargetProjectId, taskId: targetTaskId);

        // Transition → running
        var transResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/transition", new { state = "running" });
        transResp.EnsureSuccessStatusCode();

        // Progress checkpoint
        var cpResp = await _client.PostAsJsonAsync($"/api/worker-pool/assignments/{assignmentId}/checkpoints", new
        {
            run_id = runId,
            checkpoint_type = "progress",
            payload = """{"progress":"75%","artifact_dir":"/tmp/artifacts"}""",
        });
        cpResp.EnsureSuccessStatusCode();

        // Verify assignment still has target project after all operations
        // After appending a checkpoint, the state moves to checkpoint_waiting
        var assignmentResp = await _client.GetAsync($"/api/worker-pool/assignments/{assignmentId}");
        assignmentResp.EnsureSuccessStatusCode();
        using var assignDoc = JsonDocument.Parse(await assignmentResp.Content.ReadAsStringAsync());

        Assert.Equal(TargetProjectId, assignDoc.RootElement.GetProperty("project_id").GetString());
        Assert.Equal(targetTaskId, assignDoc.RootElement.GetProperty("task_id").GetInt32());
        Assert.Equal("checkpoint_waiting", assignDoc.RootElement.GetProperty("state").GetString());
        Assert.Equal("shared-control-channel", assignDoc.RootElement.GetProperty("channel_id").GetString());

        // Verify by-run lookup also returns target project
        var byRunResp = await _client.GetAsync($"/api/worker-pool/assignments/by-run/{runId}");
        byRunResp.EnsureSuccessStatusCode();
        using var byRunDoc = JsonDocument.Parse(await byRunResp.Content.ReadAsStringAsync());
        Assert.Equal(TargetProjectId, byRunDoc.RootElement.GetProperty("project_id").GetString());

        // Verify residency projection
        var residencyResp = await _client.GetAsync($"/api/worker-pool/residency/{TargetProjectId}");
        residencyResp.EnsureSuccessStatusCode();
        using var resDoc = JsonDocument.Parse(await residencyResp.Content.ReadAsStringAsync());
        var projections = resDoc.RootElement.GetProperty("projections").EnumerateArray().ToList();
        Assert.Contains(projections, p =>
            p.GetProperty("project_id").GetString() == TargetProjectId &&
            p.GetProperty("residency_kind").GetString() == "task_worker_assignment");
    }

    // ── AppFactory ─────────────────────────────────────────────────────

    private sealed class AttributionTestAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-core-attribution-contract-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DenCore:Provider", "Postgres");
            builder.UseSetting("DenCore:ConnectionString", DatabaseInitializer.GetConnectionString(_dbPath));
            builder.UseSetting("db-path", _dbPath);
            builder.UseSetting("llm-endpoint", "http://localhost/fake");
            builder.UseSetting("llm-api-key", "test-key");
            builder.UseSetting("llm-model", "fake");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            DatabaseInitializer.DisposeLeaseAsync(_dbPath).AsTask().GetAwaiter().GetResult();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
