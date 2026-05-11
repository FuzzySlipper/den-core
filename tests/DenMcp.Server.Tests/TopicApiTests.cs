using System.Net.Http.Json;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

public sealed class TopicApiTests : IAsyncLifetime
{
    private TopicAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new TopicAppFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── GET /api/topics ────────────────────────────────────────────────

    [Fact]
    public async Task GetTopics_Default_ReturnsOnlyActive()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"active-{suffix}", DisplayName = "Active", Status = "active" });
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"inactive-{suffix}", DisplayName = "Inactive", Status = "inactive" });

        var response = await _client.GetAsync("/api/topics");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var slugs = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ToHashSet();

        Assert.Contains($"active-{suffix}", slugs);
        Assert.DoesNotContain($"inactive-{suffix}", slugs);
    }

    [Fact]
    public async Task GetTopics_IncludeInactive_ReturnsAll()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"active-{suffix}", DisplayName = "Active", Status = "active" });
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"inactive-{suffix}", DisplayName = "Inactive", Status = "inactive" });

        var response = await _client.GetAsync("/api/topics?include_inactive=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var slugs = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ToHashSet();

        Assert.Contains($"active-{suffix}", slugs);
        Assert.Contains($"inactive-{suffix}", slugs);
    }

    [Fact]
    public async Task GetTopics_FiltersByOwningSpace()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var projectRepo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        await projectRepo.CreateAsync(new Project { Id = $"space-{suffix}", Name = "Space" });
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"topic-a-{suffix}", DisplayName = "Topic A", Status = "active", OwningSpace = $"space-{suffix}" });
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"topic-b-{suffix}", DisplayName = "Topic B", Status = "active" });

        var response = await _client.GetAsync($"/api/topics?owning_space=space-{suffix}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var slugs = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ToHashSet();

        Assert.Contains($"topic-a-{suffix}", slugs);
        Assert.DoesNotContain($"topic-b-{suffix}", slugs);
    }

    // ─── GET /api/topics/{id} ───────────────────────────────────────────

    [Fact]
    public async Task GetTopicById_ReturnsTopic()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var topic = await repo.CreateAsync(new ConsolidationTopic { Slug = $"by-id-{suffix}", DisplayName = "By ID", Status = "active" });

        var response = await _client.GetAsync($"/api/topics/{topic.Id}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal($"by-id-{suffix}", doc.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task GetTopicById_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/topics/99999");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── GET /api/topics/by-slug/{slug} ─────────────────────────────────

    [Fact]
    public async Task GetTopicBySlug_ReturnsTopic()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"by-slug-{suffix}", DisplayName = "By Slug", Status = "active" });

        var response = await _client.GetAsync($"/api/topics/by-slug/by-slug-{suffix}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("By Slug", doc.RootElement.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task GetTopicBySlug_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/topics/by-slug/nonexistent");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── POST /api/topics ───────────────────────────────────────────────

    [Fact]
    public async Task PostTopic_CreatesTopic()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var request = new { slug = $"new-topic-{suffix}", display_name = "New Topic", status = "active" };
        var response = await _client.PostAsJsonAsync("/api/topics", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal($"new-topic-{suffix}", doc.RootElement.GetProperty("slug").GetString());
        Assert.Equal("active", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostTopic_DuplicateSlug_Returns409()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"dup-{suffix}", DisplayName = "Original", Status = "active" });

        var request = new { slug = $"dup-{suffix}", display_name = "Duplicate", status = "active" };
        var response = await _client.PostAsJsonAsync("/api/topics", request);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("dup-", body);
    }

    // ─── PUT /api/topics/{id} ───────────────────────────────────────────

    [Fact]
    public async Task PutTopic_UpdatesTopic()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var topic = await repo.CreateAsync(new ConsolidationTopic { Slug = $"to-update-{suffix}", DisplayName = "To Update", Status = "active" });

        var request = new { slug = $"to-update-{suffix}", display_name = "Updated Name", status = "inactive" };
        var response = await _client.PutAsJsonAsync($"/api/topics/{topic.Id}", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Updated Name", doc.RootElement.GetProperty("display_name").GetString());
        Assert.Equal("inactive", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PutTopic_NotFound_Returns404()
    {
        var request = new { slug = "missing", display_name = "Missing", status = "active" };
        var response = await _client.PutAsJsonAsync("/api/topics/99999", request);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PutTopic_DuplicateSlug_Returns409()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var topicA = await repo.CreateAsync(new ConsolidationTopic { Slug = $"put-dup-a-{suffix}", DisplayName = "A", Status = "active" });
        var topicB = await repo.CreateAsync(new ConsolidationTopic { Slug = $"put-dup-b-{suffix}", DisplayName = "B", Status = "active" });

        var request = new { slug = $"put-dup-a-{suffix}", display_name = "B Renamed", status = "active" };
        var response = await _client.PutAsJsonAsync($"/api/topics/{topicB.Id}", request);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("put-dup-a-", body);
    }

    // ─── DELETE /api/topics/{id} ────────────────────────────────────────

    [Fact]
    public async Task DeleteTopic_RemovesTopic()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        var topic = await repo.CreateAsync(new ConsolidationTopic { Slug = $"to-delete-{suffix}", DisplayName = "To Delete", Status = "active" });

        var response = await _client.DeleteAsync($"/api/topics/{topic.Id}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteTopic_NotFound_Returns404()
    {
        var response = await _client.DeleteAsync("/api/topics/99999");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── POST /api/topics/validate ──────────────────────────────────────

    [Fact]
    public async Task ValidateTopics_ReturnsResults()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();
        await repo.CreateAsync(new ConsolidationTopic { Slug = $"valid-{suffix}", DisplayName = "Valid", Status = "active" });

        var request = new { tags = new[] { $"valid-{suffix}", "unknown" } };
        var response = await _client.PostAsJsonAsync("/api/topics/validate", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, results.Count);

        var valid = results.Single(r => r.GetProperty("input").GetString() == $"valid-{suffix}");
        Assert.True(valid.GetProperty("valid").GetBoolean());

        var invalid = results.Single(r => r.GetProperty("input").GetString() == "unknown");
        Assert.False(invalid.GetProperty("valid").GetBoolean());
    }

    private sealed class TopicAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-topic-{Guid.NewGuid()}.db");

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
