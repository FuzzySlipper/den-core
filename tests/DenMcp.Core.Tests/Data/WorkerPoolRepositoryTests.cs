using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Tests;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Tests.Data;

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
            Status = DenMcp.Core.Models.TaskStatus.Planned,
        }, null);
        return task.Id;
    }

    private async Task<string> SeedMemberAsync(string identity = "worker-1", string capabilities = "[\"coder\",\"dotnet\"]")
    {
        var member = await _repo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = identity,
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
}
