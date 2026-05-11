using System.Net.Http.Json;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

public sealed class SpaceApiTests : IAsyncLifetime
{
    private SpaceAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SpaceAppFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── GET /api/projects defaults ──────────────────────────────────────

    [Fact]
    public async Task GetProjects_Defaults_ToProjectKindOnly()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-hidden-{suffix}", Name = "Hidden Project", Visibility = "hidden" });
        await repo.CreateAsync(new Project { Id = $"assistant-{suffix}", Name = "Assistant Space", Kind = "assistant" });
        await repo.CreateAsync(new Project { Id = $"personal-{suffix}", Name = "Personal Space", Kind = "personal" });
        await repo.CreateAsync(new Project { Id = $"kb-{suffix}", Name = "Knowledge Base", Kind = "knowledge_base" });
        await repo.CreateAsync(new Project { Id = $"system-{suffix}", Name = "System Space", Kind = "system" });

        var response = await _client.GetAsync("/api/projects");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.DoesNotContain($"proj-hidden-{suffix}", ids);
        Assert.DoesNotContain($"assistant-{suffix}", ids);
        Assert.DoesNotContain($"personal-{suffix}", ids);
        Assert.DoesNotContain($"kb-{suffix}", ids);
        Assert.DoesNotContain($"system-{suffix}", ids);
    }

    // ─── GET /api/spaces ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSpaces_Default_ExcludesHiddenAndArchived()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-hidden-{suffix}", Name = "Hidden Project", Visibility = "hidden" });
        await repo.CreateAsync(new Project { Id = $"assistant-{suffix}", Name = "Assistant Space", Kind = "assistant" });

        var response = await _client.GetAsync("/api/spaces");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"assistant-{suffix}", ids);
        Assert.DoesNotContain($"proj-hidden-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_WithKindFilter()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"assistant-{suffix}", Name = "Assistant Space", Kind = "assistant" });

        var response = await _client.GetAsync($"/api/spaces?kind=assistant");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"assistant-{suffix}", ids);
        Assert.DoesNotContain($"proj-visible-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_IncludeHidden()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-hidden-{suffix}", Name = "Hidden Project", Visibility = "hidden" });

        var response = await _client.GetAsync("/api/spaces?includeHidden=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"proj-hidden-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_IncludeArchived()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-archived-{suffix}", Name = "Archived Project", Visibility = "archived" });

        var response = await _client.GetAsync("/api/spaces?includeArchived=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"proj-archived-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_Default_IncludesAllVisibleKinds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"system-{suffix}", Name = "System Space", Kind = "system" });
        await repo.CreateAsync(new Project { Id = $"personal-{suffix}", Name = "Personal Space", Kind = "personal" });

        var response = await _client.GetAsync("/api/spaces");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"system-{suffix}", ids);
        Assert.Contains($"personal-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_ExcludesHiddenSpacesByDefault()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"system-hidden-{suffix}", Name = "Hidden System Space", Kind = "system", Visibility = "hidden" });
        await repo.CreateAsync(new Project { Id = $"personal-hidden-{suffix}", Name = "Hidden Personal Space", Kind = "personal", Visibility = "hidden" });

        var response = await _client.GetAsync("/api/spaces");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.DoesNotContain($"system-hidden-{suffix}", ids);
        Assert.DoesNotContain($"personal-hidden-{suffix}", ids);
    }

    // ─── POST /api/spaces ────────────────────────────────────────────────

    [Fact]
    public async Task PostSpace_CreatesNonProjectSpace()
    {
        var id = $"new-assistant-{Guid.NewGuid():N}";
        var request = new { id, name = "New Assistant", kind = "assistant", visibility = "normal" };
        var response = await _client.PostAsJsonAsync("/api/spaces", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("assistant", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("normal", doc.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task PostSpace_DefaultsToProjectKind()
    {
        var id = $"new-project-{Guid.NewGuid():N}";
        var request = new { id, name = "New Project" };
        var response = await _client.PostAsJsonAsync("/api/spaces", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("project", doc.RootElement.GetProperty("kind").GetString());
    }

    // ─── GET /api/spaces/{id} ────────────────────────────────────────────

    [Fact]
    public async Task GetSpace_ReturnsStats()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"space-stats-{suffix}", Name = "Space Stats" });

        var response = await _client.GetAsync($"/api/spaces/space-stats-{suffix}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal($"space-stats-{suffix}", doc.RootElement.GetProperty("project").GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetSpace_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/spaces/nonexistent");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class SpaceAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-space-{Guid.NewGuid()}.db");

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
