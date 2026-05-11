using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public sealed class AgentGuidanceRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private ProjectRepository _projects = null!;
    private DocumentRepository _documents = null!;
    private AgentGuidanceRepository _guidance = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _projects = new ProjectRepository(_testDb.Db);
        _documents = new DocumentRepository(_testDb.Db);
        _guidance = new AgentGuidanceRepository(_testDb.Db);

        await _projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
        await _documents.UpsertAsync(new Document
        {
            ProjectId = "_global",
            Slug = "global-required",
            Title = "Global Required",
            Content = "Global guidance",
            DocType = DocType.Convention,
            Tags = ["guidance", "global"]
        });
        await _documents.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "project-important",
            Title = "Project Important",
            Content = "Project guidance",
            DocType = DocType.Spec,
            Tags = ["guidance", "project"]
        });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task Resolve_CombinesGlobalThenProjectGuidanceWithSourceMetadata()
    {
        await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "proj",
            DocumentProjectId = "proj",
            DocumentSlug = "project-important",
            Importance = AgentGuidanceImportance.Important,
            Audience = ["coder"],
            SortOrder = 20,
            Notes = "Project-local guidance"
        });
        await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "_global",
            DocumentProjectId = "_global",
            DocumentSlug = "global-required",
            Importance = AgentGuidanceImportance.Required,
            Audience = ["all"],
            SortOrder = 10,
            Notes = "Inherited by every project"
        });

        var resolved = await _guidance.ResolveAsync("proj");

        Assert.Equal("proj", resolved.ProjectId);
        Assert.Equal(2, resolved.Sources.Count);
        Assert.Equal("_global", resolved.Sources[0].ScopeProjectId);
        Assert.Equal("global-required", resolved.Sources[0].Slug);
        Assert.Equal(AgentGuidanceImportance.Required, resolved.Sources[0].Importance);
        Assert.Equal("proj", resolved.Sources[1].ScopeProjectId);
        Assert.Equal("project-important", resolved.Sources[1].Slug);
        Assert.Contains("Global guidance", resolved.Content);
        Assert.Contains("Project guidance", resolved.Content);
        Assert.Contains("_global/global-required", resolved.Content);
        Assert.Contains("proj/project-important", resolved.Content);
    }

    [Fact]
    public async Task Upsert_ReplacesExistingEntryForSameScopeAndDocument()
    {
        var first = await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "proj",
            DocumentProjectId = "proj",
            DocumentSlug = "project-important",
            Importance = AgentGuidanceImportance.Important,
            SortOrder = 30
        });

        var second = await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "proj",
            DocumentProjectId = "proj",
            DocumentSlug = "project-important",
            Importance = AgentGuidanceImportance.Required,
            Audience = ["reviewer"],
            SortOrder = 5,
            Notes = "Updated"
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(AgentGuidanceImportance.Required, second.Importance);
        Assert.Equal(["reviewer"], second.Audience);
        Assert.Equal(5, second.SortOrder);
        Assert.Equal("Updated", second.Notes);

        var entries = await _guidance.ListAsync("proj");
        var entry = Assert.Single(entries);
        Assert.Equal(second.Id, entry.Id);
    }

    [Fact]
    public async Task DeleteAsync_IsScopedAndKeepsReferencedDocument()
    {
        var global = await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "_global",
            DocumentProjectId = "_global",
            DocumentSlug = "global-required",
            Importance = AgentGuidanceImportance.Required,
            SortOrder = 0
        });
        var local = await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "proj",
            DocumentProjectId = "proj",
            DocumentSlug = "project-important",
            SortOrder = 0
        });

        var wrongScopeDeleted = await _guidance.DeleteAsync(global.Id, "proj");
        Assert.False(wrongScopeDeleted);

        var localDeleted = await _guidance.DeleteAsync(local.Id, "proj");
        Assert.True(localDeleted);

        var withGlobal = await _guidance.ListAsync("proj", includeGlobal: true);
        var remaining = Assert.Single(withGlobal);
        Assert.Equal(global.Id, remaining.Id);

        var referencedDocument = await _documents.GetAsync("proj", "project-important");
        Assert.NotNull(referencedDocument);
    }

    [Fact]
    public async Task List_CanIncludeInheritedGlobalEntries()
    {
        await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "_global",
            DocumentProjectId = "_global",
            DocumentSlug = "global-required",
            Importance = AgentGuidanceImportance.Required,
            SortOrder = 0
        });
        await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "proj",
            DocumentProjectId = "proj",
            DocumentSlug = "project-important",
            SortOrder = 0
        });

        var localOnly = await _guidance.ListAsync("proj");
        Assert.Single(localOnly);
        Assert.Equal("proj", localOnly[0].ProjectId);

        var withGlobal = await _guidance.ListAsync("proj", includeGlobal: true);
        Assert.Equal(["_global", "proj"], withGlobal.Select(entry => entry.ProjectId).ToArray());
    }

    [Fact]
    public async Task Resolve_NonProjectSpace_CombinesGlobalAndSpaceLocalGuidance()
    {
        await _projects.CreateAsync(new Project { Id = "personal", Name = "Personal", Kind = "personal" });
        await _documents.UpsertAsync(new Document
        {
            ProjectId = "personal",
            Slug = "personal-notes",
            Title = "Personal Notes",
            Content = "Personal guidance content",
            DocType = DocType.Note,
            Tags = ["personal"]
        });

        await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "_global",
            DocumentProjectId = "_global",
            DocumentSlug = "global-required",
            Importance = AgentGuidanceImportance.Required,
            SortOrder = 10
        });
        await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "personal",
            DocumentProjectId = "personal",
            DocumentSlug = "personal-notes",
            Importance = AgentGuidanceImportance.Important,
            SortOrder = 20
        });

        var resolved = await _guidance.ResolveAsync("personal");

        Assert.Equal("personal", resolved.ProjectId);
        Assert.Equal(2, resolved.Sources.Count);
        Assert.Equal("_global", resolved.Sources[0].ScopeProjectId);
        Assert.Equal("personal", resolved.Sources[1].ScopeProjectId);
        Assert.Contains("Global guidance", resolved.Content);
        Assert.Contains("Personal guidance content", resolved.Content);
    }

    [Fact]
    public async Task List_NonProjectSpace_ReturnsSpaceLocalEntries()
    {
        await _projects.CreateAsync(new Project { Id = "assistant", Name = "Assistant", Kind = "assistant" });
        await _documents.UpsertAsync(new Document
        {
            ProjectId = "assistant",
            Slug = "persona",
            Title = "Assistant Persona",
            Content = "Be helpful and concise.",
            DocType = DocType.Convention
        });

        await _guidance.UpsertAsync(new AgentGuidanceEntry
        {
            ProjectId = "assistant",
            DocumentProjectId = "assistant",
            DocumentSlug = "persona",
            Importance = AgentGuidanceImportance.Important,
            SortOrder = 0
        });

        var entries = await _guidance.ListAsync("assistant");
        var entry = Assert.Single(entries);
        Assert.Equal("assistant", entry.ProjectId);
        Assert.Equal("persona", entry.DocumentSlug);
    }
}
