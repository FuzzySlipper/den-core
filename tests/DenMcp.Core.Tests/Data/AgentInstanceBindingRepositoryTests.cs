using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class AgentInstanceBindingRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentInstanceBindingRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new AgentInstanceBindingRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpsertAndList_RoundTripsBinding()
    {
        var created = await _repo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "codex-proj-reviewer-1",
            ProjectId = "proj",
            AgentIdentity = "codex",
            AgentFamily = "codex",
            Role = "reviewer",
            TransportKind = "local_adapter",
            SessionId = "session-1",
            Status = AgentInstanceBindingStatus.Active,
            Metadata = """{"thread":"abc"}"""
        });

        Assert.Equal("codex-proj-reviewer-1", created.InstanceId);

        var active = await _repo.GetActiveByInstanceIdAsync("codex-proj-reviewer-1");
        Assert.NotNull(active);
        Assert.Equal("reviewer", active!.Role);
        Assert.Equal("local_adapter", active.TransportKind);

        var listed = await _repo.ListAsync(new AgentInstanceBindingListOptions
        {
            ProjectId = "proj",
            Role = "reviewer",
            Statuses =
            [
                AgentInstanceBindingStatus.Active
            ]
        });

        var binding = Assert.Single(listed);
        Assert.Equal("codex", binding.AgentIdentity);
    }

    [Fact]
    public async Task CheckOutBySession_MarksBindingInactive()
    {
        await _repo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "claude-proj-reviewer-1",
            ProjectId = "proj",
            AgentIdentity = "claude-code",
            AgentFamily = "claude",
            Role = "reviewer",
            TransportKind = "manual_mcp",
            SessionId = "session-2",
            Status = AgentInstanceBindingStatus.Active
        });

        var updated = await _repo.CheckOutBySessionAsync("session-2");
        Assert.Equal(1, updated);

        var active = await _repo.GetActiveByInstanceIdAsync("claude-proj-reviewer-1");
        Assert.Null(active);
    }
}
