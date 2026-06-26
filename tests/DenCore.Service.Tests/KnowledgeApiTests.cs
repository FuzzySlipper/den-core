using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Routes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenCore.Service.Tests;

public sealed class KnowledgeApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    // Use a simple hex suffix (no hyphens, which search parsers can treat as operators)
    private static string U() => Guid.NewGuid().ToString("N")[..12];

    private KnowledgeAppFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly string _testProjectId = $"knowledgetest{U()}";

    public async Task InitializeAsync()
    {
        _factory = new KnowledgeAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(_testProjectId) is null)
            await projects.CreateAsync(new Project { Id = _testProjectId, Name = "Knowledge API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── #2131: Core storage / CRUD tests ──

    [Fact]
    public async Task KnowledgeEntry_RoundTripsWithoutProjectScope()
    {
        var slug = $"dencoreboundaries{U()}";

        var createResponse = await _client.PostAsJsonAsync("/api/knowledge/entries", new
        {
            slug,
            title = "Den Core service boundaries",
            body_markdown = "# Den Core\n\nThe canonical Den API/DB service.",
            kind = "service_map",
            tags = new[] { "domain:den", "service:den-core" },
            audience = new[] { "planner", "runner" },
            aliases = new[] { "core vs mcp", "den monolith split" },
            status = "draft",
            curation_state = "unreviewed_import",
            changed_by = "test-agent",
            change_note = "Initial import candidate."
        });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<KnowledgeEntry>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal(slug, created!.Slug);
        Assert.Equal("Den Core service boundaries", created.Title);
        Assert.Equal("service_map", created.Kind);
        Assert.Equal("draft", created.Status);
        Assert.Equal("unreviewed_import", created.CurationState);
        Assert.Contains("domain:den", created.Tags);
        Assert.Contains("planner", created.Audience);

        // GET by slug
        var getResponse = await _client.GetAsync($"/api/knowledge/entries/{slug}");
        getResponse.EnsureSuccessStatusCode();
        var fetched = await getResponse.Content.ReadFromJsonAsync<KnowledgeEntry>(JsonOpts);
        Assert.NotNull(fetched);
        Assert.Equal(slug, fetched!.Slug);

        // Update and verify revision behavior
        var updateResponse = await _client.PostAsJsonAsync("/api/knowledge/entries", new
        {
            slug,
            title = "Den Core service boundaries (updated)",
            body_markdown = "# Den Core\n\nUpdated description.",
            kind = "service_map",
            tags = new[] { "domain:den", "service:den-core" },
            status = "reviewed",
            curation_state = "agent_curated",
            changed_by = "test-agent",
            change_note = "Reviewed and promoted."
        });
        updateResponse.EnsureSuccessStatusCode();

        // Verify revision created
        var revResponse = await _client.GetAsync($"/api/knowledge/entries/{slug}/revisions");
        revResponse.EnsureSuccessStatusCode();
        var revBody = await revResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(revBody.GetProperty("count").GetInt32() >= 1,
            $"Expected >=1 revision, got {revBody.GetProperty("count").GetInt32()}");
    }

    [Fact]
    public async Task KnowledgeList_DefaultsToReviewedOnly()
    {
        var uniqueTerm = $"zebraonly{U()}";

        await CreateEntryAsync($"{uniqueTerm}reviewed", "Zebra Reviewed", $"{uniqueTerm} reviewedbody", "reviewed", "human_curated");
        await CreateEntryAsync($"{uniqueTerm}draft", "Zebra Draft", $"{uniqueTerm} draftbody", "draft", "unreviewed_import");
        await CreateEntryAsync($"{uniqueTerm}deprecated", "Zebra Deprecated", $"{uniqueTerm} deprecatedbody", "deprecated", "agent_curated");
        await CreateEntryAsync($"{uniqueTerm}needsreview", "Zebra Needs Review", $"{uniqueTerm} needsreviewbody", "needs_review", "unreviewed_import");

        // Default list = reviewed only
        var listResponse = await _client.GetAsync("/api/knowledge/entries?limit=50");
        listResponse.EnsureSuccessStatusCode();
        var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = listBody.GetProperty("items").EnumerateArray().ToList();
        var zebraItems = items.Where(i => i.GetProperty("slug").GetString()!.StartsWith(uniqueTerm)).ToList();
        Assert.Single(zebraItems);
        Assert.Equal($"{uniqueTerm}reviewed", zebraItems[0].GetProperty("slug").GetString());

        // With include_unreviewed=true + include_deprecated=true
        var listAllResponse = await _client.GetAsync("/api/knowledge/entries?include_unreviewed=true&include_deprecated=true");
        listAllResponse.EnsureSuccessStatusCode();
        var listAllBody = await listAllResponse.Content.ReadFromJsonAsync<JsonElement>();
        var allZebraItems = listAllBody.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("slug").GetString()!.StartsWith(uniqueTerm)).ToList();
        Assert.Equal(4, allZebraItems.Count); // draft + reviewed + deprecated + needs_review
    }

    [Fact]
    public async Task KnowledgeSearch_DefaultsToReviewedOnly()
    {
        var uniqueTerm = $"quokkaonly{U()}";

        await CreateEntryAsync($"{uniqueTerm}reviewed", "Quokka Reviewed", $"{uniqueTerm} reviewed only", "reviewed", "human_curated");
        await CreateEntryAsync($"{uniqueTerm}draft", "Quokka Draft", $"{uniqueTerm} draft only", "draft", "unreviewed_import");
        await CreateEntryAsync($"{uniqueTerm}deprecated", "Quokka Deprecated", $"{uniqueTerm} deprecated only", "deprecated", "agent_curated");

        // Default search = reviewed only
        var searchResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new { query = uniqueTerm });
        searchResponse.EnsureSuccessStatusCode();
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var results = searchBody.GetProperty("results").EnumerateArray().ToList();
        Assert.Single(results);
        Assert.Equal($"{uniqueTerm}reviewed", results[0].GetProperty("slug").GetString());
        Assert.Equal("reviewed", results[0].GetProperty("status").GetString());

        // With include flags
        var searchAllResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new
        {
            query = uniqueTerm,
            include_deprecated = true,
            include_unreviewed = true
        });
        searchAllResponse.EnsureSuccessStatusCode();
        var searchAllBody = await searchAllResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, searchAllBody.GetProperty("count").GetInt32()); // reviewed + draft + deprecated
    }

    [Fact]
    public async Task KnowledgeSearch_RequiresAllRequiredTags()
    {
        var uniqueTerm = $"tapironly{U()}";

        await CreateEntryAsync($"{uniqueTerm}both", "Tapir Both", $"{uniqueTerm} both entry", "reviewed", "human_curated",
            tags: ["domain:den", "topic:routing"]);
        await CreateEntryAsync($"{uniqueTerm}routingonly", "Tapir Routing", $"{uniqueTerm} routing entry", "reviewed", "human_curated",
            tags: ["topic:routing"]);
        await CreateEntryAsync($"{uniqueTerm}denonly", "Tapir Den", $"{uniqueTerm} den entry", "reviewed", "human_curated",
            tags: ["domain:den"]);

        var searchResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new
        {
            query = uniqueTerm,
            required_tags = new[] { "domain:den", "topic:routing" }
        });
        searchResponse.EnsureSuccessStatusCode();
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var results = searchBody.GetProperty("results").EnumerateArray().ToList();
        Assert.Single(results);
        Assert.Equal($"{uniqueTerm}both", results[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task KnowledgeSearch_AnyTagsFilterOr()
    {
        var uniqueTerm = $"okapionly{U()}";

        await CreateEntryAsync($"{uniqueTerm}a", "Okapi A", $"{uniqueTerm} entry a", "reviewed", "human_curated",
            tags: ["group:a", "group:common"]);
        await CreateEntryAsync($"{uniqueTerm}b", "Okapi B", $"{uniqueTerm} entry b", "reviewed", "human_curated",
            tags: ["group:b", "group:common"]);

        var searchResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new
        {
            query = uniqueTerm,
            any_tags = new[] { "group:a" }
        });
        searchResponse.EnsureSuccessStatusCode();
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, searchBody.GetProperty("count").GetInt32());

        var searchBothResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new
        {
            query = uniqueTerm,
            any_tags = new[] { "group:a", "group:b" }
        });
        searchBothResponse.EnsureSuccessStatusCode();
        var searchBothBody = await searchBothResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, searchBothBody.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task KnowledgeApi_KnowledgeDoesNotAppearInDocumentListOrSearch()
    {
        var uniqueTerm = $"knowledgetestonly{U()}";

        // Create a knowledge entry
        await CreateEntryAsync($"{uniqueTerm}entry", "Knowledge Only", $"{uniqueTerm} body text", "reviewed", "human_curated");

        // Create a normal document
        await CreateDocumentAsync("normaldoc", "Normal Document", "This is a normal document.");

        // Knowledge must NOT appear in document list
        var docListResponse = await _client.GetAsync($"/api/documents?project_id={_testProjectId}");
        docListResponse.EnsureSuccessStatusCode();
        var docListBody = await docListResponse.Content.ReadFromJsonAsync<JsonElement>();
        var docListItems = docListBody.EnumerateArray().ToList();
        Assert.DoesNotContain(docListItems, d => d.GetProperty("slug").GetString()!.Contains(uniqueTerm));

        // Knowledge must NOT appear in document search
        var docSearchResponse = await _client.GetAsync($"/api/documents/search?query={uniqueTerm}");
        docSearchResponse.EnsureSuccessStatusCode();
        var docSearchBody = await docSearchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(docSearchBody.EnumerateArray().ToList());

        // Knowledge must NOT appear in project-scoped document list
        var projDocListResponse = await _client.GetAsync($"/api/projects/{_testProjectId}/documents");
        projDocListResponse.EnsureSuccessStatusCode();
        var projDocListBody = await projDocListResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projDocListItems = projDocListBody.EnumerateArray().ToList();
        Assert.DoesNotContain(projDocListItems, d => d.GetProperty("slug").GetString()!.Contains(uniqueTerm));

        // Knowledge IS retrievable through its own API
        var knowledgeGetResponse = await _client.GetAsync($"/api/knowledge/entries/{uniqueTerm}entry");
        knowledgeGetResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task KnowledgeFullEntry_ProgressiveDisclosure()
    {
        var slug = $"capybaratest{U()}";

        await CreateEntryAsync(slug, "Capybara Test", $"# {slug}\n\nDeep body content here.", "reviewed", "human_curated");

        // List returns summary without body_markdown
        var listResponse = await _client.GetAsync("/api/knowledge/entries?limit=50");
        listResponse.EnsureSuccessStatusCode();
        var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var capybaraItem = listBody.GetProperty("items").EnumerateArray()
            .FirstOrDefault(i => i.GetProperty("slug").GetString() == slug);
        Assert.NotEqual(default, capybaraItem);
        Assert.False(capybaraItem.TryGetProperty("body_markdown", out _));

        // Search returns snippet without body_markdown
        var searchResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new { query = slug });
        searchResponse.EnsureSuccessStatusCode();
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var result = searchBody.GetProperty("results").EnumerateArray().First();
        Assert.NotNull(result.GetProperty("snippet").GetString());
        Assert.False(result.TryGetProperty("body_markdown", out _));

        // GET entry returns full body_markdown
        var getResponse = await _client.GetAsync($"/api/knowledge/entries/{slug}");
        getResponse.EnsureSuccessStatusCode();
        var fullEntry = await getResponse.Content.ReadFromJsonAsync<KnowledgeEntry>(JsonOpts);
        Assert.NotNull(fullEntry);
        Assert.Contains(slug, fullEntry!.BodyMarkdown);
    }

    [Fact]
    public async Task KnowledgeRepository_RevisionsIncrementOnUpdate()
    {
        var slug = $"revtestonly{U()}";

        await CreateEntryAsync(slug, "Revision 1", "Content v1", "draft", "unreviewed_import");

        var updateResponse = await _client.PostAsJsonAsync("/api/knowledge/entries", new
        {
            slug,
            title = "Revision 2",
            body_markdown = "Content v2",
            kind = "reference",
            status = "reviewed",
            curation_state = "agent_curated",
            changed_by = "test-agent",
            change_note = "Updated to v2."
        });
        updateResponse.EnsureSuccessStatusCode();

        var revResponse = await _client.GetAsync($"/api/knowledge/entries/{slug}/revisions");
        revResponse.EnsureSuccessStatusCode();
        var revBody = await revResponse.Content.ReadFromJsonAsync<JsonElement>();
        var revisions = revBody.GetProperty("revisions").EnumerateArray().ToList();
        Assert.Equal(1, revBody.GetProperty("count").GetInt32());
        Assert.Equal(1, revisions[0].GetProperty("revision_number").GetInt32());
        Assert.Equal("Updated to v2.", revisions[0].GetProperty("change_note").GetString());

        // Current entry has latest content
        var getResponse = await _client.GetAsync($"/api/knowledge/entries/{slug}");
        getResponse.EnsureSuccessStatusCode();
        var current = await getResponse.Content.ReadFromJsonAsync<KnowledgeEntry>(JsonOpts);
        Assert.NotNull(current);
        Assert.Equal("Content v2", current!.BodyMarkdown);
        Assert.Equal("reviewed", current.Status);
    }

    // ── #2132: Search-specific tests ──

    [Fact]
    public async Task KnowledgeSearch_ReturnsSnippetAndStatusMetadata()
    {
        var uniqueTerm = $"axolotlonly{U()}";

        await CreateEntryAsync($"{uniqueTerm}entry", "Axolotl Reference",
            $"# {uniqueTerm}\n\nThe axolotl is a paedomorphic salamander.",
            "reviewed", "agent_curated");

        var searchResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new { query = uniqueTerm });
        searchResponse.EnsureSuccessStatusCode();
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var results = searchBody.GetProperty("results").EnumerateArray().ToList();

        Assert.NotEmpty(results);
        var result = results[0];
        Assert.Equal($"{uniqueTerm}entry", result.GetProperty("slug").GetString());
        Assert.NotNull(result.GetProperty("snippet").GetString());
        Assert.Equal("reviewed", result.GetProperty("status").GetString());
        Assert.Equal("agent_curated", result.GetProperty("curation_state").GetString());
    }

    [Fact]
    public async Task KnowledgeSearch_ListAndSearchHaveConsistentSummaries()
    {
        var slug = $"llamaknowledge{U()}";

        await CreateEntryAsync(slug, "Llama Knowledge", "Llama details here.", "reviewed", "human_curated",
            tags: ["domain:animals", "topic:mammals"],
            summary: "Short summary about llamas.");

        var listResponse = await _client.GetAsync("/api/knowledge/entries?limit=50");
        listResponse.EnsureSuccessStatusCode();

        var searchResponse = await _client.PostAsJsonAsync("/api/knowledge/search", new { query = slug });
        searchResponse.EnsureSuccessStatusCode();
        var searchBody = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var result = searchBody.GetProperty("results").EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("slug").GetString() == slug);
        Assert.NotEqual(default, result);
        Assert.Equal("Short summary about llamas.", result.GetProperty("summary").GetString());
    }

    // ── #2133: Guide tests ──

    [Fact]
    public async Task KnowledgeGuide_ReturnsExtractiveCitations()
    {
        // Create reviewed entries with distinct content
        var slug1 = $"guideentry1{U()}";
        var slug2 = $"guideentry2{U()}";
        var commonTag = $"guidetopic{U()}";

        await CreateEntryAsync(slug1, "Knowledge Tables",
            $"The {commonTag} knowledge library uses separate tables for storage. This is the correct approach for the Den knowledge base.",
            "reviewed", "human_curated", tags: [commonTag, "topic:knowledge"]);

        await CreateEntryAsync(slug2, "Document Scope",
            $"The {commonTag} document schema is project-scoped. Normal document search queries the documents table only.",
            "reviewed", "human_curated", tags: [commonTag, "topic:documents"]);

        // Guide query
        var guideResponse = await _client.PostAsJsonAsync("/api/knowledge/guide", new
        {
            question = $"I am confused about {commonTag} and tables versus documents.",
            required_tags = new[] { commonTag }
        });
        guideResponse.EnsureSuccessStatusCode();

        var guideBody = await guideResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Should have an answer
        Assert.NotNull(guideBody.GetProperty("answer").GetString());
        Assert.False(string.IsNullOrEmpty(guideBody.GetProperty("answer").GetString()));

        // Should have citations
        var citations = guideBody.GetProperty("citations").EnumerateArray().ToList();
        Assert.NotEmpty(citations);

        // Each citation should have slug, title, excerpt
        var first = citations[0];
        Assert.NotNull(first.GetProperty("slug").GetString());
        Assert.NotNull(first.GetProperty("title").GetString());
        Assert.NotNull(first.GetProperty("excerpt").GetString());

        // Should cite both entries
        var citedSlugs = citations.Select(c => c.GetProperty("slug").GetString()).ToHashSet();
        Assert.Contains(slug1, citedSlugs);
        Assert.Contains(slug2, citedSlugs);
    }

    // ── Helpers ──

    private async Task CreateEntryAsync(string slug, string title, string bodyTerm,
        string status, string curationState,
        string[]? tags = null, string? summary = null)
    {
        tags ??= ["tag:test"];
        var body = bodyTerm.StartsWith("#") ? bodyTerm : $"# {title}\n\n{bodyTerm}";
        var response = await _client.PostAsJsonAsync("/api/knowledge/entries", new
        {
            slug,
            title,
            body_markdown = body,
            kind = "reference",
            status,
            curation_state = curationState,
            tags,
            summary,
            changed_by = "test-agent",
            change_note = "Test creation."
        });
        response.EnsureSuccessStatusCode();
    }

    private async Task CreateDocumentAsync(string slug, string title, string content)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{_testProjectId}/documents", new
        {
            slug,
            title,
            content,
            doc_type = "reference"
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed class KnowledgeAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dencoreknowledgeapi{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["DenCore:ConnectionString"] = DatabaseInitializer.GetConnectionString(_dbPath),
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            DatabaseInitializer.DisposeLeaseAsync(_dbPath).AsTask().GetAwaiter().GetResult();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
