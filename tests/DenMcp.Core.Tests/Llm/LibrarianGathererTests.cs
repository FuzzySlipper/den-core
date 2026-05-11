using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Llm;

public class LibrarianGathererTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TaskRepository _taskRepo = null!;
    private DocumentRepository _docRepo = null!;
    private MessageRepository _msgRepo = null!;
    private ProjectRepository _projRepo = null!;
    private LibrarianGatherer _gatherer = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _taskRepo = new TaskRepository(_testDb.Db);
        _docRepo = new DocumentRepository(_testDb.Db);
        _msgRepo = new MessageRepository(_testDb.Db);
        _projRepo = new ProjectRepository(_testDb.Db);
        _gatherer = new LibrarianGatherer(_taskRepo, _docRepo, _msgRepo);

        await _projRepo.CreateAsync(new Project { Id = "proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task Gather_WithTaskId_IncludesTaskContext()
    {
        var task = await _taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Implement FTS search",
            Description = "Add full-text search to documents",
            Tags = ["search", "core"]
        });

        var ctx = await _gatherer.GatherAsync("proj", "working on search", task.Id);

        Assert.Contains("## Task Context", ctx.FormattedText);
        Assert.Contains($"### Task #{task.Id}: Implement FTS search", ctx.FormattedText);
        Assert.Contains("Add full-text search to documents", ctx.FormattedText);
        Assert.Contains("search, core", ctx.FormattedText);
        Assert.True(ctx.EstimatedTokens > 0);
    }

    [Fact]
    public async Task Gather_WithTaskId_IncludesParent()
    {
        var parent = await _taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Document Storage Feature"
        });
        var child = await _taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Add FTS5 index",
            ParentId = parent.Id
        });

        var ctx = await _gatherer.GatherAsync("proj", "fts index", child.Id);

        Assert.Contains($"### Parent Task #{parent.Id}: Document Storage Feature", ctx.FormattedText);
    }

    [Fact]
    public async Task Gather_WithTaskId_IncludesSubtasksAndDependencies()
    {
        var dep = await _taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Create schema"
        });
        var task = await _taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Build repositories"
        }, dependsOn: [dep.Id]);
        var sub = await _taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Task repository",
            ParentId = task.Id
        });

        var ctx = await _gatherer.GatherAsync("proj", "repositories", task.Id);

        Assert.Contains("### Subtasks", ctx.FormattedText);
        Assert.Contains($"#{sub.Id}: Task repository", ctx.FormattedText);
        Assert.Contains("### Dependencies", ctx.FormattedText);
        Assert.Contains($"#{dep.Id}: Create schema", ctx.FormattedText);
    }

    [Fact]
    public async Task Gather_FindsMatchingDocuments()
    {
        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "fts-spec",
            Title = "FTS5 Search Specification",
            Content = "The search system uses SQLite FTS5 with porter stemmer.",
            DocType = DocType.Spec
        });

        var ctx = await _gatherer.GatherAsync("proj", "search specification");

        Assert.Contains("## Relevant Documents", ctx.FormattedText);
        Assert.Contains("[doc: proj/fts-spec]", ctx.FormattedText);
        Assert.Contains("FTS5 Search Specification", ctx.FormattedText);
    }

    [Fact]
    public async Task Gather_IncludesGlobalDocuments()
    {
        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "_global",
            Slug = "conventions",
            Title = "Coding Conventions",
            Content = "All SQL must use parameterized queries for safety.",
            DocType = DocType.Convention
        });

        var ctx = await _gatherer.GatherAsync("proj", "SQL parameterized queries", includeGlobal: true);

        Assert.Contains("[doc: _global/conventions]", ctx.FormattedText);
    }

    [Fact]
    public async Task Gather_ExcludesGlobalDocuments_WhenDisabled()
    {
        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "_global",
            Slug = "excluded",
            Title = "Should Not Appear",
            Content = "This unique searchable content xyz123.",
            DocType = DocType.Reference
        });

        var ctx = await _gatherer.GatherAsync("proj", "xyz123", includeGlobal: false);

        Assert.DoesNotContain("_global/excluded", ctx.FormattedText);
    }

    [Fact]
    public async Task Gather_IncludesRecentProjectMessages()
    {
        await _msgRepo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "orchestrator",
            Content = "Merge freeze starts Thursday"
        });

        var ctx = await _gatherer.GatherAsync("proj", "anything");

        Assert.Contains("## Recent Project Messages", ctx.FormattedText);
        Assert.Contains("orchestrator", ctx.FormattedText);
        Assert.Contains("Merge freeze starts Thursday", ctx.FormattedText);
    }

    [Fact]
    public async Task Gather_RespectsTokenBudget()
    {
        // Create a large document to exceed a small budget
        var largeContent = string.Join(" ", Enumerable.Repeat("searchable content here repeated", 500));
        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "big-doc",
            Title = "Large Doc",
            Content = largeContent,
            DocType = DocType.Reference
        });

        var ctx = await _gatherer.GatherAsync("proj", "searchable content", tokenBudget: 100);

        // Should stay within budget (rough estimate)
        Assert.True(ctx.EstimatedTokens <= 120); // small tolerance
    }

    [Fact]
    public async Task Gather_EmptyProject_ReturnsEmptyContext()
    {
        await _projRepo.CreateAsync(new Project { Id = "empty", Name = "Empty" });

        var ctx = await _gatherer.GatherAsync("empty", "anything");

        Assert.True(ctx.EstimatedTokens == 0 || ctx.FormattedText.Length < 10);
    }

    [Fact]
    public async Task Gather_NoTaskId_UsesQueryAlone()
    {
        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "arch-doc",
            Title = "Architecture Overview",
            Content = "The system uses a layered architecture with Core and Server.",
            DocType = DocType.Adr
        });

        var ctx = await _gatherer.GatherAsync("proj", "architecture layered");

        Assert.DoesNotContain("## Task Context", ctx.FormattedText);
        Assert.Contains("## Relevant Documents", ctx.FormattedText);
        Assert.Contains("[doc: proj/arch-doc]", ctx.FormattedText);
    }

    [Fact]
    public void EstimateTokens_RoughCharDiv4()
    {
        Assert.Equal(25, LibrarianGatherer.EstimateTokens(new string('x', 100)));
        Assert.Equal(0, LibrarianGatherer.EstimateTokens(""));
    }

    [Fact]
    public async Task Gather_NonProjectSpace_IncludesDocsTasksMessages()
    {
        await _projRepo.CreateAsync(new Project { Id = "personal", Name = "Personal", Kind = "personal" });

        var task = await _taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "personal",
            Title = "Buy groceries",
            Description = "Milk, eggs, bread"
        });

        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "personal",
            Slug = "recipes",
            Title = "Favorite Recipes",
            Content = "Pancakes require flour, eggs, and milk.",
            DocType = DocType.Note
        });

        await _msgRepo.CreateAsync(new Message
        {
            ProjectId = "personal",
            Sender = "user",
            Content = "Remember to buy organic"
        });

        var ctx = await _gatherer.GatherAsync("personal", "groceries and recipes", task.Id);

        Assert.Contains("## Task Context", ctx.FormattedText);
        Assert.Contains("Buy groceries", ctx.FormattedText);
        Assert.Contains("## Relevant Documents", ctx.FormattedText);
        Assert.Contains("[doc: personal/recipes]", ctx.FormattedText);
        Assert.Contains("## Recent Project Messages", ctx.FormattedText);
        Assert.Contains("Remember to buy organic", ctx.FormattedText);
    }

    [Fact]
    public async Task Gather_NonProjectSpace_WithGlobalDocs()
    {
        await _projRepo.CreateAsync(new Project { Id = "assistant", Name = "Assistant", Kind = "assistant" });

        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "_global",
            Slug = "assistant-conventions",
            Title = "Assistant Conventions",
            Content = "Always greet users politely.",
            DocType = DocType.Convention
        });

        await _docRepo.UpsertAsync(new Document
        {
            ProjectId = "assistant",
            Slug = "persona",
            Title = "Assistant Persona",
            Content = "Helpful and concise.",
            DocType = DocType.Note
        });

        var ctx = await _gatherer.GatherAsync("assistant", "assistant behavior", includeGlobal: true);

        Assert.Contains("[doc: assistant/persona]", ctx.FormattedText);
        Assert.Contains("[doc: _global/assistant-conventions]", ctx.FormattedText);
    }
}
