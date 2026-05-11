using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public class AgentStreamMessageServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentInstanceBindingRepository _bindings = null!;
    private AgentStreamRepository _stream = null!;
    private AgentStreamMessageService _service = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _bindings = new AgentInstanceBindingRepository(_testDb.Db);
        _stream = new AgentStreamRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });

        var resolver = new AgentRecipientResolver(_bindings, _stream);
        _service = new AgentStreamMessageService(_stream, resolver);
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateAsync_TargetedQuestionWithWake_ResolvesRecipient()
    {
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "codex-impl-1",
            ProjectId = "proj",
            AgentIdentity = "codex",
            AgentFamily = "codex",
            Role = "implementer",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active
        });

        var result = await _service.CreateAsync(new AgentStreamMessageCreateRequest
        {
            ProjectId = "proj",
            Sender = "user",
            EventType = "question",
            RecipientAgent = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Wake,
            Body = "Should we merge this now?",
            Metadata = JsonSerializer.Deserialize<JsonElement>("""{"source":"operator"}""")
        });

        Assert.True(result.Entry.Id > 0);
        Assert.Equal(AgentStreamKind.Message, result.Entry.StreamKind);
        Assert.Equal("question", result.Entry.EventType);
        Assert.Equal(AgentStreamDeliveryMode.Wake, result.Entry.DeliveryMode);
        Assert.NotNull(result.WakeResolution);
        Assert.Equal(AgentRecipientResolutionStatus.Resolved, result.WakeResolution!.Status);
        Assert.Equal("codex-impl-1", result.WakeResolution.Binding!.InstanceId);
    }

    [Fact]
    public async Task CreateAsync_TargetedAnswerWithWake_UsesExplicitInstance()
    {
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "codex-reviewer-2",
            ProjectId = "proj",
            AgentIdentity = "codex",
            AgentFamily = "codex",
            Role = "reviewer",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active
        });

        var result = await _service.CreateAsync(new AgentStreamMessageCreateRequest
        {
            Sender = "user",
            EventType = "answer",
            RecipientInstanceId = "codex-reviewer-2",
            DeliveryMode = AgentStreamDeliveryMode.Wake,
            Body = "Yes, go ahead."
        });

        Assert.Equal("answer", result.Entry.EventType);
        Assert.Equal("codex-reviewer-2", result.Entry.RecipientInstanceId);
        Assert.NotNull(result.WakeResolution);
        Assert.Equal(AgentRecipientResolutionStatus.Resolved, result.WakeResolution!.Status);
        Assert.Equal("codex-reviewer-2", result.WakeResolution.Binding!.InstanceId);
    }

    [Fact]
    public async Task CreateAsync_RecordOnlyNote_DoesNotRecordWakeDropForAmbiguousRecipient()
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

        var result = await _service.CreateAsync(new AgentStreamMessageCreateRequest
        {
            ProjectId = "proj",
            Sender = "codex",
            EventType = "note",
            RecipientRole = "reviewer",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "Heads up: I rebased the branch."
        });

        Assert.Equal(AgentStreamDeliveryMode.RecordOnly, result.Entry.DeliveryMode);
        Assert.Null(result.WakeResolution);

        var wakeDrops = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "proj",
            EventType = "wake_dropped"
        });

        Assert.Empty(wakeDrops);
    }
}
