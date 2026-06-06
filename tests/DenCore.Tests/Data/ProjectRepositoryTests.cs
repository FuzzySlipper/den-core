using DenCore.Data;
using DenCore.Models;

namespace DenCore.Tests.Data;

public class ProjectRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private ProjectRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new ProjectRepository(_testDb.Db);
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateAndGet_RoundTrips()
    {
        var project = await _repo.CreateAsync(new Project { Id = "test", Name = "Test Project", Description = "A test" });
        Assert.Equal("test", project.Id);
        Assert.Equal("Test Project", project.Name);
        Assert.Equal("project", project.Kind);
        Assert.Equal("normal", project.Visibility);

        var fetched = await _repo.GetByIdAsync("test");
        Assert.NotNull(fetched);
        Assert.Equal("Test Project", fetched.Name);
        Assert.Equal("project", fetched.Kind);
        Assert.Equal("normal", fetched.Visibility);
    }

    [Fact]
    public async Task Create_NonProjectSpace_RoundTrips()
    {
        var space = await _repo.CreateAsync(new Project
        {
            Id = "assistant-1",
            Name = "Assistant Space",
            Kind = "assistant",
            Visibility = "normal",
            Owner = "user-1",
            Description = "An assistant space",
            SettingsJson = "{\"theme\":\"dark\"}"
        });

        Assert.Equal("assistant-1", space.Id);
        Assert.Equal("assistant", space.Kind);
        Assert.Equal("normal", space.Visibility);
        Assert.Equal("user-1", space.Owner);
        Assert.Equal("{\"theme\":\"dark\"}", space.SettingsJson);

        var fetched = await _repo.GetByIdAsync("assistant-1");
        Assert.NotNull(fetched);
        Assert.Equal("assistant", fetched.Kind);
        Assert.Equal("normal", fetched.Visibility);
        Assert.Equal("user-1", fetched.Owner);
        Assert.Equal("{\"theme\":\"dark\"}", fetched.SettingsJson);
    }

    [Fact]
    public async Task GetAll_IncludesGlobalProject()
    {
        var all = await _repo.GetAllAsync();
        Assert.Contains(all, p => p.Id == "_global");
    }

    [Fact]
    public async Task GetAll_ReturnsAllKinds()
    {
        await _repo.CreateAsync(new Project { Id = "proj-1", Name = "Project 1" });
        await _repo.CreateAsync(new Project { Id = "personal-1", Name = "Personal", Kind = "personal" });
        await _repo.CreateAsync(new Project { Id = "kb-1", Name = "Knowledge Base", Kind = "knowledge_base" });

        var all = await _repo.GetAllAsync();
        Assert.Contains(all, p => p.Id == "proj-1" && p.Kind == "project");
        Assert.Contains(all, p => p.Id == "personal-1" && p.Kind == "personal");
        Assert.Contains(all, p => p.Id == "kb-1" && p.Kind == "knowledge_base");
    }

    [Fact]
    public async Task ListAsync_FiltersByKindAndVisibility()
    {
        await _repo.CreateAsync(new Project { Id = "proj-visible", Name = "Visible Project" });
        await _repo.CreateAsync(new Project { Id = "proj-hidden", Name = "Hidden Project", Visibility = "hidden" });
        await _repo.CreateAsync(new Project { Id = "assistant-1", Name = "Assistant", Kind = "assistant" });

        var projectsOnly = await _repo.ListAsync(kind: "project", includeHidden: false);
        Assert.Contains(projectsOnly, p => p.Id == "proj-visible");
        Assert.DoesNotContain(projectsOnly, p => p.Id == "proj-hidden");
        Assert.DoesNotContain(projectsOnly, p => p.Id == "assistant-1");

        var projectsWithHidden = await _repo.ListAsync(kind: "project", includeHidden: true);
        Assert.Contains(projectsWithHidden, p => p.Id == "proj-visible");
        Assert.Contains(projectsWithHidden, p => p.Id == "proj-hidden");

        var allVisible = await _repo.ListAsync(includeHidden: false);
        Assert.Contains(allVisible, p => p.Id == "proj-visible");
        Assert.DoesNotContain(allVisible, p => p.Id == "proj-hidden");
        Assert.Contains(allVisible, p => p.Id == "assistant-1");
    }

    [Fact]
    public async Task GetWithStats_ReturnsTaskCounts()
    {
        await _repo.CreateAsync(new Project { Id = "stats-test", Name = "Stats" });
        var taskRepo = new TaskRepository(_testDb.Db);
        await taskRepo.CreateAsync(new ProjectTask { ProjectId = "stats-test", Title = "T1" });
        await taskRepo.CreateAsync(new ProjectTask { ProjectId = "stats-test", Title = "T2" });

        var stats = await _repo.GetWithStatsAsync("stats-test");
        Assert.Equal(2, stats.TaskCountsByStatus[Models.TaskStatus.Planned]);
    }

    [Fact]
    public async Task GetWithStats_CountsUnreadMessages()
    {
        await _repo.CreateAsync(new Project { Id = "msg-test", Name = "Msg" });
        var msgRepo = new MessageRepository(_testDb.Db);
        await msgRepo.CreateAsync(new Message { ProjectId = "msg-test", Sender = "codex", Content = "Hello" });
        await msgRepo.CreateAsync(new Message { ProjectId = "msg-test", Sender = "claude-code", Content = "Hi" });

        var stats = await _repo.GetWithStatsAsync("msg-test", agent: "claude-code");
        Assert.Equal(1, stats.UnreadMessageCount); // only codex's message is unread
    }

    [Fact]
    public async Task UpdateProject_UpdatesRootPath()
    {
        await _repo.CreateAsync(new Project { Id = "update-test", Name = "Update Test" });
        var updated = await _repo.UpdateProjectAsync("update-test", new ProjectUpdateRequest
        {
            RootPath = "/home/dev/update-test",
        });
        Assert.Equal("/home/dev/update-test", updated.RootPath);
        Assert.Equal("Update Test", updated.Name); // unchanged
    }

    [Fact]
    public async Task UpdateProject_UpdatesMultipleFields()
    {
        await _repo.CreateAsync(new Project { Id = "multi-update", Name = "Original", Description = "Old desc" });
        var updated = await _repo.UpdateProjectAsync("multi-update", new ProjectUpdateRequest
        {
            Name = "New Name",
            Description = "New description",
            RootPath = "/new/path",
        });
        Assert.Equal("New Name", updated.Name);
        Assert.Equal("New description", updated.Description);
        Assert.Equal("/new/path", updated.RootPath);
    }

    [Fact]
    public async Task UpdateProject_EmptyRequestIsIdempotent()
    {
        var created = await _repo.CreateAsync(new Project { Id = "noop-test", Name = "No-Op" });
        var updated = await _repo.UpdateProjectAsync("noop-test", new ProjectUpdateRequest());
        Assert.Equal(created.Name, updated.Name);
        Assert.Equal(created.Description, updated.Description);
    }

    [Fact]
    public async Task UpdateProject_ClearRootPath()
    {
        await _repo.CreateAsync(new Project { Id = "clear-root", Name = "Clear Root", RootPath = "/old/path" });
        var updated = await _repo.UpdateProjectAsync("clear-root", new ProjectUpdateRequest
        {
            RootPath = "",
        });
        Assert.Equal("", updated.RootPath);
    }

    [Fact]
    public async Task UpdateProject_NotFoundThrows()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repo.UpdateProjectAsync("nonexistent", new ProjectUpdateRequest { RootPath = "/x" }));
    }
}
