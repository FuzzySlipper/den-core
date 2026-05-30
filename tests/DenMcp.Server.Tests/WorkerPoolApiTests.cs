using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

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

    /// <summary>Create a worker and lease it immediately, returning the assignment for follow-up ops.</summary>
    private async Task<(string workerIdentity, int assignmentId)> SeedAndLeaseAsync(string prefix)
    {
        var workerId = $"{prefix}-{Guid.NewGuid():N}";
        await SeedMemberAsync(workerId);

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();
        var lease = await repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = _projectId,
            Role = "coder",
            AssignedBy = "runner",
            RunId = $"run-{prefix}-{Guid.NewGuid():N}",
            PreferredWorkerIdentity = workerId,
        });
        Assert.NotNull(lease);
        return (workerId, lease.Id);
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
        var (w1, a1) = await SeedAndLeaseAsync("asgn-list-a");
        var (w2, a2) = await SeedAndLeaseAsync("asgn-list-b");

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("asgn-id");

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("trans");

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("cp-rest");

        var runId = $"run-cp-rest-{Guid.NewGuid():N}";
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
    public async Task AppendCheckpoint_Failure_SetsState()
    {
        var (workerId, assignmentId) = await SeedAndLeaseAsync("cp-fail");
        var runId = $"run-cp-fail-{Guid.NewGuid():N}";

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("cp-list");
        var runId = $"run-cp-list-{Guid.NewGuid():N}";

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("resp-ack");
        var runId = $"run-resp-ack-{Guid.NewGuid():N}";

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("resp-abort");
        var runId = $"run-resp-abort-{Guid.NewGuid():N}";

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
    public async Task ListCheckpointResponses_ReturnsResponses()
    {
        var (workerId, assignmentId) = await SeedAndLeaseAsync("resp-list");
        var runId = $"run-resp-list-{Guid.NewGuid():N}";

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("resp-run");
        var runId = $"run-resp-by-run-{Guid.NewGuid():N}";

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
        var (workerId, assignmentId) = await SeedAndLeaseAsync("clean-no");

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

    // ── Bad request handling ───────────────────────────────────────────

    [Fact]
    public async Task AppendCheckpoint_MissingFields_ReturnsBadRequest()
    {
        var (workerId, assignmentId) = await SeedAndLeaseAsync("cp-bad");

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
