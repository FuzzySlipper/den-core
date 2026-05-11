using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public sealed class PiSessionRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private PiSessionRepository _sessions = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _sessions = new PiSessionRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "den-mcp", Name = "Den MCP" });
        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "den-mcp",
            Title = "Pi session",
        });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpdateState_PreservesAttentionForActiveStatesAndClearsItForTerminalStates()
    {
        var created = await _sessions.CreateAsync(new PiSessionRecord
        {
            SessionId = "session-attention",
            ProjectId = "den-mcp",
            TaskId = _task.Id,
            HostId = "host-test",
            TmuxSessionName = "tmux-session-attention",
            State = PiSessionStates.Running,
            LaunchProfileKind = "test",
            LaunchProfileJson = "{}",
            LaunchCommandJson = "[]",
            LaunchCommandDisplay = "test",
            AttentionState = PiSessionAttentionStates.UserInputNeeded,
            AttentionReason = "prompted",
            AttentionSinceAt = DateTime.UtcNow.AddMinutes(-5),
            AttentionUpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            NeedsUserInput = true,
        });

        var terminating = await _sessions.UpdateStateAsync(
            created.ProjectId,
            created.SessionId,
            PiSessionStates.Terminating,
            stateReason: "operator requested stop");

        Assert.Equal(PiSessionAttentionStates.UserInputNeeded, terminating.AttentionState);
        Assert.Equal("prompted", terminating.AttentionReason);
        Assert.NotNull(terminating.AttentionSinceAt);
        Assert.NotNull(terminating.AttentionUpdatedAt);
        Assert.True(terminating.NeedsUserInput);

        var completed = await _sessions.UpdateStateAsync(
            created.ProjectId,
            created.SessionId,
            PiSessionStates.Completed,
            stateReason: "finished");

        Assert.Null(completed.AttentionState);
        Assert.Null(completed.AttentionReason);
        Assert.Null(completed.AttentionSinceAt);
        Assert.Null(completed.AttentionUpdatedAt);
        Assert.False(completed.NeedsUserInput);
    }
}
