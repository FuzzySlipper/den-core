using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class AgentStreamRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentStreamRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new AgentStreamRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
        await projects.CreateAsync(new Project { Id = "other", Name = "Other" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task AppendAndGet_RoundTripsEntry()
    {
        var created = await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "review_requested",
            ProjectId = "proj",
            Sender = "codex",
            SenderInstanceId = "codex-proj-1",
            RecipientAgent = "claude-code",
            RecipientRole = "reviewer",
            RecipientInstanceId = "claude-proj-1",
            DeliveryMode = AgentStreamDeliveryMode.Wake,
            Body = "Please review task 42.",
            Metadata = JsonSerializer.Deserialize<JsonElement>("""{"review_round_id":3}"""),
            DedupKey = "review:42:1"
        });

        Assert.True(created.Id > 0);
        Assert.Equal(AgentStreamKind.Ops, created.StreamKind);
        Assert.Equal(AgentStreamDeliveryMode.Wake, created.DeliveryMode);

        var fetched = await _repo.GetByIdAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("review_requested", fetched!.EventType);
        Assert.Equal("proj", fetched.ProjectId);
        Assert.Equal("claude-code", fetched.RecipientAgent);
        JsonElement roundId = default;
        var hasRoundId = false;
        if (fetched.Metadata is JsonElement metadata &&
            metadata.TryGetProperty("review_round_id", out var parsedRoundId))
        {
            roundId = parsedRoundId;
            hasRoundId = true;
        }

        Assert.True(hasRoundId);
        Assert.Equal(3, roundId.GetInt32());
    }

    [Fact]
    public async Task List_FiltersAndOrdersNewestFirst()
    {
        await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "question",
            ProjectId = "proj",
            Sender = "user",
            RecipientAgent = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Notify,
            Body = "Can you check that?"
        });

        var firstOps = await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "review_requested",
            ProjectId = "proj",
            Sender = "codex",
            RecipientRole = "reviewer",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        var secondOps = await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "review_requested",
            ProjectId = "other",
            Sender = "claude-code",
            RecipientRole = "reviewer",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        var filtered = await _repo.ListAsync(new AgentStreamListOptions
        {
            StreamKind = AgentStreamKind.Ops,
            RecipientRole = "reviewer",
            Limit = 10
        });

        Assert.Equal(2, filtered.Count);
        Assert.Equal(secondOps.Id, filtered[0].Id);
        Assert.Equal(firstOps.Id, filtered[1].Id);

        var scoped = await _repo.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "proj",
            StreamKind = AgentStreamKind.Ops,
            Sender = "codex"
        });

        var entry = Assert.Single(scoped);
        Assert.Equal(firstOps.Id, entry.Id);
    }

    [Fact]
    public async Task Append_WithDuplicateDedupKey_ReturnsExistingEntry()
    {
        var first = await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = "proj",
            Sender = "codex",
            RecipientAgent = "claude-code",
            DeliveryMode = AgentStreamDeliveryMode.Wake,
            Body = "first payload",
            DedupKey = "dup-key"
        });

        var second = await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = "proj",
            Sender = "codex",
            RecipientAgent = "claude-code",
            DeliveryMode = AgentStreamDeliveryMode.Wake,
            Body = "second payload",
            DedupKey = "dup-key"
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("first payload", second.Body);

        var entries = await _repo.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "proj",
            Sender = "codex",
            EventType = "wake_requested"
        });

        var entry = Assert.Single(entries);
        Assert.Equal(first.Id, entry.Id);
        Assert.Equal("dup-key", entry.DedupKey);
    }

    [Fact]
    public async Task List_DefaultExcludesDebugEntries()
    {
        // Summary event (no metadata) — should appear by default
        var summaryEntry = await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = "proj",
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly
        });

        // Debug event via event_visibility metadata — should be excluded by default
        await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_work_tool_start",
            ProjectId = "proj",
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = JsonSerializer.Deserialize<JsonElement>("""{"event_visibility":"debug"}""")
        });

        // Debug event via subagent_work_ prefix without explicit metadata — excluded by default
        await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_work_turn_end",
            ProjectId = "proj",
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly
        });

        // Event with summary visibility metadata — should appear by default
        var summaryMetaEntry = await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_completed",
            ProjectId = "proj",
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = JsonSerializer.Deserialize<JsonElement>("""{"event_visibility":"summary"}""")
        });

        // Default (IncludeDebug = false) — only non-debug entries
        var filtered = await _repo.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "proj",
            Limit = 50
        });

        Assert.Equal(2, filtered.Count);
        Assert.Equal(summaryMetaEntry.Id, filtered[0].Id); // newest first
        Assert.Equal(summaryEntry.Id, filtered[1].Id);
        Assert.All(filtered, e => Assert.DoesNotContain("subagent_work_", e.EventType));
    }

    [Fact]
    public async Task List_IncludeDebugTrueReturnsAllEntries()
    {
        // Summary event
        await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = "proj",
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly
        });

        // Debug event via subagent_work_ prefix
        await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_work_tool_start",
            ProjectId = "proj",
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly
        });

        // Debug event via metadata
        await _repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "custom_event",
            ProjectId = "proj",
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = JsonSerializer.Deserialize<JsonElement>("""{"event_visibility":"debug"}""")
        });

        // IncludeDebug = true — all entries
        var all = await _repo.ListAsync(new AgentStreamListOptions
        {
            ProjectId = "proj",
            IncludeDebug = true,
            Limit = 50
        });

        Assert.Equal(3, all.Count);
    }
}
