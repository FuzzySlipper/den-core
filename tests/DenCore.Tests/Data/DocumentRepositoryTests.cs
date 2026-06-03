using DenCore.Data;
using DenCore.Models;

namespace DenCore.Tests.Data;

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

    // --- Visibility / Archive tests ---

    [Fact]
    public async Task Upsert_DefaultsToNormalVisibility()
    {
        var doc = await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "vis-default", Title = "Default Vis",
            Content = "content"
        });
        Assert.Equal(DocumentVisibility.Normal, doc.Visibility);

        var fetched = await _repo.GetAsync("proj", "vis-default");
        Assert.NotNull(fetched);
        Assert.Equal(DocumentVisibility.Normal, fetched!.Visibility);
    }

    [Fact]
    public async Task List_ExcludesArchivedByDefault()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "normal-doc", Title = "Normal Doc", Content = "visible"
        });
        var archivedDoc = await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "archived-doc", Title = "Archived Doc", Content = "hidden",
            Visibility = DocumentVisibility.Archived
        });
        // Verify it was stored as archived
        Assert.Equal(DocumentVisibility.Archived, archivedDoc.Visibility);

        var docs = await _repo.ListAsync("proj");
        Assert.Single(docs);
        Assert.Equal("normal-doc", docs[0].Slug);
    }

    [Fact]
    public async Task List_CanFilterByVisibility()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "normal-vis", Title = "Normal", Content = "x",
            Visibility = DocumentVisibility.Normal
        });
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "hidden-vis", Title = "Hidden", Content = "x",
            Visibility = DocumentVisibility.Hidden
        });
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "archived-vis", Title = "Archived", Content = "x",
            Visibility = DocumentVisibility.Archived
        });

        var normalDocs = await _repo.ListAsync("proj", visibility: DocumentVisibility.Normal);
        Assert.Single(normalDocs);
        Assert.Equal("normal-vis", normalDocs[0].Slug);

        var archivedDocs = await _repo.ListAsync("proj", visibility: DocumentVisibility.Archived);
        Assert.Single(archivedDocs);
        Assert.Equal("archived-vis", archivedDocs[0].Slug);

        var hiddenDocs = await _repo.ListAsync("proj", visibility: DocumentVisibility.Hidden);
        Assert.Single(hiddenDocs);
        Assert.Equal("hidden-vis", hiddenDocs[0].Slug);
    }

    [Fact]
    public async Task Search_ExcludesArchived()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "search-normal", Title = "Searchable Normal",
            Content = "unique_term visible"
        });
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "search-archived", Title = "Searchable Archived",
            Content = "unique_term hidden", Visibility = DocumentVisibility.Archived
        });

        var results = await _repo.SearchAsync("unique_term");
        Assert.Single(results);
        Assert.Equal("search-normal", results[0].Slug);
    }

    [Fact]
    public async Task UpdateVisibility_ArchivesDocument()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "to-archive", Title = "To Archive", Content = "content"
        });

        var updated = await _repo.UpdateVisibilityAsync("proj", "to-archive", DocumentVisibility.Archived);
        Assert.NotNull(updated);
        Assert.Equal(DocumentVisibility.Archived, updated!.Visibility);

        // Verify it's excluded from default list
        var docs = await _repo.ListAsync("proj");
        Assert.DoesNotContain(docs, d => d.Slug == "to-archive");

        // But still accessible via GetAsync
        var fetched = await _repo.GetAsync("proj", "to-archive");
        Assert.NotNull(fetched);
        Assert.Equal(DocumentVisibility.Archived, fetched!.Visibility);
    }

    [Fact]
    public async Task UpdateVisibility_UnarchivesDocument()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "to-unarchive", Title = "To Unarchive",
            Content = "content", Visibility = DocumentVisibility.Archived
        });

        var updated = await _repo.UpdateVisibilityAsync("proj", "to-unarchive", DocumentVisibility.Normal);
        Assert.NotNull(updated);
        Assert.Equal(DocumentVisibility.Normal, updated!.Visibility);

        // Now visible in default list
        var docs = await _repo.ListAsync("proj");
        Assert.Contains(docs, d => d.Slug == "to-unarchive");
    }

    [Fact]
    public async Task UpdateVisibility_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.UpdateVisibilityAsync("proj", "nonexistent", DocumentVisibility.Archived);
        Assert.Null(result);
    }

    [Fact]
    public async Task ListArchived_ReturnsOnlyArchived()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "arch-list-1", Title = "Archived 1", Content = "x",
            Visibility = DocumentVisibility.Archived
        });
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "arch-list-2", Title = "Archived 2", Content = "x",
            Visibility = DocumentVisibility.Archived
        });
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "arch-list-normal", Title = "Normal", Content = "x"
        });

        var archived = await _repo.ListArchivedAsync("proj");
        Assert.Equal(2, archived.Count);
        Assert.All(archived, d => Assert.Equal(DocumentVisibility.Archived, d.Visibility));
    }

    [Fact]
    public async Task SearchArchived_ReturnsOnlyArchivedMatches()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "arch-search-1", Title = "Archived Searchable",
            Content = "rare_keyword here", Visibility = DocumentVisibility.Archived
        });
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "arch-search-normal", Title = "Normal Searchable",
            Content = "rare_keyword also here"
        });

        var results = await _repo.SearchArchivedAsync("rare_keyword");
        Assert.Single(results);
        Assert.Equal("arch-search-1", results[0].Slug);
        Assert.Equal(DocumentVisibility.Archived, results[0].Visibility);
    }

    [Fact]
    public async Task ArchivePreflight_NoReferences_ReturnsCanArchive()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "preflight-clean", Title = "Clean", Content = "x"
        });

        var result = await _repo.ArchivePreflightAsync("proj", "preflight-clean");
        Assert.True(result.CanArchive);
        Assert.Empty(result.ReferencedBy);
    }

    [Fact]
    public async Task ArchivePreflight_WithGuidanceReference_ReturnsCannotArchive()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "preflight-guided", Title = "Guided", Content = "x"
        });

        var guidanceRepo = new AgentGuidanceRepository(_testDb.Db);
        await guidanceRepo.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "proj",
            DocumentProjectId = "proj",
            DocumentSlug = "preflight-guided",
            Importance = AgentGuidanceImportance.Important,
            SortOrder = 0
        });

        var result = await _repo.ArchivePreflightAsync("proj", "preflight-guided");
        Assert.False(result.CanArchive);
        Assert.Single(result.ReferencedBy);
        Assert.Equal("agent_guidance", result.ReferencedBy[0].RefKind);
    }

    [Fact]
    public async Task ArchivePreflight_NonExistentDoc_ReturnsCannotArchive()
    {
        var result = await _repo.ArchivePreflightAsync("proj", "does-not-exist");
        Assert.False(result.CanArchive);
        Assert.Empty(result.ReferencedBy);
    }

    [Fact]
    public async Task DocumentSummary_IncludesVisibility()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "summary-vis", Title = "Summary Vis",
            Content = "x", Visibility = DocumentVisibility.Hidden
        });

        var docs = await _repo.ListAsync("proj", visibility: DocumentVisibility.Hidden);
        Assert.Single(docs);
        Assert.Equal(DocumentVisibility.Hidden, docs[0].Visibility);
    }

    [Fact]
    public async Task SearchResult_IncludesVisibility()
    {
        await _repo.UpsertAsync(new Document
        {
            ProjectId = "proj", Slug = "search-vis", Title = "Search Vis",
            Content = "vis_test_term searchable"
        });

        var results = await _repo.SearchAsync("vis_test_term");
        Assert.Single(results);
        Assert.Equal(DocumentVisibility.Normal, results[0].Visibility);
    }
}
