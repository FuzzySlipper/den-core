using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public class AgentRecipientResolverTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentInstanceBindingRepository _bindings = null!;
    private AgentStreamRepository _stream = null!;
    private AgentRecipientResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _bindings = new AgentInstanceBindingRepository(_testDb.Db);
        _stream = new AgentStreamRepository(_testDb.Db);
        _resolver = new AgentRecipientResolver(_bindings, _stream);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task ResolveAsync_PrefersRecipientInstanceId()
    {
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "codex-reviewer-1",
            ProjectId = "proj",
            AgentIdentity = "codex",
            AgentFamily = "codex",
            Role = "reviewer",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active
        });

        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "claude-reviewer-1",
            ProjectId = "proj",
            AgentIdentity = "claude-code",
            AgentFamily = "claude",
            Role = "reviewer",
            TransportKind = "manual_mcp",
            Status = AgentInstanceBindingStatus.Active
        });

        var resolution = await _resolver.ResolveAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = "proj",
            Sender = "den",
            RecipientInstanceId = "claude-reviewer-1",
            RecipientRole = "reviewer",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        Assert.Equal(AgentRecipientResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("claude-reviewer-1", resolution.Binding!.InstanceId);
        Assert.Null(resolution.RecordedAgentStreamEntryId);
    }

    [Fact]
    public async Task ResolveAsync_UsesProjectAgentWhenSingleMatch()
    {
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "codex-implementer-1",
            ProjectId = "proj",
            AgentIdentity = "codex",
            AgentFamily = "codex",
            Role = "implementer",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active
        });

        var resolution = await _resolver.ResolveAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "question",
            ProjectId = "proj",
            Sender = "user",
            RecipientAgent = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Notify
        });

        Assert.Equal(AgentRecipientResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("codex-implementer-1", resolution.Binding!.InstanceId);
    }

    [Fact]
    public async Task ResolveAsync_UsesGlobalAgentWhenSingleMatch()
    {
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "codex-global-1",
            ProjectId = "proj",
            AgentIdentity = "codex",
            AgentFamily = "codex",
            Role = "implementer",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active
        });

        var resolution = await _resolver.ResolveAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "nudge",
            Sender = "user",
            RecipientAgent = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        }, recordFailures: false);

        Assert.Equal(AgentRecipientResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("codex-global-1", resolution.Binding!.InstanceId);
    }

    [Fact]
    public async Task ResolveAsync_WhenRoleAmbiguous_RecordsWakeDropped()
    {
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "codex-reviewer-1",
            ProjectId = "proj",
            AgentIdentity = "codex",
            AgentFamily = "codex",
            Role = "reviewer",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active
        });
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "claude-reviewer-1",
            ProjectId = "proj",
            AgentIdentity = "claude-code",
            AgentFamily = "claude",
            Role = "reviewer",
            TransportKind = "manual_mcp",
            Status = AgentInstanceBindingStatus.Active
        });

        var source = await _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = "proj",
            Sender = "den",
            RecipientRole = "reviewer",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        var resolution = await _resolver.ResolveAsync(source);

        Assert.Equal(AgentRecipientResolutionStatus.Ambiguous, resolution.Status);
        Assert.Equal(2, resolution.CandidateInstanceIds!.Count);
        Assert.NotNull(resolution.RecordedAgentStreamEntryId);

        var streamEntries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "proj",
            EventType = "wake_dropped"
        });

        var wakeDropped = Assert.Single(streamEntries);
        Assert.Equal("den", wakeDropped.Sender);
        Assert.Equal("reviewer", wakeDropped.RecipientRole);
        Assert.Contains("Multiple active bindings", wakeDropped.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_WhenRecipientMissing_RecordsWakeDropped()
    {
        var source = await _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = "proj",
            Sender = "den",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        var resolution = await _resolver.ResolveAsync(source);

        Assert.Equal(AgentRecipientResolutionStatus.MissingRecipient, resolution.Status);
        Assert.NotNull(resolution.RecordedAgentStreamEntryId);

        var streamEntries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "proj",
            EventType = "wake_dropped"
        });

        var wakeDropped = Assert.Single(streamEntries);
        Assert.Contains("Recipient resolution requires", wakeDropped.Body, StringComparison.Ordinal);
    }
}
