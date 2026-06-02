using DenCore.Data;
using DenCore.Models;
using DenCore.Tests;
using Microsoft.Data.Sqlite;

namespace DenCore.Tests.Data;

public class WorkerPoolRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private IWorkerPoolRepository _repo = null!;
    private IProjectRepository _projects = null!;
    private ITaskRepository _tasks = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new WorkerPoolRepository(_testDb.Db);
        _projects = new ProjectRepository(_testDb.Db);
        _tasks = new TaskRepository(_testDb.Db);

        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private async Task<int> SeedTaskAsync()
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = "Test Task",
            Status = DenCore.Models.TaskStatus.Planned,
        }, null);
        return task.Id;
    }

    private async Task<string> SeedMemberAsync(string identity = "worker-1", string capabilities = "[\"coder\",\"dotnet\"]", string profileIdentity = "")
    {
        var member = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            ProfileIdentity = profileIdentity,
            DisplayName = "Test Worker",
            Capabilities = capabilities,
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });
        return member.WorkerIdentity;
    }

    private Task<string> SeedProjectOrchestratorMemberAsync(string identity, string profileIdentity = "pooled-orchestrator") =>
        SeedMemberAsync(identity, "[\"planning\",\"den-coordination\"]", profileIdentity, workerRole: "project_orchestrator");

    private async Task<string> SeedMemberAsync(
        string identity,
        string capabilities,
        string profileIdentity,
        string? workerRole)
    {
        var member = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
            ProfileIdentity = profileIdentity,
            WorkerRole = workerRole,
            DisplayName = "Test Worker",
            Capabilities = capabilities,
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });
        return member.WorkerIdentity;
    }

    // ─────────────────────────────────────────────────────────────────
    // Fresh DB creates tables
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_CreatesAllWorkerPoolTables()
    {
        await using var conn = await _testDb.Db.CreateConnectionAsync();

        foreach (var table in new[] { "worker_pool_members", "worker_assignments", "worker_checkpoints", "checkpoint_responses" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);
        }

        // Check key indexes exist
        foreach (var idx in new[] { "idx_worker_pool_members_status", "idx_worker_assignments_worker_state", "idx_worker_assignments_project_state", "idx_worker_checkpoints_assignment", "idx_checkpoint_responses_checkpoint" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='index' AND name='{idx}'";
            Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Member CRUD
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertMember_CreatesAndReturns()
    {
        var member = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "test-worker",
            DisplayName = "Test Worker",
            Status = WorkerPoolStates.MemberAvailable,
        });
        Assert.Equal("test-worker", member.WorkerIdentity);
        Assert.Equal(WorkerPoolStates.MemberAvailable, member.Status);
    }

    [Fact]
    public async Task UpsertMember_UpdateExisting()
    {
        await SeedMemberAsync("upd-worker");
        var updated = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "upd-worker",
            DisplayName = "Updated Name",
            Status = WorkerPoolStates.MemberBusy,
            Metadata = "{\"key\":\"val\"}",
        });
        Assert.Equal("Updated Name", updated.DisplayName);
        Assert.Equal(WorkerPoolStates.MemberBusy, updated.Status);
        Assert.Equal("{\"key\":\"val\"}", updated.Metadata);
    }

    [Fact]
    public async Task GetMember_ReturnsNullForMissing()
    {
        var member = await _repo.GetMemberAsync("nonexistent");
        Assert.Null(member);
    }

    [Fact]
    public async Task GetMember_ReturnsMember()
    {
        await SeedMemberAsync("get-me");
        var member = await _repo.GetMemberAsync("get-me");
        Assert.NotNull(member);
        Assert.Equal("get-me", member.WorkerIdentity);
    }

    [Fact]
    public async Task ListMembers_FiltersByStatus()
    {
        await SeedMemberAsync("avail-1");
        await SeedMemberAsync("avail-2");
        await _repo.UpsertMemberAsync(new WorkerPoolMember { WorkerIdentity = "busy-1", Status = WorkerPoolStates.MemberBusy });

        var available = await _repo.ListMembersAsync(new WorkerPoolMemberListOptions { Status = WorkerPoolStates.MemberAvailable });
        Assert.Equal(2, available.Count);

        var busy = await _repo.ListMembersAsync(new WorkerPoolMemberListOptions { Status = WorkerPoolStates.MemberBusy });
        Assert.Single(busy);
    }

    [Fact]
    public async Task SetMemberStatus_UpdatesStatus()
    {
        await SeedMemberAsync("status-test");
        var rows = await _repo.SetMemberStatusAsync("status-test", WorkerPoolStates.MemberBusy);
        Assert.Equal(1, rows);

        var member = await _repo.GetMemberAsync("status-test");
        Assert.Equal(WorkerPoolStates.MemberBusy, member!.Status);
    }

    // ─────────────────────────────────────────────────────────────────
    // Lease
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lease_NoAvailableWorker_ReturnsNull()
    {
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-1",
        });
        Assert.Null(lease);
    }

    [Fact]
    public async Task Lease_AvailableWorker_Success()
    {
        await SeedMemberAsync("avail-leasor");

        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-lease-1",
        });
        Assert.NotNull(lease);
        Assert.Equal("avail-leasor", lease.WorkerIdentity);
        Assert.Equal("test-proj", lease.ProjectId);
        Assert.Equal("coder", lease.Role);
        Assert.Equal(WorkerPoolStates.Ack, lease.State);
        Assert.NotNull(lease.AcquiredAt);

        // Member should now be busy
        var member = await _repo.GetMemberAsync("avail-leasor");
        Assert.Equal(WorkerPoolStates.MemberBusy, member!.Status);
    }

    [Fact]
    public async Task Lease_PreferredWorker_Success()
    {
        await SeedMemberAsync("preferred-one");
        await SeedMemberAsync("preferred-two");

        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "reviewer",
            AssignedBy = "runner",
            RunId = "run-pref",
            PreferredWorkerIdentity = "preferred-two",
        });
        Assert.NotNull(lease);
        Assert.Equal("preferred-two", lease.WorkerIdentity);
    }

    [Fact]
    public async Task Lease_WithCapabilityMatch_Success()
    {
        await SeedMemberAsync("cap-match", "[\"coder\",\"dotnet\"]");
        await SeedMemberAsync("cap-mismatch", "[\"reviewer\"]");

        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-cap",
            RequiredCapabilities = new[] { "coder", "dotnet" },
        });
        Assert.NotNull(lease);
        Assert.Equal("cap-match", lease.WorkerIdentity);
    }

    [Fact]
    public async Task Lease_ConflictIdempotency_ReturnsNullForDupRun()
    {
        await SeedMemberAsync("conflict-worker");
        var first = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-conflict",
        });
        Assert.NotNull(first);

        // Second lease with same run_id should return null (conflict)
        var second = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-conflict",
        });
        Assert.Null(second);
    }

    // ─────────────────────────────────────────────────────────────────
    // Assignment management
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignmentByRunId_ReturnsAssignment()
    {
        await SeedMemberAsync("get-run-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "get-by-run",
        });
        Assert.NotNull(lease);

        var found = await _repo.GetAssignmentByRunIdAsync("get-by-run");
        Assert.NotNull(found);
        Assert.Equal(lease.Id, found.Id);
    }

    [Fact]
    public async Task ListAssignments_FiltersByState()
    {
        await SeedMemberAsync("list-worker-1");
        await SeedMemberAsync("list-worker-2");

        var a1 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-list-1" });
        var a2 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-list-2" });

        Assert.NotNull(a1);
        Assert.NotNull(a2);

        var all = await _repo.ListAssignmentsAsync(new WorkerAssignmentListOptions { Limit = 200 });
        Assert.Equal(2, all.Count);

        var ack = await _repo.ListAssignmentsAsync(new WorkerAssignmentListOptions { State = WorkerPoolStates.Ack });
        Assert.Equal(2, ack.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // State transitions
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transition_Ack_To_Running()
    {
        await SeedMemberAsync("transit-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-transit" });
        Assert.NotNull(lease);

        var running = await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        Assert.NotNull(running);
        Assert.Equal(WorkerPoolStates.Running, running.State);
    }

    [Fact]
    public async Task Transition_Running_To_Blocked()
    {
        await SeedMemberAsync("block-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-block" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        var blocked = await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Blocked);
        Assert.NotNull(blocked);
        Assert.Equal(WorkerPoolStates.Blocked, blocked.State);
    }

    [Fact]
    public async Task Transition_Running_To_Completed_ReturnsAvailable()
    {
        await SeedMemberAsync("done-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-done" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        var completed = await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);
        Assert.NotNull(completed);
        Assert.Equal(WorkerPoolStates.Completed, completed.State);
        Assert.NotNull(completed.ReleasedAt);

        // Member should be available again
        var member = await _repo.GetMemberAsync("done-worker");
        Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);
    }

    [Fact]
    public async Task Transition_TerminalUpdateFailure_RollsBackMemberStatus()
    {
        await SeedMemberAsync("rollback-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-rollback" });
        Assert.NotNull(lease);
        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);

        await using (var conn = await _testDb.Db.CreateConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE TRIGGER fail_worker_assignment_completion
                BEFORE UPDATE OF state ON worker_assignments
                WHEN OLD.id = {lease.Id} AND NEW.state = 'completed'
                BEGIN
                    SELECT RAISE(ABORT, 'forced assignment update failure');
                END;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed));

        var assignment = await _repo.GetAssignmentAsync(lease.Id);
        Assert.Equal(WorkerPoolStates.Running, assignment!.State);
        var member = await _repo.GetMemberAsync("rollback-worker");
        Assert.Equal(WorkerPoolStates.MemberBusy, member!.Status);
    }

    [Fact]
    public async Task Transition_Invalid_ReturnsNull()
    {
        await SeedMemberAsync("bad-transit-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-bad" });
        Assert.NotNull(lease);

        // Completed -> Running is invalid (terminal -> non-terminal)
        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);
        var reverted = await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        Assert.Null(reverted);
    }

    // ─────────────────────────────────────────────────────────────────
    // Checkpoints
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppendCheckpoint_SetsCheckpointWaiting()
    {
        await SeedMemberAsync("cp-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-cp" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        var cp = await _repo.AppendCheckpointAsync(lease.Id, "run-cp", WorkerPoolStates.CheckpointPeriodic, "{\"progress\":\"50%\"}");
        Assert.NotNull(cp);
        Assert.Equal(WorkerPoolStates.CheckpointPeriodic, cp.CheckpointType);

        var assignment = await _repo.GetAssignmentAsync(lease.Id);
        Assert.Equal(WorkerPoolStates.CheckpointWaiting, assignment!.State);
        Assert.Equal(cp.Id, assignment.LatestCheckpointId);
    }

    [Fact]
    public async Task AppendCheckpoint_Completion_SetsCompleted()
    {
        await SeedMemberAsync("cp-done-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-cp-done" });
        Assert.NotNull(lease);

        var cp = await _repo.AppendCheckpointAsync(lease.Id, "run-cp-done", WorkerPoolStates.CheckpointCompletion, "{\"result\":\"success\"}");
        Assert.NotNull(cp);

        var assignment = await _repo.GetAssignmentAsync(lease.Id);
        Assert.Equal(WorkerPoolStates.Completed, assignment!.State);
    }

    [Fact]
    public async Task AppendCheckpoint_Failure_SetsFailed()
    {
        await SeedMemberAsync("cp-fail-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-cp-fail" });
        Assert.NotNull(lease);

        var cp = await _repo.AppendCheckpointAsync(lease.Id, "run-cp-fail", WorkerPoolStates.CheckpointFailure, "{\"error\":\"timeout\"}");
        Assert.NotNull(cp);

        var assignment = await _repo.GetAssignmentAsync(lease.Id);
        Assert.Equal(WorkerPoolStates.Failed, assignment!.State);
    }

    [Fact]
    public async Task ListCheckpoints_Filters()
    {
        await SeedMemberAsync("cp-list-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-cp-list" });
        Assert.NotNull(lease);

        await _repo.AppendCheckpointAsync(lease.Id, "run-cp-list", WorkerPoolStates.CheckpointPeriodic, "{}");
        await _repo.AppendCheckpointAsync(lease.Id, "run-cp-list", WorkerPoolStates.CheckpointProgress, "{}");

        var all = await _repo.ListCheckpointsAsync(new WorkerCheckpointListOptions { AssignmentId = lease.Id });
        Assert.Equal(2, all.Count);

        var filtered = await _repo.ListCheckpointsAsync(new WorkerCheckpointListOptions { CheckpointType = WorkerPoolStates.CheckpointProgress });
        Assert.Single(filtered);
    }

    // ─────────────────────────────────────────────────────────────────
    // Checkpoint Responses
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckpointResponse_Ack_RestoresRunning()
    {
        await SeedMemberAsync("resp-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-resp" });
        Assert.NotNull(lease);

        var cp = await _repo.AppendCheckpointAsync(lease.Id, "run-resp", WorkerPoolStates.CheckpointPeriodic, "{}");
        var resp = await _repo.AppendCheckpointResponseAsync(cp.Id, lease.Id, "run-resp", WorkerPoolStates.ResponseAck, "{}");
        Assert.NotNull(resp);
        Assert.Equal(WorkerPoolStates.ResponseAck, resp.ResponseType);

        var assignment = await _repo.GetAssignmentAsync(lease.Id);
        Assert.Equal(WorkerPoolStates.Running, assignment!.State);
    }

    [Fact]
    public async Task CheckpointResponse_Abort_ExpiresAssignment()
    {
        await SeedMemberAsync("resp-abort-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-resp-abort" });
        Assert.NotNull(lease);

        var cp = await _repo.AppendCheckpointAsync(lease.Id, "run-resp-abort", WorkerPoolStates.CheckpointPeriodic, "{}");
        var resp = await _repo.AppendCheckpointResponseAsync(cp.Id, lease.Id, "run-resp-abort", WorkerPoolStates.ResponseAbort, "{\"reason\":\"cancelled\"}");
        Assert.NotNull(resp);

        var assignment = await _repo.GetAssignmentAsync(lease.Id);
        Assert.Equal(WorkerPoolStates.Expired, assignment!.State);
    }

    [Fact]
    public async Task ListResponses_ByCheckpointAndRun()
    {
        await SeedMemberAsync("resp-list-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-resp-list" });
        Assert.NotNull(lease);

        var cp = await _repo.AppendCheckpointAsync(lease.Id, "run-resp-list", WorkerPoolStates.CheckpointPeriodic, "{}");
        await _repo.AppendCheckpointResponseAsync(cp.Id, lease.Id, "run-resp-list", WorkerPoolStates.ResponseAck, "{}");
        await _repo.AppendCheckpointResponseAsync(cp.Id, lease.Id, "run-resp-list", WorkerPoolStates.ResponseGuidance, "{\"msg\":\"continue\"}");

        var byCp = await _repo.ListResponsesAsync(cp.Id);
        Assert.Equal(2, byCp.Count);

        var byRun = await _repo.ListResponsesByRunIdAsync("run-resp-list");
        Assert.Equal(2, byRun.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Cleanup & Release
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordCleanup_NonTerminal_ReturnsNull()
    {
        await SeedMemberAsync("cleanup-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-cleanup" });
        Assert.NotNull(lease);

        var result = await _repo.RecordCleanupEvidenceAsync(lease.Id, "{\"log\":\"path\"}");
        Assert.Null(result); // Not terminal yet
    }

    [Fact]
    public async Task RecordCleanup_Terminal_Success()
    {
        await SeedMemberAsync("cleanup-done-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-cleanup-done" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);
        var result = await _repo.RecordCleanupEvidenceAsync(lease.Id, "{\"log\":\"path\"}");
        Assert.NotNull(result);
        Assert.NotNull(result.CleanupEvidence);
    }

    [Fact]
    public async Task Release_WithoutCleanup_ReturnsNull()
    {
        await SeedMemberAsync("release-no-cleanup");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-release-noclean" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);
        var result = await _repo.ReleaseAssignmentAsync(lease.Id);
        Assert.Null(result); // Missing cleanup evidence
    }

    [Fact]
    public async Task Release_WithCleanup_Success()
    {
        await SeedMemberAsync("release-done-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-release-done" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);
        var cleanResult = await _repo.RecordCleanupEvidenceAsync(lease.Id, "{\"log\":\"path\"}");
        Assert.NotNull(cleanResult);

        var releaseResult = await _repo.ReleaseAssignmentAsync(lease.Id);
        Assert.NotNull(releaseResult);
        Assert.NotNull(releaseResult.ReleasedAt);
        Assert.NotNull(releaseResult.CleanupRecordedAt);
    }

    [Fact]
    public async Task Release_Idempotent()
    {
        await SeedMemberAsync("release-idemp-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-release-idemp" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);
        await _repo.RecordCleanupEvidenceAsync(lease.Id, "{}");
        var first = await _repo.ReleaseAssignmentAsync(lease.Id);
        Assert.NotNull(first);

        var second = await _repo.ReleaseAssignmentAsync(lease.Id);
        Assert.NotNull(second); // Idempotent
    }

    // ─────────────────────────────────────────────────────────────────
    // Quarantine
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quarantine_SetsStatus()
    {
        await SeedMemberAsync("quarantine-me");
        var ok = await _repo.QuarantineWorkerAsync("quarantine-me", "admin", "bad behavior");
        Assert.True(ok);

        var member = await _repo.GetMemberAsync("quarantine-me");
        Assert.Equal(WorkerPoolStates.MemberQuarantined, member!.Status);
        Assert.NotNull(member.Metadata);
        Assert.Contains("quarantined_by", member.Metadata);
        Assert.Contains("admin", member.Metadata);
    }

    [Fact]
    public async Task Quarantine_MissingMember_ReturnsFalse()
    {
        var ok = await _repo.QuarantineWorkerAsync("no-such-worker", "admin");
        Assert.False(ok);
    }

    // ─────────────────────────────────────────────────────────────────
    // Summary
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_ReflectsPoolState()
    {
        await SeedMemberAsync("sum-avail-1");
        await SeedMemberAsync("sum-avail-2");
        await _repo.UpsertMemberAsync(new WorkerPoolMember { WorkerIdentity = "sum-busy-1", Status = WorkerPoolStates.MemberBusy });
        await _repo.UpsertMemberAsync(new WorkerPoolMember { WorkerIdentity = "sum-quar-1", Status = WorkerPoolStates.MemberQuarantined });

        var summary = await _repo.GetSummaryAsync();
        Assert.Equal(4, summary.TotalMembers);
        Assert.Equal(2, summary.AvailableMembers);
        Assert.Equal(1, summary.BusyMembers);
        Assert.Equal(1, summary.QuarantinedMembers);
    }

    [Fact]
    public async Task Summary_TracksAssignmentCounts()
    {
        await SeedMemberAsync("sum-asgn-a");
        await SeedMemberAsync("sum-asgn-b");

        var a1 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-sum-1" });
        var a2 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-sum-2" });
        Assert.NotNull(a1);
        Assert.NotNull(a2);

        await _repo.TransitionAssignmentStateAsync(a1.Id, WorkerPoolStates.Completed);
        await _repo.AppendCheckpointAsync(a2.Id, "run-sum-2", WorkerPoolStates.CheckpointFailure, "{}");

        var summary = await _repo.GetSummaryAsync();
        Assert.Equal(1, summary.CompletedAssignments);
        Assert.Equal(1, summary.FailedAssignments);
        Assert.Equal(0, summary.ActiveAssignments); // a1 completed, a2 failed
    }

    // ─────────────────────────────────────────────────────────────────
    // Malformed transition rejection
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transition_UnknownState_ReturnsNull()
    {
        await SeedMemberAsync("bad-state-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-bad-state" });
        Assert.NotNull(lease);

        var result = await _repo.TransitionAssignmentStateAsync(lease.Id, "nonexistent_state");
        Assert.Null(result);
    }

    [Fact]
    public async Task Transition_TerminalToNonTerminal_ReturnsNull()
    {
        await SeedMemberAsync("terminal-revert-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-terminal-rev" });
        Assert.NotNull(lease);

        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);

        var reverted = await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Ack);
        Assert.Null(reverted);
    }

    // ─────────────────────────────────────────────────────────────────
    // Existing DB idempotency
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbInitializer_Idempotent()
    {
        // Re-initialize on the same DB should not throw.
        // Since DbConnectionFactory wraps the connection string, get it from the TestDb's init.
        var ex = await Record.ExceptionAsync(async () =>
        {
            await using var conn = await _testDb.Db.CreateConnectionAsync();
            // Verify tables still work by doing a simple count
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM worker_pool_members";
            await cmd.ExecuteScalarAsync();
        });
        Assert.Null(ex);
    }

    // ─────────────────────────────────────────────────────────────────
    // Checkpoint readback evidence
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Checkpoint_AppendAndReadback_Verifiable()
    {
        await SeedMemberAsync("readback-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput { ProjectId = "test-proj", Role = "coder", AssignedBy = "runner", RunId = "run-readback" });
        Assert.NotNull(lease);

        var payload = "{\"progress\":\"75%\",\"files_changed\":[\"src/a.cs\",\"src/b.cs\"]}";
        var cp = await _repo.AppendCheckpointAsync(lease.Id, "run-readback", WorkerPoolStates.CheckpointProgress, payload);

        // Read it back
        var checkpoints = await _repo.ListCheckpointsAsync(new WorkerCheckpointListOptions { RunId = "run-readback" });
        Assert.Single(checkpoints);
        Assert.Equal(payload, checkpoints[0].Payload);
        Assert.Equal(WorkerPoolStates.CheckpointProgress, checkpoints[0].CheckpointType);
        Assert.Equal(lease.Id, checkpoints[0].AssignmentId);
    }

    // ─────────────────────────────────────────────────────────────────
    // Shared Profile Identity — multiple members with the same profile
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SharedProfile_TwoMembersWithSameProfile_IndependentLifecycle()
    {
        // Register two members sharing the same profile identity
        var profileId = "spawned-coder";
        var member1 = "shared-coder-alpha";
        var member2 = "shared-coder-beta";

        await SeedMemberAsync(member1, "[\"coder\"]", profileId);
        await SeedMemberAsync(member2, "[\"coder\"]", profileId);

        // Both should be available initially
        var m1 = await _repo.GetMemberAsync(member1);
        Assert.NotNull(m1);
        Assert.Equal(WorkerPoolStates.MemberAvailable, m1.Status);
        Assert.Equal(profileId, m1.ProfileIdentity);

        var m2 = await _repo.GetMemberAsync(member2);
        Assert.NotNull(m2);
        Assert.Equal(WorkerPoolStates.MemberAvailable, m2.Status);
        Assert.Equal(profileId, m2.ProfileIdentity);

        // Filter by profile identity should return both
        var byProfile = await _repo.ListMembersAsync(new WorkerPoolMemberListOptions
        {
            ProfileIdentity = profileId,
        });
        Assert.Equal(2, byProfile.Count);

        // Lease member1 by concrete identity
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-shared-alpha",
            PreferredWorkerIdentity = member1,
        });
        Assert.NotNull(lease);
        Assert.Equal(member1, lease.WorkerIdentity);

        // member1 should now be busy
        m1 = await _repo.GetMemberAsync(member1);
        Assert.Equal(WorkerPoolStates.MemberBusy, m1!.Status);

        // member2 should still be available (not affected)
        m2 = await _repo.GetMemberAsync(member2);
        Assert.Equal(WorkerPoolStates.MemberAvailable, m2!.Status);

        // Quarantine member2 by concrete identity
        var quarantined = await _repo.QuarantineWorkerAsync(member2, "admin", "test quarantine isolation");
        Assert.True(quarantined);

        // member2 should now be quarantined
        m2 = await _repo.GetMemberAsync(member2);
        Assert.Equal(WorkerPoolStates.MemberQuarantined, m2!.Status);
        Assert.Contains("quarantined_by", m2.Metadata ?? "");

        // member1 should still be busy (not affected by member2's quarantine)
        m1 = await _repo.GetMemberAsync(member1);
        Assert.Equal(WorkerPoolStates.MemberBusy, m1!.Status);

        // Complete member1's assignment — should return to available
        await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Running);
        var completed = await _repo.TransitionAssignmentStateAsync(lease.Id, WorkerPoolStates.Completed);
        Assert.NotNull(completed);
        Assert.Equal(WorkerPoolStates.Completed, completed.State);

        m1 = await _repo.GetMemberAsync(member1);
        Assert.Equal(WorkerPoolStates.MemberAvailable, m1!.Status);

        // member2 is still quarantined (independent lifecycle)
        m2 = await _repo.GetMemberAsync(member2);
        Assert.Equal(WorkerPoolStates.MemberQuarantined, m2!.Status);
    }

    [Fact]
    public async Task SharedProfile_LeaseByProfileIdentity_FiltersCorrectly()
    {
        var profileId = "spawned-coder";
        var member1 = "profile-filter-alpha";
        var member2 = "profile-filter-beta";

        await SeedMemberAsync(member1, "[\"coder\"]", profileId);
        await SeedMemberAsync(member2, "[\"coder\"]", profileId);

        // Also create a member with a different profile
        await SeedMemberAsync("other-worker", "[\"coder\"]", "spawned-reviewer");

        // Lease by profile identity should only find workers with that profile
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-profile-filter",
            ProfileIdentity = profileId,
        });
        Assert.NotNull(lease);
        Assert.Contains(lease.WorkerIdentity, new[] { member1, member2 });

        // The other-profile worker should still be available
        var other = await _repo.GetMemberAsync("other-worker");
        Assert.Equal(WorkerPoolStates.MemberAvailable, other!.Status);
    }

    [Fact]
    public async Task SharedProfile_ProfileIdentityRoundTrips()
    {
        var identity = "profile-rt-worker";
        var profileId = "spawned-coder";
        await SeedMemberAsync(identity, "[\"coder\"]", profileId);

        var member = await _repo.GetMemberAsync(identity);
        Assert.NotNull(member);
        Assert.Equal(profileId, member.ProfileIdentity);
    }

    [Fact]
    public async Task SharedProfile_MembersListedByProfileIdentity()
    {
        var profileId = "profile-list-group";
        await SeedMemberAsync("pl-alpha", "[\"coder\"]", profileId);
        await SeedMemberAsync("pl-beta", "[\"coder\"]", profileId);
        await SeedMemberAsync("pl-gamma", "[\"coder\"]", profileId);
        // A member with different profile
        await SeedMemberAsync("pl-other", "[\"coder\"]", "other-profile");

        var result = await _repo.ListMembersAsync(new WorkerPoolMemberListOptions
        {
            ProfileIdentity = profileId,
        });
        Assert.Equal(3, result.Count);
        Assert.All(result, m => Assert.Equal(profileId, m.ProfileIdentity));
    }

    // ─────────────────────────────────────────────────────────────────
    // No-Capacity Diagnostics
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LeaseWithDiagnostics_EmptyPool_ReturnsNoMatchingWorker()
    {
        // No workers in pool at all
        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-empty",
        });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);
        Assert.Equal(WorkerPoolStates.NoCapacityNoMatchingWorker, result.NoCapacity.ReasonCode);
        Assert.Equal("test-proj", result.NoCapacity.ProjectId);
        Assert.Equal("coder", result.NoCapacity.Role);
        Assert.Equal("runner", result.NoCapacity.AssignedBy);
        Assert.NotNull(result.NoCapacity.CandidateDetails);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_AllBusy_ReturnsAllBusy()
    {
        // Create a single busy worker (no available workers)
        await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "busy-worker-nc",
            Status = WorkerPoolStates.MemberBusy,
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
        });

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-busy",
            ProfileIdentity = "spawned-coder",
        });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);
        Assert.Equal(WorkerPoolStates.NoCapacityAllBusy, result.NoCapacity.ReasonCode);
        Assert.Contains("busy", result.NoCapacity.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_AllQuarantined_ReturnsAllQuarantined()
    {
        await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "quar-worker-nc",
            Status = WorkerPoolStates.MemberQuarantined,
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
        });

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-quar",
            ProfileIdentity = "spawned-coder",
        });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);
        Assert.Equal(WorkerPoolStates.NoCapacityAllQuarantinedOrOffline, result.NoCapacity.ReasonCode);
        Assert.Contains("quarantined", result.NoCapacity.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_PreferredWorkerNotFound_ReturnsPreferredNotFound()
    {
        await SeedMemberAsync("existing-worker-nc", "[\"coder\"]", "spawned-coder");

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-pref",
            PreferredWorkerIdentity = "nonexistent-pref",
        });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);
        Assert.Equal(WorkerPoolStates.NoCapacityPreferredNotFoundOrBusy, result.NoCapacity.ReasonCode);
        Assert.Equal("nonexistent-pref", result.NoCapacity.PreferredWorkerIdentity);
        Assert.Contains("not found", result.NoCapacity.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_PreferredWorkerBusy_ReturnsPreferredNotFound()
    {
        await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "pref-busy-nc",
            Status = WorkerPoolStates.MemberBusy,
        });

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-pref-busy",
            PreferredWorkerIdentity = "pref-busy-nc",
        });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);
        Assert.Equal(WorkerPoolStates.NoCapacityPreferredNotFoundOrBusy, result.NoCapacity.ReasonCode);
        Assert.Contains("not available", result.NoCapacity.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_CapabilityMismatch_ReturnsNoMatchingWorker()
    {
        // Workers exist but none have the required capabilities
        await SeedMemberAsync("cap-worker-1", "[\\\"reviewer\\\"]");
        await SeedMemberAsync("cap-worker-2", "[\\\"reviewer\\\"]");

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-cap",
            RequiredCapabilities = new[] { "coder", "dotnet" },
        });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);
        Assert.Equal(WorkerPoolStates.NoCapacityNoMatchingWorker, result.NoCapacity.ReasonCode);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_SuccessStillWorks()
    {
        await SeedMemberAsync("success-worker-nc");

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-success",
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Assignment);
        Assert.Equal("success-worker-nc", result.Assignment.WorkerIdentity);
        Assert.Equal(WorkerPoolStates.Ack, result.Assignment.State);
        Assert.Null(result.NoCapacity);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_PreferredWorkerSuccess()
    {
        await SeedMemberAsync("pref-success-nc");

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-pref-ok",
            PreferredWorkerIdentity = "pref-success-nc",
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Assignment);
        Assert.Equal("pref-success-nc", result.Assignment.WorkerIdentity);
    }

    [Fact]
    public async Task ListNoCapacityRequests_FiltersByProjectAndReason()
    {
        // Trigger a no-capacity event
        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-list-1",
        });
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);

        // Trigger another
        var result2 = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "reviewer",
            AssignedBy = "planner",
            RunId = "run-nocap-list-2",
        });
        Assert.False(result2.IsSuccess);

        // List by project
        var byProject = await _repo.ListNoCapacityRequestsAsync(new NoCapacityRequestListOptions
        {
            ProjectId = "test-proj",
        });
        Assert.Equal(2, byProject.Count);

        // List by reason code
        var byReason = await _repo.ListNoCapacityRequestsAsync(new NoCapacityRequestListOptions
        {
            ReasonCode = WorkerPoolStates.NoCapacityNoMatchingWorker,
        });
        Assert.Equal(2, byReason.Count);

        // List by run id
        var byRun = await _repo.ListNoCapacityRequestsAsync(new NoCapacityRequestListOptions
        {
            RunId = "run-nocap-list-1",
        });
        Assert.Single(byRun);
        Assert.Equal("run-nocap-list-1", byRun[0].RunId);
    }

    [Fact]
    public async Task GetNoCapacityRequest_ReturnsRecord()
    {
        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-nocap-get",
        });
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.NoCapacity);

        var fetched = await _repo.GetNoCapacityRequestAsync(result.NoCapacity.Id);
        Assert.NotNull(fetched);
        Assert.Equal(result.NoCapacity.Id, fetched.Id);
        Assert.Equal("test-proj", fetched.ProjectId);
        Assert.Equal("coder", fetched.Role);
        Assert.Equal("runner", fetched.AssignedBy);
        Assert.Equal(WorkerPoolStates.NoCapacityNoMatchingWorker, fetched.ReasonCode);
    }

    [Fact]
    public async Task GetNoCapacityRequest_Missing_ReturnsNull()
    {
        var fetched = await _repo.GetNoCapacityRequestAsync(99999);
        Assert.Null(fetched);
    }

    // ─────────────────────────────────────────────────────────────────
    // New: Shared-profile capacity lanes (#1804)
    // ─────────────────────────────────────────────────────────────────

    private async Task<(string ProfileIdentity, string WorkerRole)> SeedLaneAsync(
        string profileIdentity = "spawned-coder", string workerRole = "coder", int capacity = 4)
    {
        var lane = await _repo.UpsertLaneAsync(new WorkerPoolLane
        {
            ProfileIdentity = profileIdentity,
            WorkerRole = workerRole,
            Capacity = capacity,
            Status = WorkerPoolStates.LaneActive,
        });
        return (lane.ProfileIdentity, lane.WorkerRole);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_ReturnsCapacityOnSuccess()
    {
        await SeedLaneAsync("diag-cap-success", "coder", capacity: 2);
        await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "diag-cap-success-1",
            ProfileIdentity = "diag-cap-success",
            WorkerRole = "coder",
            Status = WorkerPoolStates.MemberAvailable,
            Capabilities = "[\"coder\"]",
        });
        await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "diag-cap-success-2",
            ProfileIdentity = "diag-cap-success",
            WorkerRole = "coder",
            Status = WorkerPoolStates.MemberAvailable,
            Capabilities = "[\"coder\"]",
        });

        var result = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-diag-cap-success",
            ProfileIdentity = "diag-cap-success",
            WorkerRole = "coder",
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Capacity);
        Assert.Equal("diag-cap-success", result.Capacity.ProfileIdentity);
        Assert.Equal(2, result.Capacity.TotalCapacity);
        Assert.Equal(1, result.Capacity.ActiveLeases);
        Assert.Equal(1, result.Capacity.AvailableSlots);
    }

    [Fact]
    public async Task LeaseWithDiagnostics_ReturnsCapacityWhenLaneFull()
    {
        await SeedLaneAsync("diag-cap-full", "coder", capacity: 1);
        for (var i = 1; i <= 2; i++)
        {
            await _repo.UpsertMemberAsync(new WorkerPoolMember
            {
                WorkerIdentity = $"diag-cap-full-{i}",
                ProfileIdentity = "diag-cap-full",
                WorkerRole = "coder",
                Status = WorkerPoolStates.MemberAvailable,
                Capabilities = "[\"coder\"]",
            });
        }

        var first = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-diag-cap-full-1",
            ProfileIdentity = "diag-cap-full",
            WorkerRole = "coder",
        });
        Assert.True(first.IsSuccess);

        var full = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-diag-cap-full-2",
            ProfileIdentity = "diag-cap-full",
            WorkerRole = "coder",
        });

        Assert.False(full.IsSuccess);
        Assert.NotNull(full.Capacity);
        Assert.Equal(1, full.Capacity.TotalCapacity);
        Assert.Equal(1, full.Capacity.ActiveLeases);
        Assert.Equal(0, full.Capacity.AvailableSlots);
    }

    [Fact]
    public async Task Lane_Crud_UpsertAndRetrieve()
    {
        var lane = await _repo.UpsertLaneAsync(new WorkerPoolLane
        {
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            Capacity = 4,
            Status = WorkerPoolStates.LaneActive,
        });
        Assert.Equal("spawned-coder", lane.ProfileIdentity);
        Assert.Equal("coder", lane.WorkerRole);
        Assert.Equal(4, lane.Capacity);
        Assert.Equal(WorkerPoolStates.LaneActive, lane.Status);

        var fetched = await _repo.GetLaneAsync("spawned-coder", "coder");
        Assert.NotNull(fetched);
        Assert.Equal(4, fetched.Capacity);
    }

    [Fact]
    public async Task Lane_Upsert_UpdatesExisting()
    {
        await SeedLaneAsync();
        var updated = await _repo.UpsertLaneAsync(new WorkerPoolLane
        {
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            Capacity = 8,
            Status = WorkerPoolStates.LaneActive,
        });
        Assert.Equal(8, updated.Capacity);
    }

    [Fact]
    public async Task Lane_List_FiltersByProfile()
    {
        await SeedLaneAsync("spawned-coder", "coder");
        await SeedLaneAsync("spawned-reviewer", "reviewer");

        var lanes = await _repo.ListLanesAsync("spawned-coder");
        Assert.Single(lanes);
        Assert.Equal("coder", lanes[0].WorkerRole);
    }

    [Fact]
    public async Task Lane_Quarantine_BlocksNewLeases()
    {
        await SeedLaneAsync("spawned-coder", "coder", capacity: 2);

        // Seed two members so one remains available after the first lease.
        for (var i = 1; i <= 2; i++)
        {
            await _repo.UpsertMemberAsync(new WorkerPoolMember
            {
                WorkerIdentity = $"lane-coder-{i}",
                ProfileIdentity = "spawned-coder",
                WorkerRole = "coder",
                Status = WorkerPoolStates.MemberAvailable,
                Capabilities = "[\"coder\"]",
            });
        }

        // First lease should succeed and remain active.
        var lease1 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-lane-q-1",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
        });
        Assert.NotNull(lease1);

        // Quarantine the lane. This blocks new leases without mutating concrete
        // member statuses or disturbing the active assignment.
        await _repo.SetLaneStatusAsync("spawned-coder", "coder", WorkerPoolStates.LaneQuarantined);

        var busyMember = await _repo.GetMemberAsync("lane-coder-1");
        var availableMember = await _repo.GetMemberAsync("lane-coder-2");
        Assert.Equal(WorkerPoolStates.MemberBusy, busyMember!.Status);
        Assert.Equal(WorkerPoolStates.MemberAvailable, availableMember!.Status);

        var assignment = await _repo.GetAssignmentAsync(lease1.Id);
        Assert.NotNull(assignment);
        Assert.False(WorkerPoolStates.IsTerminal(assignment.State));

        // New lease should fail despite an available member because the lane is quarantined.
        var lease2 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-lane-q-2",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
        });
        Assert.Null(lease2);
    }

    [Fact]
    public async Task Capacity_Accounting_ExhaustsAndSummarizes()
    {
        var capacity = 2;
        await SeedLaneAsync("spawned-coder", "coder", capacity);

        // Seed more concrete members than lane capacity. Capacity enforcement
        // must stop at the lane cap, not merely when all members are busy.
        var memberCount = 3;
        for (int i = 0; i < memberCount; i++)
        {
            await _repo.UpsertMemberAsync(new WorkerPoolMember
            {
                WorkerIdentity = $"slot-{i + 1}",
                ProfileIdentity = "spawned-coder",
                WorkerRole = "coder",
                Status = WorkerPoolStates.MemberAvailable,
                Capabilities = "[\"coder\"]",
            });
        }

        // Lease all 3 slots
        for (int i = 0; i < capacity; i++)
        {
            var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
            {
                ProjectId = "test-proj",
                Role = "coder",
                AssignedBy = "runner",
                RunId = $"run-cap-{i}",
                ProfileIdentity = "spawned-coder",
                WorkerRole = "coder",
            });
            Assert.NotNull(lease);
        }

        // Capacity summary should show 3/3 busy (computed from non-terminal assignments)
        var summary = await _repo.GetProfileCapacitySummaryAsync("spawned-coder");
        Assert.Equal(capacity, summary.TotalCapacity);
        Assert.Equal(capacity, summary.ActiveLeases);
        Assert.Equal(0, summary.AvailableSlots);
        Assert.Single(summary.Lanes);
        Assert.Equal(capacity, summary.Lanes[0].BusyCount);

        // Next lease should fail because lane capacity is full even though one member remains available.
        var failed = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-cap-full",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
        });
        Assert.Null(failed);
    }

    [Fact]
    public async Task ProfileCapacitySummary_EmptyProfile_ReturnsZeroes()
    {
        var summary = await _repo.GetProfileCapacitySummaryAsync("nonexistent");
        Assert.Equal("nonexistent", summary.ProfileIdentity);
        Assert.Equal(0, summary.TotalCapacity);
        Assert.Equal(0, summary.ActiveLeases);
        Assert.Equal(0, summary.AvailableSlots);
        Assert.Empty(summary.Lanes);
    }

    [Fact]
    public async Task LeaseId_PopulatedAndRaceSafe()
    {
        await SeedMemberAsync("leaseid-worker", profileIdentity: "spawned-coder");

        var lease1 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-leaseid-1",
            PreferredWorkerIdentity = "leaseid-worker",
        });
        Assert.NotNull(lease1);
        Assert.Equal("leaseid-worker:run-leaseid-1", lease1.LeaseId);

        // Release the first assignment
        await _repo.TransitionAssignmentStateAsync(lease1.Id, WorkerPoolStates.Completed);
        await _repo.SetMemberStatusAsync("leaseid-worker", WorkerPoolStates.MemberAvailable);

        // Lease again — different run_id, different lease_id
        var lease2 = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-leaseid-2",
            PreferredWorkerIdentity = "leaseid-worker",
        });
        Assert.NotNull(lease2);
        Assert.Equal("leaseid-worker:run-leaseid-2", lease2.LeaseId);
        Assert.NotEqual(lease1.LeaseId, lease2.LeaseId);
    }

    [Fact]
    public async Task RunIdMismatchGuard_RejectsWrongRunId()
    {
        await SeedMemberAsync("guard-worker");
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-guard-1",
            PreferredWorkerIdentity = "guard-worker",
        });
        Assert.NotNull(lease);

        // Try to append checkpoint with wrong run_id
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.AppendCheckpointAsync(lease.Id, "wrong-run-id",
                WorkerPoolStates.CheckpointProgress, "{}"));
    }

    [Fact]
    public async Task StaleDetection_ReleasesOverdueWorkers()
    {
        // Seed as available first, then make stale after lease
        await SeedMemberAsync("stale-worker", profileIdentity: "spawned-coder");

        // Lease the worker (transitions to busy)
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-stale-1",
            PreferredWorkerIdentity = "stale-worker",
        });
        Assert.NotNull(lease);

        // Now make the worker stale by updating last_heartbeat and stale_after_seconds
        await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "stale-worker",
            ProfileIdentity = "spawned-coder",
            Status = WorkerPoolStates.MemberBusy,
            StaleAfterSeconds = 1,
            LastHeartbeat = DateTime.UtcNow.AddSeconds(-10).ToString("o"),
        });

        // Release stale leases
        var count = await _repo.ReleaseStaleLeasesAsync();
        Assert.Equal(1, count);

        // Worker should be back to available
        var member = await _repo.GetMemberAsync("stale-worker");
        Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);

        // Assignment should be expired
        var assignment = await _repo.GetAssignmentByRunIdAsync("run-stale-1");
        Assert.NotNull(assignment);
        Assert.Equal(WorkerPoolStates.Expired, assignment.State);
    }

    [Fact]
    public async Task StaleDetection_IgnoresNonStaleWorkers()
    {
        await SeedMemberAsync("fresh-worker", profileIdentity: "spawned-coder");

        await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-fresh-1",
            PreferredWorkerIdentity = "fresh-worker",
        });

        // Set a far-future stale timeout — not stale
        await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "fresh-worker",
            ProfileIdentity = "spawned-coder",
            Status = WorkerPoolStates.MemberBusy,
            StaleAfterSeconds = 3600,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var count = await _repo.ReleaseStaleLeasesAsync();
        Assert.Equal(0, count);

        // Worker should still be busy
        var member = await _repo.GetMemberAsync("fresh-worker");
        Assert.Equal(WorkerPoolStates.MemberBusy, member!.Status);
    }

    [Fact]
    public async Task GatewayFields_RoundTrip()
    {
        var member = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "gw-worker",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            Status = WorkerPoolStates.MemberAvailable,
            AgentInstanceId = "agent-inst-abc",
            AdapterInstanceId = "adapter-inst-xyz",
            SessionId = "session-123",
            ChannelId = "ch-1",
            LogPointer = "/var/log/worker-1.log",
            StaleAfterSeconds = 60,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
            Capabilities = "[\"coder\"]",
        });

        Assert.Equal("gw-worker", member.WorkerIdentity);
        Assert.Equal("agent-inst-abc", member.AgentInstanceId);
        Assert.Equal("adapter-inst-xyz", member.AdapterInstanceId);
        Assert.Equal("session-123", member.SessionId);
        Assert.Equal("ch-1", member.ChannelId);
        Assert.Equal("/var/log/worker-1.log", member.LogPointer);
        Assert.Equal(60, member.StaleAfterSeconds);

        // Read back
        var fetched = await _repo.GetMemberAsync("gw-worker");
        Assert.Equal("adapter-inst-xyz", fetched!.AdapterInstanceId);
        Assert.Equal("/var/log/worker-1.log", fetched.LogPointer);
        Assert.Equal(60, fetched.StaleAfterSeconds);
    }

    [Fact]
    public async Task BackwardCompat_LegacyMemberWithoutNewFields()
    {
        // Seed using only legacy fields
        var member = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "legacy-worker",
            Status = WorkerPoolStates.MemberAvailable,
        });

        Assert.Equal("legacy-worker", member.WorkerIdentity);
        Assert.Null(member.AdapterInstanceId);
        Assert.Null(member.LogPointer);
        Assert.Null(member.StaleAfterSeconds);

        // Lease should work without lane (unbounded capacity)
        var lease = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-legacy-1",
            PreferredWorkerIdentity = "legacy-worker",
        });
        Assert.NotNull(lease);
    }

    // ─────────────────────────────────────────────────────────────────
    // Orchestrator Lease lifecycle
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrchLease_CreateAndReadByInternalId()
    {
        await SeedProjectOrchestratorMemberAsync("orch-1");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "den-mcp-runner",
            ScopeType = WorkerPoolStates.ScopeProject,
            Objective = "Orchestrate den-core task dispatch",
            PreferredOrchestratorIdentity = "orch-1",
            ProfileIdentity = "pooled-orchestrator",
        });

        Assert.True(lease.Id > 0);
        Assert.Equal(WorkerPoolStates.OrchLeaseLeased, lease.State);
        Assert.Equal(WorkerPoolStates.LeaseKindProjectOrchestrator, lease.LeaseKind);
        Assert.Equal(WorkerPoolStates.ScopeProject, lease.ScopeType);
        Assert.Equal("test-proj", lease.ProjectId);
        Assert.Equal("orch-1", lease.OrchestratorIdentity);
        Assert.Equal("pooled-orchestrator", lease.ProfileIdentity);
        Assert.Equal("den-mcp-runner", lease.LeaseOwner);
        Assert.Equal("Orchestrate den-core task dispatch", lease.Objective);
        Assert.Null(lease.LeaseExpiresAt); // indefinite

        // Read back by internal id
        var read = await _repo.GetOrchestratorLeaseAsync(lease.Id);
        Assert.NotNull(read);
        Assert.Equal(lease.LeaseId, read!.LeaseId);
    }

    [Fact]
    public async Task OrchLease_ReadByLeaseId()
    {
        await SeedProjectOrchestratorMemberAsync("orch-2");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-2",
        });

        var read = await _repo.GetOrchestratorLeaseByLeaseIdAsync(lease.LeaseId);
        Assert.NotNull(read);
        Assert.Equal(lease.Id, read!.Id);
    }

    [Fact]
    public async Task OrchLease_PreferredWorkerMustBeProjectOrchestrator()
    {
        await SeedMemberAsync("orch-wrong-role", profileIdentity: "pooled-orchestrator");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
            {
                ProjectId = "test-proj",
                LeaseOwner = "runner",
                PreferredOrchestratorIdentity = "orch-wrong-role",
                ProfileIdentity = "pooled-orchestrator",
            }));

        Assert.Contains("Preferred orchestrator", ex.Message);
    }

    [Fact]
    public async Task OrchLease_InvalidRequestedDurationThrows()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
            {
                ProjectId = "test-proj",
                LeaseOwner = "runner",
                RequestedDurationSeconds = 0,
            }));

        Assert.Contains("requested_duration_seconds", ex.Message);
    }

    [Fact]
    public async Task OrchLease_FullLifecycleTransition()
    {
        await SeedProjectOrchestratorMemberAsync("orch-3");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-3",
        });

        // leased -> active
        var active = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseActive });
        Assert.NotNull(active);
        Assert.Equal(WorkerPoolStates.OrchLeaseActive, active!.State);

        // active -> checkpoint_waiting
        var cp = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseCheckpointWaiting });
        Assert.NotNull(cp);
        Assert.Equal(WorkerPoolStates.OrchLeaseCheckpointWaiting, cp!.State);

        // checkpoint_waiting -> active
        var activeAgain = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseActive });
        Assert.NotNull(activeAgain);

        // active -> draining
        var draining = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseDraining });
        Assert.NotNull(draining);
        Assert.Equal(WorkerPoolStates.OrchLeaseDraining, draining!.State);

        // draining -> released (terminal)
        var released = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput
            {
                LeaseInternalId = lease.Id,
                NewState = WorkerPoolStates.OrchLeaseReleased,
                Evidence = """{"log_path":"/tmp/orch-3.log"}""",
            });
        Assert.NotNull(released);
        Assert.Equal(WorkerPoolStates.OrchLeaseReleased, released!.State);
        Assert.Contains("log_path", released.CleanupEvidence);
        Assert.NotNull(released.CleanupRecordedAt);
        Assert.NotNull(released.ActualDurationSeconds);

        // Verify pool member released back to available
        var member = await _repo.GetMemberAsync("orch-3");
        Assert.NotNull(member);
        Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);
    }

    [Fact]
    public async Task OrchLease_InvalidTransition_ReturnsNull()
    {
        await SeedProjectOrchestratorMemberAsync("orch-4");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-4",
        });

        // leased -> proposed (backward transition not allowed)
        var result = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseProposed });
        Assert.Null(result);

        // leased -> released, then released -> active (terminal -> non-terminal not allowed)
        await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseReleased });
        var postTerminal = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseActive });
        Assert.Null(postTerminal);
    }

    [Fact]
    public async Task OrchLease_WithExpiry()
    {
        await SeedProjectOrchestratorMemberAsync("orch-5");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-5",
            RequestedDurationSeconds = 3600,
            RenewalPolicy = WorkerPoolStates.RenewalPolicyAllow,
            DrainPolicy = WorkerPoolStates.DrainPolicyImmediate,
        });

        Assert.NotNull(lease.LeaseExpiresAt);
        Assert.Equal(WorkerPoolStates.RenewalPolicyAllow, lease.RenewalPolicy);
        Assert.Equal(WorkerPoolStates.DrainPolicyImmediate, lease.DrainPolicy);
        Assert.Equal(3600, lease.RequestedDurationSeconds);
    }

    [Fact]
    public async Task OrchLease_ListWithFilters()
    {
        await SeedProjectOrchestratorMemberAsync("orch-a");
        await SeedProjectOrchestratorMemberAsync("orch-b");

        await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-a",
        });

        await _projects.CreateAsync(new Project { Id = "proj-2", Name = "Project 2" });

        await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "proj-2",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-b",
            ScopeType = WorkerPoolStates.ScopeChannel,
            ChannelId = "ch-42",
        });

        // Filter by project
        var forTestProj = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { ProjectId = "test-proj" });
        Assert.Single(forTestProj);

        var forProj2 = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { ProjectId = "proj-2" });
        Assert.Single(forProj2);

        // Filter by scope
        var channelScoped = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { ScopeType = WorkerPoolStates.ScopeChannel });
        Assert.Single(channelScoped);
        Assert.Equal("ch-42", channelScoped[0].ChannelId);
    }

    [Fact]
    public async Task OrchLease_ExcludeTerminalByDefault()
    {
        await SeedProjectOrchestratorMemberAsync("orch-term");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-term",
        });

        // Should appear in non-terminal list
        var active = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { ProjectId = "test-proj", IncludeTerminal = false });
        Assert.Single(active);

        // Transition to terminal
        await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseReleased });

        // Should not appear in non-terminal list
        var afterRelease = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { ProjectId = "test-proj", IncludeTerminal = false });
        Assert.Empty(afterRelease);

        // Should appear when including terminal
        var withTerminal = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { ProjectId = "test-proj", IncludeTerminal = true });
        Assert.Single(withTerminal);
    }

    [Fact]
    public async Task OrchLease_CleanupEvidenceOnTerminal()
    {
        await SeedProjectOrchestratorMemberAsync("orch-cleanup");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-cleanup",
        });

        // Transition to quarantined (terminal)
        await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput { LeaseInternalId = lease.Id, NewState = WorkerPoolStates.OrchLeaseQuarantined });

        // Record cleanup evidence
        var withCleanup = await _repo.RecordOrchestratorLeaseCleanupAsync(lease.Id, """{"drained":true}""");
        Assert.NotNull(withCleanup);
        Assert.Contains("drained", withCleanup!.CleanupEvidence);
        Assert.NotNull(withCleanup.CleanupRecordedAt);
    }

    [Fact]
    public async Task OrchLease_ReconcileExpiresStaleLeases()
    {
        await SeedProjectOrchestratorMemberAsync("orch-stale");

        // Create lease with normal duration, then directly update lease_expires_at to past
        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-stale",
            RequestedDurationSeconds = 3600,
        });

        Assert.NotNull(lease.LeaseExpiresAt);

        // Manually set expires_at to past
        await using (var conn = await _testDb.Db.CreateConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE orchestrator_leases SET lease_expires_at = datetime('now', '-1 hour') WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", lease.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        var affected = await _repo.ReconcileStaleOrchestratorLeasesAsync();
        Assert.True(affected >= 1);

        var expired = await _repo.GetOrchestratorLeaseAsync(lease.Id);
        Assert.Equal(WorkerPoolStates.OrchLeaseExpired, expired!.State);

        // Pool member should be back to available
        var member = await _repo.GetMemberAsync("orch-stale");
        Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);
    }

    [Fact]
    public async Task OrchLease_ReconcileDegradesHeartbeatStale()
    {
        // Seed member with stale_after_seconds and old heartbeat
        var member = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "orch-hb-stale",
            ProfileIdentity = "pooled-orchestrator",
            WorkerRole = "project_orchestrator",
            Status = WorkerPoolStates.MemberBusy,
            LastHeartbeat = DateTime.UtcNow.AddHours(-2).ToString("o"),
            StaleAfterSeconds = 60, // stale after 1 minute
        });

        // Create lease directly (bypassing the member selection which would check availability)
        await using var conn = await _testDb.Db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO orchestrator_leases (lease_id, lease_kind, scope_type, project_id,
                lease_owner, orchestrator_identity, profile_identity, state)
            VALUES ('test-hb-stale:1', 'project_orchestrator', 'project', 'test-proj',
                'runner', 'orch-hb-stale', 'pooled-orchestrator', 'active')
            """;
        await cmd.ExecuteNonQueryAsync();

        var affected = await _repo.ReconcileStaleOrchestratorLeasesAsync();
        Assert.True(affected >= 1);

        // Verify lease degraded
        var leases = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { OrchestratorIdentity = "orch-hb-stale", IncludeTerminal = true });
        Assert.Single(leases);
        Assert.Equal(WorkerPoolStates.OrchLeaseDegraded, leases[0].State);

        // Profile not permanently busy
        var refreshedMember = await _repo.GetMemberAsync("orch-hb-stale");
        Assert.Equal(WorkerPoolStates.MemberAvailable, refreshedMember!.Status);
    }

    [Fact]
    public async Task OrchLease_PoolResidencyProjection()
    {
        // Setup: one task-worker assignment and one orchestrator lease
        await SeedMemberAsync("worker-a", profileIdentity: "spawned-coder");
        await SeedProjectOrchestratorMemberAsync("orch-res");

        var taskId = await SeedTaskAsync();

        // Create task-worker assignment
        await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            TaskId = taskId,
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-res-1",
            PreferredWorkerIdentity = "worker-a",
        });

        // Create orchestrator lease
        await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-res",
            Objective = "Project orchestration",
        });

        var projections = await _repo.GetPoolResidencyProjectionAsync("test-proj");

        // Should have both a task_worker_assignment and an orchestrator_lease
        Assert.Equal(2, projections.Count);
        Assert.Contains(projections, p => p.ResidencyKind == "task_worker_assignment" && p.WorkerIdentity == "worker-a");
        Assert.Contains(projections, p => p.ResidencyKind == "orchestrator_lease" && p.WorkerIdentity == "orch-res");

        var orchProj = projections.First(p => p.ResidencyKind == "orchestrator_lease");
        Assert.Equal("test-proj", orchProj.ProjectId);
        Assert.Equal(WorkerPoolStates.OrchLeaseLeased, orchProj.State);
        Assert.NotNull(orchProj.StartedAt);
    }

    [Fact]
    public async Task OrchLease_BackwardCompat_TaskWorkerUnaffected()
    {
        // Verify that existing task-scoped worker assignments still work after changes
        await SeedMemberAsync("bw-worker", profileIdentity: "spawned-coder");

        var assignment = await _repo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-bw-1",
            PreferredWorkerIdentity = "bw-worker",
        });

        Assert.NotNull(assignment);
        Assert.Equal(WorkerPoolStates.LeaseKindTaskWorker, assignment!.LeaseKind);
        Assert.Equal("ack", assignment.State);

        // Transition through normal lifecycle
        await _repo.TransitionAssignmentStateAsync(assignment.Id, WorkerPoolStates.Running);
        await _repo.AppendCheckpointAsync(assignment.Id, "run-bw-1", WorkerPoolStates.CheckpointCompletion, """{"status":"done"}""");

        var completed = await _repo.GetAssignmentAsync(assignment.Id);
        Assert.Equal("completed", completed!.State);

        // Member should be available again
        var member = await _repo.GetMemberAsync("bw-worker");
        Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);
    }

    [Fact]
    public async Task OrchLease_NoConfusionBetweenBindingAndLease()
    {
        await SeedProjectOrchestratorMemberAsync("bind-orch");

        // Create orchestrator lease
        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "bind-orch",
        });

        // The member is busy due to the lease
        var member = await _repo.GetMemberAsync("bind-orch");
        Assert.Equal(WorkerPoolStates.MemberBusy, member!.Status);

        // No active task-worker assignment for this member
        var assignments = await _repo.ListAssignmentsAsync(
            new WorkerAssignmentListOptions { WorkerIdentity = "bind-orch", State = "ack" });
        Assert.Empty(assignments);

        // But an active orchestrator lease exists
        var leases = await _repo.ListOrchestratorLeasesAsync(
            new OrchestratorLeaseListOptions { OrchestratorIdentity = "bind-orch" });
        Assert.Single(leases);
        Assert.Equal(lease.Id, leases[0].Id);
    }

    [Fact]
    public async Task OrchLease_SchemaCreatesOrchestratorLeasesTable()
    {
        await using var conn = await _testDb.Db.CreateConnectionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='orchestrator_leases'";
        Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);

        // Check indexes
        foreach (var idx in new[] {
            "idx_orchestrator_leases_project",
            "idx_orchestrator_leases_orchestrator",
            "idx_orchestrator_leases_state",
            "idx_orchestrator_leases_expires",
            "idx_orchestrator_leases_lease_kind",
        })
        {
            await using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='index' AND name='{idx}'";
            Assert.Equal(1L, (await idxCmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task OrchLease_WorkerAssignmentHasLeaseKindColumn()
    {
        // Verify lease_kind column was added to worker_assignments
        await SeedMemberAsync("lk-worker", profileIdentity: "spawned-coder");

        var assignment = await _repo.LeaseWorkerWithDiagnosticsAsync(new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-lk-1",
            PreferredWorkerIdentity = "lk-worker",
        });

        Assert.NotNull(assignment);
        Assert.Equal(WorkerPoolStates.LeaseKindTaskWorker, assignment!.Assignment!.LeaseKind);
    }

    [Fact]
    public async Task OrchLease_ChannelAndWorkstreamScope()
    {
        await SeedProjectOrchestratorMemberAsync("orch-scope");

        var taskId = await SeedTaskAsync();

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-scope",
            ScopeType = WorkerPoolStates.ScopeWorkstream,
            WorkstreamHandle = "ws-infrastructure",
            TaskId = taskId,
            ChannelId = "ch-99",
        });

        Assert.Equal(WorkerPoolStates.ScopeWorkstream, lease.ScopeType);
        Assert.Equal("ws-infrastructure", lease.WorkstreamHandle);
        Assert.Equal(taskId, lease.TaskId);
        Assert.Equal("ch-99", lease.ChannelId);
    }

    [Fact]
    public async Task OrchLease_QuarantineDoesNotPermanentlyBusyProfile()
    {
        await SeedProjectOrchestratorMemberAsync("orch-quar");

        var lease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-quar",
        });

        // Quarantine the lease
        var result = await _repo.TransitionOrchestratorLeaseAsync(
            new TransitionOrchestratorLeaseInput
            {
                LeaseInternalId = lease.Id,
                NewState = WorkerPoolStates.OrchLeaseQuarantined,
                Evidence = """{"reason":"config_drift"}""",
            });

        Assert.NotNull(result);
        Assert.Equal(WorkerPoolStates.OrchLeaseQuarantined, result!.State);

        // Profile should be available again (not permanently busy)
        var member = await _repo.GetMemberAsync("orch-quar");
        Assert.Equal(WorkerPoolStates.MemberAvailable, member!.Status);

        // The same pool member can be leased again for a different project
        await _projects.CreateAsync(new Project { Id = "proj-other", Name = "Other" });
        var newLease = await _repo.CreateOrchestratorLeaseAsync(new CreateOrchestratorLeaseInput
        {
            ProjectId = "proj-other",
            LeaseOwner = "runner",
            PreferredOrchestratorIdentity = "orch-quar",
        });
        Assert.Equal(WorkerPoolStates.OrchLeaseLeased, newLease.State);
    }
}
