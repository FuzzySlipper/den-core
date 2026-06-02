using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenCore.Service.Tests;

public sealed class MemoryApiTests : IAsyncLifetime
{
    private readonly string _projectId = $"memory-api-test-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private MemoryAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new MemoryAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(_projectId) is null)
            await projects.CreateAsync(new Project { Id = _projectId, Name = "Memory API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MemoryApi_ListSpaces_ReturnsInitialHermesSpaces()
    {
        var response = await _client.GetAsync($"/api/v1/projects/{_projectId}/memory/spaces");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var spaces = doc.RootElement.GetProperty("spaces").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("project", spaces);
        Assert.Contains("task", spaces);
        Assert.Contains("session", spaces);
        Assert.Contains("review", spaces);
    }

    [Fact]
    public async Task MemoryApi_WriteReadSearchAndDelete_RoundTripsNoSecretEntry()
    {
        var writeResponse = await _client.PostAsJsonAsync($"/api/v1/projects/{_projectId}/memory/entries", new
        {
            key = "live-smoke-note",
            space = "project",
            content = "No-secret memory live smoke marker for Den Hermes.",
            metadata = new { source = "test" },
            provenance = new { run_id = "run-1491", role = "smoke" }
        });
        writeResponse.EnsureSuccessStatusCode();

        using var written = JsonDocument.Parse(await writeResponse.Content.ReadAsStringAsync());
        var entryId = written.RootElement.GetProperty("entry_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(entryId));
        Assert.Equal("project", written.RootElement.GetProperty("space").GetString());
        Assert.Equal("live-smoke-note", written.RootElement.GetProperty("key").GetString());

        var readResponse = await _client.GetAsync($"/api/v1/projects/{_projectId}/memory/entries/{entryId}");
        readResponse.EnsureSuccessStatusCode();
        using var read = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
        Assert.Equal("No-secret memory live smoke marker for Den Hermes.", read.RootElement.GetProperty("content").GetString());
        Assert.Equal("project", read.RootElement.GetProperty("space").GetString());
        Assert.Equal("test", read.RootElement.GetProperty("metadata").GetProperty("source").GetString());
        Assert.Equal("run-1491", read.RootElement.GetProperty("provenance").GetProperty("run_id").GetString());
        Assert.Equal("smoke", read.RootElement.GetProperty("provenance").GetProperty("role").GetString());

        var searchResponse = await _client.PostAsJsonAsync($"/api/v1/projects/{_projectId}/memory/search", new
        {
            query = "Hermes",
            spaces = new[] { "project" },
            limit = 10
        });
        searchResponse.EnsureSuccessStatusCode();
        using var search = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        var first = search.RootElement.GetProperty("results").EnumerateArray().First();
        Assert.Equal(entryId, first.GetProperty("entry_id").GetString());
        Assert.Equal("project", first.GetProperty("space").GetString());

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/projects/{_projectId}/memory/entries/{entryId}")
        {
            Content = JsonContent.Create(new { tombstone_reason = "test cleanup" })
        };
        var deleteResponse = await _client.SendAsync(deleteRequest);
        deleteResponse.EnsureSuccessStatusCode();

        var readDeleted = await _client.GetAsync($"/api/v1/projects/{_projectId}/memory/entries/{entryId}");
        Assert.Equal(HttpStatusCode.NotFound, readDeleted.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var tombstonedDoc = await documents.GetAsync(_projectId, entryId!);
        Assert.NotNull(tombstonedDoc);
        var tombstoneTags = tombstonedDoc.Tags ?? [];
        Assert.Contains("den-memory-tombstoned", tombstoneTags);
        Assert.Contains("memory-tombstone-reason:test-cleanup", tombstoneTags);
    }



    [Fact]
    public async Task MemoryApi_UsesConfiguredServiceTokenWhenPresent()
    {
        var projectId = $"memory-auth-project-{Guid.NewGuid():N}";
        using var factory = new MemoryAppFactory(serviceToken: "memory-token");
        using var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            if (await projects.GetByIdAsync(projectId) is null)
                await projects.CreateAsync(new Project { Id = projectId, Name = "Memory Auth Project" });
        }

        var unauthenticated = await client.GetAsync($"/api/v1/projects/{projectId}/memory/spaces");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{projectId}/memory/spaces");
        request.Headers.Add("X-Den-Service-Token", "memory-token");
        var authenticated = await client.SendAsync(request);
        authenticated.EnsureSuccessStatusCode();
    }

    private sealed class MemoryAppFactory(string? serviceToken = null) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-memory-api-{Guid.NewGuid()}.db");

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
                    ["llm-model"] = "fake",
                    ["DenCore:GatewayContract:ServiceToken"] = serviceToken
                });
            });

            builder.ConfigureServices(services =>
            {
                if (serviceToken is not null)
                {
                    services.RemoveAll<DenCoreOptions>();
                    services.AddSingleton(new DenCoreOptions
                    {
                        DatabasePath = _dbPath,
                        GatewayContract = new GatewayContractOptions { ServiceToken = serviceToken }
                    });
                }
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
