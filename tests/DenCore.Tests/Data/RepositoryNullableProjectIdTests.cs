using DenCore.Data;
using DenCore.Models;

namespace DenCore.Tests.Data;

/// <summary>
/// Direct repository tests for nullable project_id flowing through production
/// repository paths (not just direct SQL). Addresses acceptance gaps from
/// validation packet #13214 for task #1931.
/// </summary>
public class RepositoryNullableProjectIdTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private IAgentInstanceBindingRepository _bindings = null!;
    private IWorkerPoolRepository _workers = null!;
    private IDesktopSessionEventRepository _events = null!;
    private IProjectRepository _projects = null!;
    private ITaskRepository _tasks = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _bindings = new AgentInstanceBindingRepository(_testDb.Db);
        _workers = new WorkerPoolRepository(_testDb.Db);
        _events = new DesktopSessionEventRepository(_testDb.Db);
        _projects = new ProjectRepository(_testDb.Db);
        _tasks = new TaskRepository(_testDb.Db);
        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    // ── AgentInstanceBindingRepository ───────────────────────────────────

    [Fact]
    public async Task AgentInstanceBinding_UpsertWithNullProjectId_ReadsBackNull()
    {
        var binding = new AgentInstanceBinding
        {
            InstanceId = "inst-null-proj",
            ProjectId = null,                // NULL project — cross-project binding
            AgentIdentity = "global-worker",
            AgentFamily = "hermes",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        };

        var result = await _bindings.UpsertAsync(binding);
        Assert.Equal("inst-null-proj", result.InstanceId);
        Assert.Null(result.ProjectId);
        Assert.Equal("global-worker", result.AgentIdentity);
    }

    [Fact]
    public async Task AgentInstanceBinding_UpsertWithNonNullProjectId_ReadsBackProjectId()
    {
        var binding = new AgentInstanceBinding
        {
            InstanceId = "inst-proj",
            ProjectId = "test-proj",
            AgentIdentity = "proj-worker",
            AgentFamily = "hermes",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        };

        var result = await _bindings.UpsertAsync(binding);
        Assert.Equal("test-proj", result.ProjectId);
    }

    [Fact]
    public async Task AgentInstanceBinding_ListByProject_ReturnsOnlyMatchingProject()
    {
        // Insert one with test-proj and one with NULL project_id
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "inst-a", ProjectId = "test-proj",
            AgentIdentity = "a", AgentFamily = "h", TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        });
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "inst-b", ProjectId = null,
            AgentIdentity = "b", AgentFamily = "h", TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        });

        // List by test-proj — should only get inst-a (project filter preservation)
        var projList = await _bindings.ListAsync(new AgentInstanceBindingListOptions { ProjectId = "test-proj" });
        Assert.Single(projList);
        Assert.Equal("inst-a", projList[0].InstanceId);
    }

    // ── DesktopSessionEventRepository ────────────────────────────────────

    [Fact]
    public async Task DesktopSessionEvent_AppendWithNullProjectId_ReadsBackNull()
    {
        var evt = new DesktopSessionEvent
        {
            ProjectId = null,                 // NULL project
            SourceInstanceId = "src-1",
            SessionId = "sess-1",
            EventType = "created",
            Payload = "{}",
            ObservedAt = DateTime.UtcNow,
        };

        var result = await _events.AppendAsync(evt);
        Assert.Null(result.ProjectId);
        Assert.Equal("sess-1", result.SessionId);
    }

    [Fact]
    public async Task DesktopSessionEvent_AppendWithNonNullProjectId_ReadsBackProjectId()
    {
        var evt = new DesktopSessionEvent
        {
            ProjectId = "test-proj",
            SourceInstanceId = "src-2",
            SessionId = "sess-2",
            EventType = "created",
            Payload = "{}",
            ObservedAt = DateTime.UtcNow,
        };

        var result = await _events.AppendAsync(evt);
        Assert.Equal("test-proj", result.ProjectId);
    }

    // ── LeaseAvailableWorkerAsync with null ProjectId ────────────────────

    [Fact]
    public async Task LeaseWorker_WithNullProjectId_CreatesAssignment()
    {
        // Seed a pool member
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "wk-null",
            ProfileIdentity = "spawned-coder",
            DisplayName = "Null Proj Worker",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new LeaseWorkerInput
        {
            ProjectId = null,                 // NULL project
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-null-proj",
            PreferredWorkerIdentity = "wk-null",
        };

        var assignment = await _workers.LeaseAvailableWorkerAsync(input);
        Assert.NotNull(assignment);
        Assert.Equal("run-null-proj", assignment!.RunId);
        Assert.Null(assignment.ProjectId);
    }

    // ── CreateOrchestratorLeaseAsync with null ProjectId ─────────────────

    [Fact]
    public async Task CreateOrchLease_WithNullProjectId_CreatesLease()
    {
        // Seed a pool member with orchestrator role
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "orch-null",
            ProfileIdentity = "runner",
            WorkerRole = "project_orchestrator",
            DisplayName = "Null Proj Orch",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new CreateOrchestratorLeaseInput
        {
            ProjectId = null,                 // NULL project
            LeaseOwner = "admin",
            PreferredOrchestratorIdentity = "orch-null",
        };

        var lease = await _workers.CreateOrchestratorLeaseAsync(input);
        Assert.NotNull(lease);
        Assert.Equal("orch-null", lease.OrchestratorIdentity);
        Assert.Null(lease.ProjectId);
    }

    // ── LeaseWorkerAsync with non-null ProjectId (project-filter works) ──

    [Fact]
    public async Task LeaseWorker_WithNonNullProjectId_PreservesProjectOnAssignment()
    {
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "wk-proj",
            ProfileIdentity = "spawned-coder",
            DisplayName = "Proj Worker",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-proj-1",
            PreferredWorkerIdentity = "wk-proj",
        };

        var assignment = await _workers.LeaseAvailableWorkerAsync(input);
        Assert.NotNull(assignment);
        Assert.Equal("test-proj", assignment!.ProjectId);
    }

    // ── CreateOrchestratorLeaseAsync with non-null ProjectId ─────────────

    [Fact]
    public async Task CreateOrchLease_WithNonNullProjectId_PreservesProjectOnLease()
    {
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "orch-proj",
            ProfileIdentity = "runner",
            WorkerRole = "project_orchestrator",
            DisplayName = "Proj Orch",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "admin",
            PreferredOrchestratorIdentity = "orch-proj",
        };

        var lease = await _workers.CreateOrchestratorLeaseAsync(input);
        Assert.NotNull(lease);
        Assert.Equal("test-proj", lease.ProjectId);
    }
}
