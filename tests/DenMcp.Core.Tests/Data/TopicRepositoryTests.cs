using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class TopicRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TopicRepository _repo = null!;
    private ProjectRepository _projectRepo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new TopicRepository(_testDb.Db);
        _projectRepo = new ProjectRepository(_testDb.Db);
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateAndGet_RoundTrips()
    {
        var topic = await _repo.CreateAsync(new ConsolidationTopic
        {
            Slug = "architecture",
            DisplayName = "Architecture",
            Description = "System architecture decisions",
            Aliases = new List<string> { "arch", "system-design" },
            Status = "active"
        });

        Assert.Equal("architecture", topic.Slug);
        Assert.Equal("Architecture", topic.DisplayName);
        Assert.Equal("active", topic.Status);
        Assert.NotNull(topic.Aliases);
        Assert.Contains("arch", topic.Aliases);

        var fetched = await _repo.GetByIdAsync(topic.Id);
        Assert.NotNull(fetched);
        Assert.Equal("architecture", fetched.Slug);
        Assert.Equal("Architecture", fetched.DisplayName);
        Assert.Equal("System architecture decisions", fetched.Description);
    }

    [Fact]
    public async Task GetBySlug_ReturnsTopic()
    {
        await _repo.CreateAsync(new ConsolidationTopic
        {
            Slug = "testing",
            DisplayName = "Testing",
            Status = "active"
        });

        var fetched = await _repo.GetBySlugAsync("testing");
        Assert.NotNull(fetched);
        Assert.Equal("Testing", fetched.DisplayName);
    }

    [Fact]
    public async Task ListActive_ExcludesInactive()
    {
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "active-topic", DisplayName = "Active", Status = "active" });
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "inactive-topic", DisplayName = "Inactive", Status = "inactive" });

        var active = await _repo.ListActiveAsync();
        Assert.Contains(active, t => t.Slug == "active-topic");
        Assert.DoesNotContain(active, t => t.Slug == "inactive-topic");
    }

    [Fact]
    public async Task ListAsync_WithIncludeInactive_ReturnsAll()
    {
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "active-topic", DisplayName = "Active", Status = "active" });
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "inactive-topic", DisplayName = "Inactive", Status = "inactive" });

        var all = await _repo.ListAsync(includeInactive: true);
        Assert.Contains(all, t => t.Slug == "active-topic");
        Assert.Contains(all, t => t.Slug == "inactive-topic");
    }

    [Fact]
    public async Task ListAsync_FiltersByOwningSpace()
    {
        await _projectRepo.CreateAsync(new Project { Id = "space-a", Name = "Space A" });
        await _projectRepo.CreateAsync(new Project { Id = "space-b", Name = "Space B" });

        await _repo.CreateAsync(new ConsolidationTopic { Slug = "topic-a", DisplayName = "Topic A", Status = "active", OwningSpace = "space-a" });
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "topic-b", DisplayName = "Topic B", Status = "active", OwningSpace = "space-b" });
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "topic-global", DisplayName = "Topic Global", Status = "active" });

        var spaceA = await _repo.ListAsync(owningSpace: "space-a");
        Assert.Contains(spaceA, t => t.Slug == "topic-a");
        Assert.DoesNotContain(spaceA, t => t.Slug == "topic-b");
        Assert.DoesNotContain(spaceA, t => t.Slug == "topic-global");
    }

    [Fact]
    public async Task Update_ModifiesTopic()
    {
        var topic = await _repo.CreateAsync(new ConsolidationTopic
        {
            Slug = "api-design",
            DisplayName = "API Design",
            Status = "active"
        });

        var updated = await _repo.UpdateAsync(topic.Id, new ConsolidationTopic
        {
            Slug = "api-design",
            DisplayName = "API Design Patterns",
            Description = "REST and GraphQL patterns",
            Status = "inactive"
        });

        Assert.Equal("API Design Patterns", updated.DisplayName);
        Assert.Equal("inactive", updated.Status);

        var fetched = await _repo.GetByIdAsync(topic.Id);
        Assert.Equal("API Design Patterns", fetched!.DisplayName);
    }

    [Fact]
    public async Task Update_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            await _repo.UpdateAsync(99999, new ConsolidationTopic { Slug = "missing", DisplayName = "Missing" });
        });
    }

    [Fact]
    public async Task Delete_RemovesTopic()
    {
        var topic = await _repo.CreateAsync(new ConsolidationTopic { Slug = "to-delete", DisplayName = "To Delete", Status = "active" });
        var deleted = await _repo.DeleteAsync(topic.Id);
        Assert.True(deleted);

        var fetched = await _repo.GetByIdAsync(topic.Id);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task Delete_ReturnsFalseWhenNotFound()
    {
        var deleted = await _repo.DeleteAsync(99999);
        Assert.False(deleted);
    }

    [Fact]
    public async Task CreateAsync_DuplicateSlug_ThrowsInvalidOperationException()
    {
        await _repo.CreateAsync(new ConsolidationTopic
        {
            Slug = "duplicate-slug",
            DisplayName = "Original",
            Status = "active"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _repo.CreateAsync(new ConsolidationTopic
            {
                Slug = "duplicate-slug",
                DisplayName = "Duplicate",
                Status = "active"
            });
        });

        Assert.Contains("duplicate-slug", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_DuplicateSlug_ThrowsInvalidOperationException()
    {
        var topicA = await _repo.CreateAsync(new ConsolidationTopic
        {
            Slug = "topic-a",
            DisplayName = "Topic A",
            Status = "active"
        });

        var topicB = await _repo.CreateAsync(new ConsolidationTopic
        {
            Slug = "topic-b",
            DisplayName = "Topic B",
            Status = "active"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _repo.UpdateAsync(topicB.Id, new ConsolidationTopic
            {
                Slug = "topic-a",
                DisplayName = "Topic B Renamed",
                Status = "active"
            });
        });

        Assert.Contains("topic-a", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_KnownActiveTopic_ReturnsValid()
    {
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "performance", DisplayName = "Performance", Status = "active" });

        var result = await _repo.ValidateAsync("performance");
        Assert.True(result.Valid);
        Assert.Equal("performance", result.CanonicalSlug);
    }

    [Fact]
    public async Task Validate_KnownAlias_ReturnsValidWithCanonicalSlug()
    {
        await _repo.CreateAsync(new ConsolidationTopic
        {
            Slug = "performance",
            DisplayName = "Performance",
            Aliases = new List<string> { "perf", "speed" },
            Status = "active"
        });

        var result = await _repo.ValidateAsync("perf");
        Assert.True(result.Valid);
        Assert.Equal("performance", result.CanonicalSlug);
    }

    [Fact]
    public async Task Validate_InactiveTopic_ReturnsInvalidByDefault()
    {
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "old-topic", DisplayName = "Old Topic", Status = "inactive" });

        var result = await _repo.ValidateAsync("old-topic");
        Assert.False(result.Valid);
        Assert.Equal("old-topic", result.CanonicalSlug);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Validate_InactiveTopic_AllowedWhenFlagSet()
    {
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "old-topic", DisplayName = "Old Topic", Status = "inactive" });

        var result = await _repo.ValidateAsync("old-topic", allowInactive: true);
        Assert.True(result.Valid);
        Assert.Equal("old-topic", result.CanonicalSlug);
    }

    [Fact]
    public async Task Validate_UnknownTag_ReturnsInvalid()
    {
        var result = await _repo.ValidateAsync("nonexistent");
        Assert.False(result.Valid);
        Assert.Null(result.CanonicalSlug);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task ValidateMany_ReturnsResultsForAllInputs()
    {
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "topic-a", DisplayName = "Topic A", Status = "active" });
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "topic-b", DisplayName = "Topic B", Status = "active" });

        var results = await _repo.ValidateManyAsync(new[] { "topic-a", "topic-b", "unknown" });
        Assert.Equal(3, results.Count);
        Assert.True(results.Single(r => r.Input == "topic-a").Valid);
        Assert.True(results.Single(r => r.Input == "topic-b").Valid);
        Assert.False(results.Single(r => r.Input == "unknown").Valid);
    }

    [Fact]
    public async Task ValidateMany_DeduplicatesInputs()
    {
        await _repo.CreateAsync(new ConsolidationTopic { Slug = "topic-a", DisplayName = "Topic A", Status = "active" });

        var results = await _repo.ValidateManyAsync(new[] { "topic-a", "topic-a", "topic-a" });
        Assert.Single(results);
    }
}
