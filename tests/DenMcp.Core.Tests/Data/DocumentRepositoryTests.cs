using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class DocumentRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private DocumentRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new DocumentRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpsertAndGet_RoundTrips()
    {
        var doc = await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "my-spec", Title = "My Spec",
            Content = "# Hello\nWorld", DocType = DocType.Spec
        });
        Assert.True(doc.Id > 0);

        var fetched = await _repo.GetAsync("proj", "my-spec");
        Assert.NotNull(fetched);
        Assert.Equal("My Spec", fetched.Title);
        Assert.Equal("# Hello\nWorld", fetched.Content);
    }

    [Fact]
    public async Task Upsert_OverwritesExisting()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "overwrite", Title = "V1", Content = "Original"
        });
        var updated = await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "overwrite", Title = "V2", Content = "Updated"
        });

        Assert.Equal("V2", updated.Title);
        Assert.Equal("Updated", updated.Content);

        var fetched = await _repo.GetAsync("proj", "overwrite");
        Assert.NotNull(fetched);
        Assert.Equal("V2", fetched.Title);
    }

    [Fact]
    public async Task List_DoesNotReturnContent()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "list-test", Title = "Listed", Content = "Big content"
        });

        var docs = await _repo.ListAsync("proj");
        Assert.Single(docs);
        Assert.Equal("Listed", docs[0].Title);
        // DocumentSummary has no Content property — that's the point
    }

    [Fact]
    public async Task List_FiltersByDocType()
    {
        await _repo.UpsertAsync(new Document { ProjectId = "proj", Slug = "a-prd", Title = "A", Content = "x", DocType = DocType.Prd });
        await _repo.UpsertAsync(new Document { ProjectId = "proj", Slug = "a-spec", Title = "B", Content = "x", DocType = DocType.Spec });

        var prds = await _repo.ListAsync("proj", docType: DocType.Prd);
        Assert.Single(prds);
        Assert.Equal("A", prds[0].Title);
    }

    [Fact]
    public async Task Search_FindsByContent()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "searchable", Title = "Searchable Doc",
            Content = "The quick brown fox jumps over the lazy dog."
        });

        var results = await _repo.SearchAsync("fox");
        Assert.Single(results);
        Assert.Equal("searchable", results[0].Slug);
        Assert.Contains("fox", results[0].Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_ReturnsSummary()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "searchable-summary", Title = "Searchable Summary Doc",
            Content = "The quick brown fox jumps over the lazy dog.",
            Summary = "A fox story"
        });

        var results = await _repo.SearchAsync("fox");
        Assert.Single(results);
        Assert.Equal("searchable-summary", results[0].Slug);
        Assert.Equal("A fox story", results[0].Summary);
    }

    [Fact]
    public async Task Search_FindsByTitle()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "titled", Title = "Architecture Decision Record",
            Content = "We decided to use SQLite."
        });

        var results = await _repo.SearchAsync("architecture");
        Assert.Single(results);
        Assert.Equal("titled", results[0].Slug);
    }

    [Fact]
    public async Task Search_ScopesToProject()
    {
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "other", Name = "Other" });

        await _repo.UpsertAsync(new Document { ProjectId = "proj", Slug = "a", Title = "A", Content = "shared term" });
        await _repo.UpsertAsync(new Document { ProjectId = "other", Slug = "b", Title = "B", Content = "shared term" });

        var scoped = await _repo.SearchAsync("shared", projectId: "proj");
        Assert.Single(scoped);
        Assert.Equal("proj", scoped[0].ProjectId);

        var all = await _repo.SearchAsync("shared");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Delete_RemovesDocument()
    {
        await _repo.UpsertAsync(new Document { ProjectId = "proj", Slug = "deleteme", Title = "D", Content = "bye" });
        var deleted = await _repo.DeleteAsync("proj", "deleteme");
        Assert.True(deleted);

        var fetched = await _repo.GetAsync("proj", "deleteme");
        Assert.Null(fetched);
    }

    [Fact]
    public async Task Delete_ReturnsFalse_WhenNotFound()
    {
        var deleted = await _repo.DeleteAsync("proj", "nonexistent");
        Assert.False(deleted);
    }

    [Fact]
    public async Task List_TagFilter_DoesNotMatchSubstrings()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "cli-doc", Title = "CLI Doc",
            Content = "x", Tags = ["cli"]
        });
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "client-doc", Title = "Client Doc",
            Content = "x", Tags = ["client"]
        });

        var cliDocs = await _repo.ListAsync("proj", tags: ["cli"]);
        Assert.Single(cliDocs);
        Assert.Equal("CLI Doc", cliDocs[0].Title);

        var clientDocs = await _repo.ListAsync("proj", tags: ["client"]);
        Assert.Single(clientDocs);
        Assert.Equal("Client Doc", clientDocs[0].Title);
    }

    [Fact]
    public async Task Upsert_OverwritesSummary()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "summary-overwrite", Title = "V1",
            Content = "Original", Summary = "First summary"
        });
        var updated = await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "summary-overwrite", Title = "V2",
            Content = "Updated", Summary = "Second summary"
        });

        Assert.Equal("Second summary", updated.Summary);

        var fetched = await _repo.GetAsync("proj", "summary-overwrite");
        Assert.NotNull(fetched);
        Assert.Equal("Second summary", fetched!.Summary);
    }
}
