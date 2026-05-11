using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class TopicClipQueueRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TopicRepository _topicRepo = null!;
    private TopicClipQueueRepository _queueRepo = null!;
    private ProjectRepository _projectRepo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _topicRepo = new TopicRepository(_testDb.Db);
        _queueRepo = new TopicClipQueueRepository(_testDb.Db, _topicRepo);
        _projectRepo = new ProjectRepository(_testDb.Db);
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task AppendAsync_ValidTopic_CreatesItem()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic
        {
            Slug = "architecture",
            DisplayName = "Architecture",
            Status = "active"
        });

        var result = await _queueRepo.AppendAsync(
            sourceAgent: "hermes",
            topicTags: new List<string> { "architecture" },
            rawContent: "Some content");

        Assert.True(result.Success);
        Assert.NotNull(result.ClipId);
        Assert.Single(result.CanonicalTopicSlugs!);
        Assert.Equal("architecture", result.CanonicalTopicSlugs![0]);
    }

    [Fact]
    public async Task AppendAsync_AliasResolvesToCanonicalSlug()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic
        {
            Slug = "performance",
            DisplayName = "Performance",
            Aliases = new List<string> { "perf" },
            Status = "active"
        });

        var result = await _queueRepo.AppendAsync(
            sourceAgent: "hermes",
            topicTags: new List<string> { "perf" },
            rawContent: "Perf content");

        Assert.True(result.Success);
        Assert.Equal("performance", result.CanonicalTopicSlugs![0]);
    }

    [Fact]
    public async Task AppendAsync_UnknownTopic_ReturnsError()
    {
        var result = await _queueRepo.AppendAsync(
            sourceAgent: "hermes",
            topicTags: new List<string> { "nonexistent" },
            rawContent: "Some content");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("nonexistent", result.Error);
    }

    [Fact]
    public async Task AppendAsync_InactiveTopic_RejectedByDefault()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic
        {
            Slug = "old-topic",
            DisplayName = "Old Topic",
            Status = "inactive"
        });

        var result = await _queueRepo.AppendAsync(
            sourceAgent: "hermes",
            topicTags: new List<string> { "old-topic" },
            rawContent: "Some content");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AppendAsync_InactiveTopic_AllowedWithFlag()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic
        {
            Slug = "old-topic",
            DisplayName = "Old Topic",
            Status = "inactive"
        });

        var result = await _queueRepo.AppendAsync(
            sourceAgent: "hermes",
            topicTags: new List<string> { "old-topic" },
            rawContent: "Some content",
            allowInactive: true);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task AppendAsync_MultipleTopics_StoresAllCanonicalSlugs()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "topic-a", DisplayName = "A", Status = "active" });
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "topic-b", DisplayName = "B", Status = "active" });

        var result = await _queueRepo.AppendAsync(
            sourceAgent: "hermes",
            topicTags: new List<string> { "topic-a", "topic-b" },
            rawContent: "Multi-topic content");

        Assert.True(result.Success);
        Assert.Equal(2, result.CanonicalTopicSlugs!.Count);
    }

    [Fact]
    public async Task ListAsync_ByStatus_ReturnsMatchingItems()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });

        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "pending content");
        var claimed = await _queueRepo.ClaimBatchAsync(batchSize: 10, claimTtl: TimeSpan.FromMinutes(30));
        Assert.NotNull(claimed);

        var pendingItems = await _queueRepo.ListAsync(status: "pending");
        Assert.Empty(pendingItems);

        var claimedItems = await _queueRepo.ListAsync(status: "claimed");
        Assert.Single(claimedItems);
    }

    [Fact]
    public async Task ListAsync_ByOwningSpace_ReturnsMatchingItems()
    {
        await _projectRepo.CreateAsync(new Project { Id = "space-a", Name = "Space A" });
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });

        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "space content", owningSpace: "space-a");
        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "global content");

        var spaceItems = await _queueRepo.ListAsync(owningSpace: "space-a");
        Assert.Single(spaceItems);
        Assert.Equal("space-a", spaceItems[0].OwningSpace);
    }

    [Fact]
    public async Task ClaimBatchAsync_ClaimsPendingItems()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content 1");
        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content 2");

        var result = await _queueRepo.ClaimBatchAsync(batchSize: 10, claimTtl: TimeSpan.FromMinutes(30));

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.NotNull(result.ClaimKey);
        Assert.All(result.Items, item => Assert.Equal("claimed", item.Status));
    }

    [Fact]
    public async Task ClaimBatchAsync_RespectsBatchSize()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content 1");
        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content 2");
        await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content 3");

        var result = await _queueRepo.ClaimBatchAsync(batchSize: 2, claimTtl: TimeSpan.FromMinutes(30));

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ClaimBatchAsync_NoPendingItems_ReturnsNull()
    {
        var result = await _queueRepo.ClaimBatchAsync(batchSize: 10, claimTtl: TimeSpan.FromMinutes(30));
        Assert.Null(result);
    }

    [Fact]
    public async Task CompleteAsync_MarksItemsProcessed()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var append = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content");

        var result = await _queueRepo.CompleteAsync(
            clipIds: new List<int> { append.ClipId!.Value },
            decidedBy: "curator");

        Assert.Single(result.UpdatedIds);
        Assert.Equal(1, result.UpdatedCount);

        var items = await _queueRepo.ListAsync(status: "processed");
        Assert.Single(items);
        Assert.Equal("curator", (await _queueRepo.ListDecisionsAsync(clipId: append.ClipId)).Single().DecidedBy);
    }

    [Fact]
    public async Task DiscardAsync_MarksItemsDiscarded()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var append = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content");

        var result = await _queueRepo.DiscardAsync(
            clipIds: new List<int> { append.ClipId!.Value },
            decidedBy: "curator",
            reason: "Not relevant");

        Assert.Single(result.UpdatedIds);

        var decisions = await _queueRepo.ListDecisionsAsync(clipId: append.ClipId);
        Assert.Single(decisions);
        Assert.Equal("discarded", decisions[0].Decision);
        Assert.Equal("Not relevant", decisions[0].Reason);
    }

    [Fact]
    public async Task EscalateAsync_MarksItemsEscalated()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var append = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content");

        var result = await _queueRepo.EscalateAsync(
            clipIds: new List<int> { append.ClipId!.Value },
            decidedBy: "curator");

        Assert.Single(result.UpdatedIds);

        var items = await _queueRepo.ListAsync(status: "escalated");
        Assert.Single(items);
    }

    [Fact]
    public async Task UpdateStatusAsync_SkipsAlreadyTerminalItems()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var append = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content");
        await _queueRepo.CompleteAsync(new List<int> { append.ClipId!.Value }, "curator");

        var result = await _queueRepo.DiscardAsync(
            clipIds: new List<int> { append.ClipId.Value },
            decidedBy: "curator");

        Assert.Empty(result.UpdatedIds);
        Assert.NotNull(result.SkippedIds);
        Assert.Single(result.SkippedIds);
    }

    [Fact]
    public async Task UpdateStatusAsync_TracksNotFoundItems()
    {
        var result = await _queueRepo.CompleteAsync(
            clipIds: new List<int> { 99999 },
            decidedBy: "curator");

        Assert.Empty(result.UpdatedIds);
        Assert.NotNull(result.NotFoundIds);
        Assert.Single(result.NotFoundIds);
    }

    [Fact]
    public async Task CleanupRawContentAsync_RedactsTerminalItems()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var append = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "sensitive content");
        await _queueRepo.CompleteAsync(new List<int> { append.ClipId!.Value }, "curator");

        var result = await _queueRepo.CleanupRawContentAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(1, result.RedactedCount);

        var items = await _queueRepo.ListAsync(status: "processed");
        Assert.Equal("[REDACTED]", items[0].RawContent);
    }

    [Fact]
    public async Task CleanupRawContentAsync_SkipsRecentItems()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var append = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "sensitive content");
        await _queueRepo.CompleteAsync(new List<int> { append.ClipId!.Value }, "curator");

        var result = await _queueRepo.CleanupRawContentAsync(DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(0, result.RedactedCount);
    }

    [Fact]
    public async Task Persistence_AcrossNewRepositoryInstance()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var append = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "persistent content");

        // Create a fresh repository instance against the same database
        var freshRepo = new TopicClipQueueRepository(_testDb.Db, new TopicRepository(_testDb.Db));
        var items = await freshRepo.ListAsync();

        Assert.Single(items);
        Assert.Equal("persistent content", items[0].RawContent);
        Assert.Equal("hermes", items[0].SourceAgent);
    }

    [Fact]
    public async Task ListDecisionsAsync_ReturnsDecisionsOrderedByDate()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        var a1 = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content 1");
        var a2 = await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, "content 2");

        await _queueRepo.CompleteAsync(new List<int> { a1.ClipId!.Value }, "curator-a");
        await _queueRepo.DiscardAsync(new List<int> { a2.ClipId!.Value }, "curator-b");

        var decisions = await _queueRepo.ListDecisionsAsync();
        Assert.Equal(2, decisions.Count);
        Assert.Equal("discarded", decisions[0].Decision); // most recent first
        Assert.Equal("processed", decisions[1].Decision);
    }

    [Fact]
    public async Task ListAsync_WithLimit_ReturnsAtMostLimit()
    {
        await _topicRepo.CreateAsync(new ConsolidationTopic { Slug = "t1", DisplayName = "T1", Status = "active" });
        for (var i = 0; i < 5; i++)
            await _queueRepo.AppendAsync("hermes", new List<string> { "t1" }, $"content {i}");

        var items = await _queueRepo.ListAsync(limit: 3);
        Assert.Equal(3, items.Count);
    }
}
