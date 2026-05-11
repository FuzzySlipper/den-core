using System.Net.Http.Json;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

public sealed class TopicClipQueueApiTests : IAsyncLifetime
{
    private TopicClipQueueAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new TopicClipQueueAppFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostClip_ValidTopic_CreatesClip()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"api-topic-{suffix}", DisplayName = "API Topic", Status = "active" });

        var request = new
        {
            source_agent = "hermes",
            topic_tags = new[] { $"api-topic-{suffix}" },
            raw_content = "API test content"
        };

        var response = await _client.PostAsJsonAsync("/api/topic-clips", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("clip_id").GetInt32() > 0);
    }

    [Fact]
    public async Task PostClip_UnknownTopic_Returns400()
    {
        var request = new
        {
            source_agent = "hermes",
            topic_tags = new[] { "unknown-topic-" + Guid.NewGuid().ToString("N")[..8] },
            raw_content = "Bad content"
        };

        var response = await _client.PostAsJsonAsync("/api/topic-clips", request);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetClips_ByStatus_ReturnsMatching()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var queueRepo = scope.ServiceProvider.GetRequiredService<ITopicClipQueueRepository>();
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"list-topic-{suffix}", DisplayName = "List Topic", Status = "active" });
        await queueRepo.AppendAsync("hermes", new List<string> { $"list-topic-{suffix}" }, "pending");

        var response = await _client.GetAsync($"/api/topic-clips?status=pending");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal("pending", i.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task ClaimBatch_ClaimsPendingItems()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var projectRepo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var queueRepo = scope.ServiceProvider.GetRequiredService<ITopicClipQueueRepository>();
        await projectRepo.CreateAsync(new Project { Id = $"claim-space-{suffix}", Name = "Claim Space" });
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"claim-topic-{suffix}", DisplayName = "Claim Topic", Status = "active" });
        await queueRepo.AppendAsync("hermes", new List<string> { $"claim-topic-{suffix}" }, "item 1", owningSpace: $"claim-space-{suffix}");
        await queueRepo.AppendAsync("hermes", new List<string> { $"claim-topic-{suffix}" }, "item 2", owningSpace: $"claim-space-{suffix}");

        var request = new { batch_size = 10, claim_ttl_minutes = 30, owning_space = $"claim-space-{suffix}" };
        var response = await _client.PostAsJsonAsync("/api/topic-clips/claim", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.NotNull(doc.RootElement.GetProperty("claim_key").GetString());
    }

    [Fact]
    public async Task Complete_MarksItemsProcessed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var queueRepo = scope.ServiceProvider.GetRequiredService<ITopicClipQueueRepository>();
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"complete-topic-{suffix}", DisplayName = "Complete Topic", Status = "active" });
        var append = await queueRepo.AppendAsync("hermes", new List<string> { $"complete-topic-{suffix}" }, "complete me");

        var request = new { clip_ids = new[] { append.ClipId!.Value }, decided_by = "curator" };
        var response = await _client.PostAsJsonAsync("/api/topic-clips/complete", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("updated_count").GetInt32());
    }

    [Fact]
    public async Task Discard_MarksItemsDiscarded()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var queueRepo = scope.ServiceProvider.GetRequiredService<ITopicClipQueueRepository>();
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"discard-topic-{suffix}", DisplayName = "Discard Topic", Status = "active" });
        var append = await queueRepo.AppendAsync("hermes", new List<string> { $"discard-topic-{suffix}" }, "discard me");

        var request = new { clip_ids = new[] { append.ClipId!.Value }, decided_by = "curator", reason = "Not relevant" };
        var response = await _client.PostAsJsonAsync("/api/topic-clips/discard", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("updated_count").GetInt32());
    }

    [Fact]
    public async Task Escalate_MarksItemsEscalated()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var queueRepo = scope.ServiceProvider.GetRequiredService<ITopicClipQueueRepository>();
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"escalate-topic-{suffix}", DisplayName = "Escalate Topic", Status = "active" });
        var append = await queueRepo.AppendAsync("hermes", new List<string> { $"escalate-topic-{suffix}" }, "escalate me");

        var request = new { clip_ids = new[] { append.ClipId!.Value }, decided_by = "curator" };
        var response = await _client.PostAsJsonAsync("/api/topic-clips/escalate", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("updated_count").GetInt32());
    }

    [Fact]
    public async Task GetDecisions_ReturnsAuditTrail()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var queueRepo = scope.ServiceProvider.GetRequiredService<ITopicClipQueueRepository>();
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"audit-topic-{suffix}", DisplayName = "Audit Topic", Status = "active" });
        var append = await queueRepo.AppendAsync("hermes", new List<string> { $"audit-topic-{suffix}" }, "audit me");
        await queueRepo.CompleteAsync(new List<int> { append.ClipId!.Value }, "curator");

        var response = await _client.GetAsync($"/api/topic-clips/decisions?clip_id={append.ClipId.Value}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var decisions = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(decisions);
        Assert.Equal("processed", decisions[0].GetProperty("decision").GetString());
        Assert.Equal("curator", decisions[0].GetProperty("decided_by").GetString());
    }

    [Fact]
    public async Task Cleanup_RedactsTerminalItems()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var queueRepo = scope.ServiceProvider.GetRequiredService<ITopicClipQueueRepository>();
        await topicRepo.CreateAsync(new ConsolidationTopic { Slug = $"cleanup-topic-{suffix}", DisplayName = "Cleanup Topic", Status = "active" });
        var append = await queueRepo.AppendAsync("hermes", new List<string> { $"cleanup-topic-{suffix}" }, "sensitive");
        await queueRepo.CompleteAsync(new List<int> { append.ClipId!.Value }, "curator");

        var request = new { cutoff = DateTime.UtcNow.AddMinutes(1) };
        var response = await _client.PostAsJsonAsync("/api/topic-clips/cleanup", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("redacted_count").GetInt32() >= 1);
    }

    private sealed class TopicClipQueueAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-topic-clip-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
