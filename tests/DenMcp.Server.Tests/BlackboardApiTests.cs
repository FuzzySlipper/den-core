using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

public sealed class BlackboardApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private BlackboardAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new BlackboardAppFactory();
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
    public async Task BlackboardApi_CreatesListsGetsAndDeletesEntriesWithoutProject()
    {
        var slug = $"api-note-{Guid.NewGuid():N}";
        var createResponse = await _client.PostAsJsonAsync("/api/blackboard", new
        {
            slug,
            title = "API Note",
            content = "# API\n\nShared memory",
            tags = new[] { "api", "memory" },
            idle_ttl_seconds = 3600
        });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<BlackboardEntry>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal(slug, created!.Slug);
        Assert.Equal(3600, created.IdleTtlSeconds);
        Assert.Equal(new[] { "api", "memory" }, created.Tags);

        var listResponse = await _client.GetAsync("/api/blackboard?tags=api");
        listResponse.EnsureSuccessStatusCode();
        var listed = await listResponse.Content.ReadFromJsonAsync<List<BlackboardEntrySummary>>(JsonOpts);
        Assert.NotNull(listed);
        Assert.Contains(listed!, entry => entry.Slug == slug);

        var getResponse = await _client.GetAsync($"/api/blackboard/{slug}");
        getResponse.EnsureSuccessStatusCode();
        var fetched = await getResponse.Content.ReadFromJsonAsync<BlackboardEntry>(JsonOpts);
        Assert.NotNull(fetched);
        Assert.Equal("# API\n\nShared memory", fetched!.Content);

        var deleteResponse = await _client.DeleteAsync($"/api/blackboard/{slug}");
        deleteResponse.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/blackboard/{slug}")).StatusCode);
    }

    [Fact]
    public async Task BlackboardApi_CleanupDeletesExpiredIdleEntries()
    {
        var slug = $"api-expired-{Guid.NewGuid():N}";
        var createResponse = await _client.PostAsJsonAsync("/api/blackboard", new
        {
            slug,
            title = "Expired",
            content = "temporary",
            idle_ttl_seconds = 1
        });
        createResponse.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
            await using var conn = await db.CreateConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE blackboard_entries SET last_accessed_at = @lastAccessed WHERE slug = @slug";
            cmd.Parameters.AddWithValue("@slug", slug);
            cmd.Parameters.AddWithValue("@lastAccessed", DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss"));
            await cmd.ExecuteNonQueryAsync();
        }

        var cleanupResponse = await _client.PostAsync("/api/blackboard/cleanup", null);
        cleanupResponse.EnsureSuccessStatusCode();
        var cleanup = await cleanupResponse.Content.ReadFromJsonAsync<CleanupResponse>(JsonOpts);
        Assert.Equal(1, cleanup!.Deleted);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/blackboard/{slug}")).StatusCode);
    }

    private sealed record CleanupResponse(int Deleted);

    private sealed class BlackboardAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-blackboard-api-{Guid.NewGuid()}.db");

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
