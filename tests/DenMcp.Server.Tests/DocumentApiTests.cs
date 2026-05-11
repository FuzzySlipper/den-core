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

public sealed class DocumentApiTests : IAsyncLifetime
{
    private readonly string _projectId = $"document-api-test-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private DocumentAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new DocumentAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(_projectId) is null)
            await projects.CreateAsync(new Project { Id = _projectId, Name = "Document API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestDocuments_PostUpdatesContentWhileKeepingSuppliedMetadata()
    {
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/documents", new
        {
            slug = "ops-guide",
            title = "Ops Guide",
            content = "# Original\n",
            doc_type = "reference",
            tags = new[] { "ops", "guide" }
        });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<Document>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal(_projectId, created!.ProjectId);
        Assert.Equal("# Original\n", created.Content);
        Assert.Equal(DocType.Reference, created.DocType);
        Assert.Equal(new[] { "ops", "guide" }, created.Tags);

        var updateResponse = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/documents", new
        {
            slug = "ops-guide",
            title = "Ops Guide",
            content = "# Updated\n\nEdited in the web UI.",
            doc_type = "reference",
            tags = new[] { "ops", "guide" }
        });
        updateResponse.EnsureSuccessStatusCode();

        var updated = await updateResponse.Content.ReadFromJsonAsync<Document>(JsonOpts);
        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated!.Id);
        Assert.Equal("ops-guide", updated.Slug);
        Assert.Equal("Ops Guide", updated.Title);
        Assert.Equal("# Updated\n\nEdited in the web UI.", updated.Content);
        Assert.Equal(DocType.Reference, updated.DocType);
        Assert.Equal(new[] { "ops", "guide" }, updated.Tags);

        var getResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents/ops-guide");
        getResponse.EnsureSuccessStatusCode();

        var fetched = await getResponse.Content.ReadFromJsonAsync<Document>(JsonOpts);
        Assert.NotNull(fetched);
        Assert.Equal(updated.Content, fetched!.Content);
        Assert.Equal(updated.Title, fetched.Title);
        Assert.Equal(updated.DocType, fetched.DocType);
        Assert.Equal(updated.Tags, fetched.Tags);
    }

    [Fact]
    public async Task RestDocuments_SupportsGlobalDocumentScope()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/projects/_global/documents", new
        {
            slug = "operator-policy",
            title = "Operator Policy",
            content = "Global guidance",
            doc_type = "convention",
            tags = new[] { "ops" }
        });
        createResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync("/api/projects/_global/documents/operator-policy");
        getResponse.EnsureSuccessStatusCode();

        var fetched = await getResponse.Content.ReadFromJsonAsync<Document>(JsonOpts);
        Assert.NotNull(fetched);
        Assert.Equal("_global", fetched!.ProjectId);
        Assert.Equal("operator-policy", fetched.Slug);
        Assert.Equal("Global guidance", fetched.Content);
        Assert.Equal(DocType.Convention, fetched.DocType);
        Assert.Equal(new[] { "ops" }, fetched.Tags);
    }

    [Fact]
    public async Task RestDocuments_MemoryDocTypeWithSummary_RoundTrips()
    {
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/documents", new
        {
            slug = "hermes-memory",
            title = "Hermes Memory",
            content = "# Long memory content\n\nDetails here...",
            doc_type = "memory",
            tags = new[] { "hermes", "context", "user-preference" },
            summary = "Short indexable summary of the memory"
        });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<Document>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal(DocType.Memory, created!.DocType);
        Assert.Equal("Short indexable summary of the memory", created.Summary);

        var getResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents/hermes-memory");
        getResponse.EnsureSuccessStatusCode();

        var fetched = await getResponse.Content.ReadFromJsonAsync<Document>(JsonOpts);
        Assert.NotNull(fetched);
        Assert.Equal(DocType.Memory, fetched!.DocType);
        Assert.Equal("Short indexable summary of the memory", fetched.Summary);
        Assert.Equal(new[] { "hermes", "context", "user-preference" }, fetched.Tags);
    }

    [Fact]
    public async Task RestDocuments_ListIncludesSummary()
    {
        await _client.PostAsJsonAsync($"/api/projects/{_projectId}/documents", new
        {
            slug = "listed-summary",
            title = "Listed Summary",
            content = "Content",
            summary = "List-visible summary"
        });

        var listResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents");
        listResponse.EnsureSuccessStatusCode();

        var docs = await listResponse.Content.ReadFromJsonAsync<List<DocumentSummary>>(JsonOpts);
        Assert.NotNull(docs);
        var doc = docs!.FirstOrDefault(d => d.Slug == "listed-summary");
        Assert.NotNull(doc);
        Assert.Equal("List-visible summary", doc!.Summary);
    }

    [Fact]
    public async Task RestDocuments_ListFiltersByMemoryDocType()
    {
        await _client.PostAsJsonAsync($"/api/projects/{_projectId}/documents", new
        {
            slug = "memory-a",
            title = "Memory A",
            content = "x",
            doc_type = "memory"
        });
        await _client.PostAsJsonAsync($"/api/projects/{_projectId}/documents", new
        {
            slug = "spec-a",
            title = "Spec A",
            content = "x",
            doc_type = "spec"
        });

        var listResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents?doc_type=memory");
        listResponse.EnsureSuccessStatusCode();

        var docs = await listResponse.Content.ReadFromJsonAsync<List<DocumentSummary>>(JsonOpts);
        Assert.NotNull(docs);
        Assert.Single(docs);
        Assert.Equal("memory-a", docs![0].Slug);
        Assert.Equal(DocType.Memory, docs[0].DocType);
    }

    private sealed class DocumentAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-document-api-{Guid.NewGuid()}.db");

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
