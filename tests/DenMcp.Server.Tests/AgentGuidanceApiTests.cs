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

public sealed class AgentGuidanceApiTests : IAsyncLifetime
{
    private readonly string _projectId = $"guidance-api-test-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private GuidanceAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new GuidanceAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        await projects.CreateAsync(new Project { Id = _projectId, Name = "Guidance API Test" });
        await documents.UpsertAsync(new Document
        {
            ProjectId = "_global",
            Slug = "global-guidance",
            Title = "Global Guidance",
            Content = "Global guidance content",
            DocType = DocType.Convention,
            Tags = ["guidance"]
        });
        await documents.UpsertAsync(new Document
        {
            ProjectId = _projectId,
            Slug = "project-guidance",
            Title = "Project Guidance",
            Content = "Project guidance content",
            DocType = DocType.Spec,
            Tags = ["guidance"]
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AgentGuidance_RestRoundTripAndResolve()
    {
        var globalResponse = await _client.PostAsJsonAsync("/api/projects/_global/agent-guidance/entries", new
        {
            document_slug = "global-guidance",
            document_project_id = "_global",
            importance = "required",
            audience = new[] { "all" },
            sort_order = 10,
            notes = "Global policy"
        });
        globalResponse.EnsureSuccessStatusCode();

        var localResponse = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/agent-guidance/entries", new
        {
            document_slug = "project-guidance",
            importance = "important",
            audience = new[] { "coder" },
            sort_order = 20
        });
        localResponse.EnsureSuccessStatusCode();

        var listResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-guidance/entries?includeGlobal=true");
        listResponse.EnsureSuccessStatusCode();
        var entries = await listResponse.Content.ReadFromJsonAsync<List<AgentGuidanceEntry>>(JsonOpts);
        Assert.NotNull(entries);
        Assert.Equal(2, entries!.Count);
        Assert.Equal("_global", entries[0].ProjectId);
        Assert.Equal(_projectId, entries[1].ProjectId);

        var resolveResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-guidance");
        resolveResponse.EnsureSuccessStatusCode();
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<ResolvedAgentGuidance>(JsonOpts);
        Assert.NotNull(resolved);
        Assert.Equal(_projectId, resolved!.ProjectId);
        Assert.Equal(2, resolved.Sources.Count);
        Assert.Equal("global-guidance", resolved.Sources[0].Slug);
        Assert.Equal("project-guidance", resolved.Sources[1].Slug);
        Assert.Contains("Global guidance content", resolved.Content);
        Assert.Contains("Project guidance content", resolved.Content);
    }

    [Fact]
    public async Task AgentGuidance_DeleteIsScopedAndKeepsReferencedDocument()
    {
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/agent-guidance/entries", new
        {
            document_slug = "project-guidance",
            sort_order = 10
        });
        createResponse.EnsureSuccessStatusCode();
        var entry = await createResponse.Content.ReadFromJsonAsync<AgentGuidanceEntry>(JsonOpts);
        Assert.NotNull(entry);

        var wrongScopeResponse = await _client.DeleteAsync($"/api/projects/_global/agent-guidance/entries/{entry!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, wrongScopeResponse.StatusCode);

        var stillListedResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-guidance/entries");
        stillListedResponse.EnsureSuccessStatusCode();
        var stillListed = await stillListedResponse.Content.ReadFromJsonAsync<List<AgentGuidanceEntry>>(JsonOpts);
        Assert.Single(stillListed!);

        var deleteResponse = await _client.DeleteAsync($"/api/projects/{_projectId}/agent-guidance/entries/{entry.Id}");
        deleteResponse.EnsureSuccessStatusCode();

        var listResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-guidance/entries");
        listResponse.EnsureSuccessStatusCode();
        var entries = await listResponse.Content.ReadFromJsonAsync<List<AgentGuidanceEntry>>(JsonOpts);
        Assert.Empty(entries!);

        var docResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents/project-guidance");
        docResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AgentGuidance_NonProjectSpace_ResolveAndList()
    {
        var personalId = $"personal-api-test-{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        await projects.CreateAsync(new Project { Id = personalId, Name = "Personal API Test", Kind = "personal" });
        await documents.UpsertAsync(new Document
        {
            ProjectId = personalId,
            Slug = "personal-policy",
            Title = "Personal Policy",
            Content = "Personal space policy content",
            DocType = DocType.Convention,
            Tags = ["personal"]
        });

        var globalResponse = await _client.PostAsJsonAsync("/api/projects/_global/agent-guidance/entries", new
        {
            document_slug = "global-guidance",
            document_project_id = "_global",
            importance = "required",
            sort_order = 10
        });
        globalResponse.EnsureSuccessStatusCode();

        var localResponse = await _client.PostAsJsonAsync($"/api/projects/{personalId}/agent-guidance/entries", new
        {
            document_slug = "personal-policy",
            document_project_id = personalId,
            importance = "important",
            sort_order = 20
        });
        localResponse.EnsureSuccessStatusCode();

        var listResponse = await _client.GetAsync($"/api/projects/{personalId}/agent-guidance/entries?includeGlobal=true");
        listResponse.EnsureSuccessStatusCode();
        var entries = await listResponse.Content.ReadFromJsonAsync<List<AgentGuidanceEntry>>(JsonOpts);
        Assert.NotNull(entries);
        Assert.Equal(2, entries!.Count);
        Assert.Equal("_global", entries[0].ProjectId);
        Assert.Equal(personalId, entries[1].ProjectId);

        var resolveResponse = await _client.GetAsync($"/api/projects/{personalId}/agent-guidance");
        resolveResponse.EnsureSuccessStatusCode();
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<ResolvedAgentGuidance>(JsonOpts);
        Assert.NotNull(resolved);
        Assert.Equal(personalId, resolved!.ProjectId);
        Assert.Equal(2, resolved.Sources.Count);
        Assert.Equal("global-guidance", resolved.Sources[0].Slug);
        Assert.Equal("personal-policy", resolved.Sources[1].Slug);
        Assert.Contains("Global guidance content", resolved.Content);
        Assert.Contains("Personal space policy content", resolved.Content);
    }

    private sealed class GuidanceAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-guidance-api-{Guid.NewGuid()}.db");

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
