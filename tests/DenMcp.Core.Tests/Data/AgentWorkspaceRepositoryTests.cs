using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class AgentWorkspaceRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentWorkspaceRepository _workspaces = null!;
    private ProjectTask _task = null!;
    private ProjectTask _otherTask = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _workspaces = new AgentWorkspaceRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
        await projects.CreateAsync(new Project { Id = "other", Name = "Other" });

        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Workspace host"
        });
        _otherTask = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "other",
            Title = "Other workspace host"
        });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpsertAsync_CreatesWorkspaceWithRequiredConductorFields()
    {
        var workspace = await _workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = "ws-task-branch",
            ProjectId = "proj",
            TaskId = _task.Id,
            Branch = "task/809-agent-workspace-backend",
            WorktreePath = "/home/patch/dev/den-mcp",
            BaseBranch = "main",
            BaseCommit = "base-sha",
            HeadCommit = "head-sha",
            State = AgentWorkspaceState.Active,
            DevServerUrl = "http://localhost:5199",
            PreviewUrl = "http://localhost:5199/web",
            CleanupPolicy = AgentWorkspaceCleanupPolicy.Keep,
            ChangedFileSummary = JsonSerializer.Deserialize<JsonElement>("""
                {"files":[{"path":"src/Foo.cs","status":"modified"}],"counts":{"modified":1}}
                """)
        });

        Assert.Equal("ws-task-branch", workspace.Id);
        Assert.Equal("proj", workspace.ProjectId);
        Assert.Equal(_task.Id, workspace.TaskId);
        Assert.Equal("task/809-agent-workspace-backend", workspace.Branch);
        Assert.Equal("main", workspace.BaseBranch);
        Assert.Equal("base-sha", workspace.BaseCommit);
        Assert.Equal("head-sha", workspace.HeadCommit);
        Assert.Equal(AgentWorkspaceState.Active, workspace.State);
        Assert.Equal(AgentWorkspaceCleanupPolicy.Keep, workspace.CleanupPolicy);
        Assert.NotNull(workspace.ChangedFileSummary);
        Assert.Equal("src/Foo.cs", workspace.ChangedFileSummary!.Value.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.True(workspace.CreatedAt <= workspace.UpdatedAt);

        var loaded = await _workspaces.GetAsync("ws-task-branch", "proj");
        Assert.NotNull(loaded);
        Assert.Equal("http://localhost:5199/web", loaded!.PreviewUrl);
    }

    [Fact]
    public async Task UpsertAsync_UpdatesExistingProjectTaskBranchTupleWithoutChangingId()
    {
        await _workspaces.UpsertAsync(NewWorkspace("ws-original", "task/shared"));

        var updated = await _workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = "ws-new-id-ignored-for-existing-tuple",
            ProjectId = "proj",
            TaskId = _task.Id,
            Branch = "task/shared",
            WorktreePath = "/tmp/worktree-updated",
            BaseBranch = "main",
            HeadCommit = "new-head",
            State = AgentWorkspaceState.Review,
            CleanupPolicy = AgentWorkspaceCleanupPolicy.DeleteWorktree
        });

        Assert.Equal("ws-original", updated.Id);
        Assert.Equal("/tmp/worktree-updated", updated.WorktreePath);
        Assert.Equal("new-head", updated.HeadCommit);
        Assert.Equal(AgentWorkspaceState.Review, updated.State);
        Assert.Equal(AgentWorkspaceCleanupPolicy.DeleteWorktree, updated.CleanupPolicy);

        var listed = await _workspaces.ListAsync(new AgentWorkspaceListOptions { ProjectId = "proj", TaskId = _task.Id, Limit = 10 });
        var only = Assert.Single(listed, workspace => workspace.Branch == "task/shared");
        Assert.Equal("ws-original", only.Id);
    }

    [Fact]
    public async Task ListAsync_FiltersByProjectTaskAndState()
    {
        await _workspaces.UpsertAsync(NewWorkspace("ws-active", "task/active"));
        await _workspaces.UpsertAsync(NewWorkspace("ws-complete", "task/complete", AgentWorkspaceState.Complete));

        var active = await _workspaces.ListAsync(new AgentWorkspaceListOptions
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            State = AgentWorkspaceState.Active,
            Limit = 10
        });

        var workspace = Assert.Single(active);
        Assert.Equal("ws-active", workspace.Id);

        Assert.Empty(await _workspaces.ListAsync(new AgentWorkspaceListOptions { ProjectId = "other", Limit = 10 }));
    }

    [Fact]
    public async Task UpsertAsync_RejectsTaskFromDifferentProject()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = "ws-cross-project",
            ProjectId = "proj",
            TaskId = _otherTask.Id,
            Branch = "task/cross-project",
            WorktreePath = "/tmp/cross-project",
            BaseBranch = "main"
        }));

        Assert.Contains("belongs to project 'other'", ex.Message);
    }

    private AgentWorkspace NewWorkspace(string id, string branch, AgentWorkspaceState state = AgentWorkspaceState.Active) => new()
    {
        Id = id,
        ProjectId = "proj",
        TaskId = _task.Id,
        Branch = branch,
        WorktreePath = $"/tmp/{id}",
        BaseBranch = "main",
        BaseCommit = "base",
        HeadCommit = "head",
        State = state,
        CleanupPolicy = AgentWorkspaceCleanupPolicy.Keep
    };
}
