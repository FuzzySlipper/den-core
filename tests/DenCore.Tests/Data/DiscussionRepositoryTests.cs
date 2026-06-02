using DenCore.Data;
using DenCore.Models;

namespace DenCore.Tests.Data;

public class DiscussionRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private DiscussionRepository _repo = null!;
    private IDocumentRepository _documents = null!;
    private IProjectRepository _projects = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _documents = new DocumentRepository(_testDb.Db);
        _repo = new DiscussionRepository(_testDb.Db, _documents);
        _projects = new ProjectRepository(_testDb.Db);

        // Create a project to satisfy FK constraints
        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private async Task SeedDocumentAsync(string slug = "test-doc")
    {
        await _documents.UpsertAsync(new Document
        {
            ProjectId = "test-proj",
            Slug = slug,
            Title = "Test Document",
            Content = "# Hello"
        });
    }

    private static DiscussionCommentSummary AssertComment(
        DiscussionCommentSummary c,
        int expectedThreadId,
        int? expectedParentId,
        string expectedAuthor,
        string expectedKind = "comment")
    {
        Assert.True(c.Id > 0);
        Assert.Equal(expectedThreadId, c.ThreadId);
        Assert.Equal(expectedParentId, c.ParentCommentId);
        Assert.Equal(expectedAuthor, c.AuthorIdentity);
        Assert.Equal(expectedKind, c.CommentKind);
        Assert.Equal("active", c.Status);
        return c;
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 1: Fresh DB creates both tables
    // ──────────────────────────────────────────────
    [Fact]
    public async Task DbInitializer_CreatesDiscussionTables()
    {
        // The TestDb InitializeAsync() runs DatabaseInitializer which includes
        // the discussion_threads and discussion_comments tables. Verify they exist.
        await using var conn = await _testDb.Db.CreateConnectionAsync();

        await using var c1 = conn.CreateCommand();
        c1.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='discussion_threads'";
        Assert.Equal(1L, (await c1.ExecuteScalarAsync())!);

        await using var c2 = conn.CreateCommand();
        c2.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='discussion_comments'";
        Assert.Equal(1L, (await c2.ExecuteScalarAsync())!);

        // Verify indexes exist
        await using var c3 = conn.CreateCommand();
        c3.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_discussion_threads_target'";
        Assert.Equal(1L, (await c3.ExecuteScalarAsync())!);

        await using var cUnique = conn.CreateCommand();
        cUnique.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_discussion_threads_unique_target_key'";
        Assert.Equal(1L, (await cUnique.ExecuteScalarAsync())!);

        await using var c4 = conn.CreateCommand();
        c4.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_discussion_comments_thread'";
        Assert.Equal(1L, (await c4.ExecuteScalarAsync())!);

        // Missing index assertions per review R1677-2 (Finding 825)
        await using var cIdxStatus = conn.CreateCommand();
        cIdxStatus.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_discussion_threads_status'";
        Assert.Equal(1L, (await cIdxStatus.ExecuteScalarAsync())!);

        await using var cIdxParent = conn.CreateCommand();
        cIdxParent.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_discussion_comments_parent'";
        Assert.Equal(1L, (await cIdxParent.ExecuteScalarAsync())!);
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 2: Migration/ensure path is idempotent
    // ──────────────────────────────────────────────
    [Fact]
    public async Task DbInitializer_EnsureDiscussionSchema_Idempotent()
    {
        // Calling InitializeAsync again (or the migration method) should be safe.
        // Since TestDb already ran InitializeAsync, re-initializing should not throw.
        var dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-test-idempotent-{Guid.NewGuid()}.db");
        try
        {
            var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>();
            var init = new DatabaseInitializer(dbPath, logger);
            await init.InitializeAsync();

            // Second init — must not throw
            var init2 = new DatabaseInitializer(dbPath, logger);
            var ex = await Record.ExceptionAsync(() => init2.InitializeAsync());
            Assert.Null(ex);

            // Tables still intact
            var conn = new Microsoft.Data.Sqlite.SqliteConnection(init.ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='discussion_threads'";
            Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);
            await conn.DisposeAsync();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 3: Create/find default thread idempotently
    // ──────────────────────────────────────────────
    [Fact]
    public async Task GetOrCreateDefaultDocumentThread_CreatesIdempotently()
    {
        await SeedDocumentAsync();
        var createdBy = "test-agent";

        var first = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", createdBy);
        Assert.True(first.Id > 0);
        Assert.Equal("open", first.Status);
        Assert.Equal("document", first.TargetType);
        Assert.Equal("test-proj", first.TargetProjectId);
        Assert.Equal("test-doc", first.TargetSlug);
        Assert.Equal("default", first.ThreadKey);
        Assert.Equal(createdBy, first.CreatedBy);

        var second = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", createdBy);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 4: Missing document fails closed
    // ──────────────────────────────────────────────
    [Fact]
    public async Task CreateDocumentThreadAsync_MissingDocument_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.CreateDocumentThreadAsync(
                "test-proj", "nonexistent-doc", "default",
                "Bad doc", "agent"));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task GetOrCreateDefaultDocumentThread_MissingDocument_Throws()
    {
        // GetOrCreateDefault also validates document existence via CreateDocumentThreadAsync
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "nonexistent-doc", "agent"));

        Assert.Contains("not found", ex.Message);
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 5: Root comments and replies in chronological order
    // ──────────────────────────────────────────────
    [Fact]
    public async Task AddCommentAndList_ReturnsChronologicalOrder()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");

        var c1 = await _repo.AddCommentAsync(thread.Id, "First comment", "alice");
        var c2 = await _repo.AddCommentAsync(thread.Id, "Second comment", "bob");
        var c3 = await _repo.AddCommentAsync(thread.Id, "Third comment", "charlie");

        var comments = await _repo.ListCommentsAsync(thread.Id);
        Assert.Equal(3, comments.Count);

        AssertComment(comments[0], thread.Id, null, "alice");
        Assert.Equal("First comment", comments[0].BodyMarkdown);

        AssertComment(comments[1], thread.Id, null, "bob");
        Assert.Equal("Second comment", comments[1].BodyMarkdown);

        AssertComment(comments[2], thread.Id, null, "charlie");
        Assert.Equal("Third comment", comments[2].BodyMarkdown);
    }

    [Fact]
    public async Task AddReply_ReturnsReplyWithParentPointer()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");

        var root = await _repo.AddCommentAsync(thread.Id, "Root", "alice");
        var reply = await _repo.AddReplyAsync(thread.Id, root.Id, "Reply", "bob");

        var comments = await _repo.ListCommentsAsync(thread.Id);
        Assert.Equal(2, comments.Count);

        AssertComment(comments[0], thread.Id, null, "alice");
        AssertComment(comments[1], thread.Id, root.Id, "bob");
        Assert.Equal("Reply", comments[1].BodyMarkdown);
    }

    [Fact]
    public async Task AddComment_UpdatesLastCommentAt()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");

        Assert.Null(thread.LastCommentAt);

        await _repo.AddCommentAsync(thread.Id, "New comment", "alice");

        var refreshed = await _repo.GetThreadByIdAsync(thread.Id);
        Assert.NotNull(refreshed);
        Assert.NotNull(refreshed!.LastCommentAt);
    }

    [Fact]
    public async Task AddComment_WrongThreadId_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.AddCommentAsync(9999, "comment", "agent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task AddReply_ParentNotInThread_Throws()
    {
        await SeedDocumentAsync();
        var t1 = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");

        // Seed a second document and thread
        await _documents.UpsertAsync(new Document
        {
            ProjectId = "test-proj", Slug = "doc2", Title = "Doc 2", Content = "# Two"
        });
        var t2 = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "doc2", "agent1");

        var c1 = await _repo.AddCommentAsync(t2.Id, "From other thread", "alice");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.AddReplyAsync(t1.Id, c1.Id, "should fail", "bob"));
        Assert.Contains("does not belong", ex.Message);
    }

    // ──────────────────────────────────────────────
    // Empty/whitespace input validation — review R1677-1 (Finding 824)
    // ──────────────────────────────────────────────
    [Fact]
    public async Task AddComment_NullBody_Throws()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.AddCommentAsync(thread.Id, null!, "agent"));
        Assert.Contains("bodyMarkdown", ex.Message);
    }

    [Fact]
    public async Task AddComment_EmptyBody_Throws()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.AddCommentAsync(thread.Id, "", "agent"));
        Assert.Contains("bodyMarkdown", ex.Message);
    }

    [Fact]
    public async Task AddComment_WhitespaceBody_Throws()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.AddCommentAsync(thread.Id, "   ", "agent"));
        Assert.Contains("bodyMarkdown", ex.Message);
    }

    [Fact]
    public async Task AddReply_NullBody_Throws()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");
        var root = await _repo.AddCommentAsync(thread.Id, "Root", "alice");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.AddReplyAsync(thread.Id, root.Id, null!, "bob"));
        Assert.Contains("bodyMarkdown", ex.Message);
    }

    [Fact]
    public async Task AddReply_EmptyBody_Throws()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");
        var root = await _repo.AddCommentAsync(thread.Id, "Root", "alice");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.AddReplyAsync(thread.Id, root.Id, "", "bob"));
        Assert.Contains("bodyMarkdown", ex.Message);
    }

    [Fact]
    public async Task AddReply_WhitespaceBody_Throws()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");
        var root = await _repo.AddCommentAsync(thread.Id, "Root", "alice");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.AddReplyAsync(thread.Id, root.Id, "   ", "bob"));
        Assert.Contains("bodyMarkdown", ex.Message);
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 6: Thread status/summary/resolution round-trip
    // ──────────────────────────────────────────────
    [Fact]
    public async Task UpdateThread_RoundTripsStatusAndSummary()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");

        thread.Status = "resolved";
        thread.Summary = "We agreed on the approach";
        thread.ResolutionSummary = "Decision: use SQLite JSON columns";

        var updated = await _repo.UpdateThreadAsync(thread);
        Assert.Equal("resolved", updated.Status);
        Assert.Equal("We agreed on the approach", updated.Summary);
        Assert.Equal("Decision: use SQLite JSON columns", updated.ResolutionSummary);

        // Re-fetch
        var fetched = await _repo.GetThreadByIdAsync(thread.Id);
        Assert.NotNull(fetched);
        Assert.Equal("resolved", fetched!.Status);
        Assert.Equal("We agreed on the approach", fetched.Summary);
        Assert.Equal("Decision: use SQLite JSON columns", fetched.ResolutionSummary);
    }

    [Fact]
    public async Task UpdateThread_InvalidStatus_DbRejects()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");

        // The DB CHECK constraint on discussion_threads.status rejects invalid values
        await using var conn = await _testDb.Db.CreateConnectionAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE discussion_threads
            SET status = 'bogus'
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", thread.Id);
        var ex = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            cmd.ExecuteNonQueryAsync());
        Assert.Contains("CHECK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 7: DocumentRepository remains discussion-free
    // ──────────────────────────────────────────────
    [Fact]
    public async Task DocumentGet_DoesNotReturnThreadData()
    {
        await SeedDocumentAsync();
        var doc = await _documents.GetAsync("test-proj", "test-doc");
        Assert.NotNull(doc);
        // Document model has no discussion-related properties
        Assert.Equal("Test Document", doc!.Title);
    }

    [Fact]
    public async Task DocumentList_DoesNotIncludeThreadData()
    {
        await SeedDocumentAsync();
        var list = await _documents.ListAsync(projectId: "test-proj");
        Assert.Single(list);
        // DocumentSummary has no discussion fields
        Assert.Equal("test-doc", list[0].Slug);
    }

    [Fact]
    public async Task SearchAsync_DoesNotIncludeThreadData()
    {
        await SeedDocumentAsync("search-disc-free");
        Assert.NotNull(await _documents.GetAsync("test-proj", "search-disc-free"));

        // Create discussion data alongside the document — search should stay clean
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync(
            "test-proj", "search-disc-free", "agent");
        await _repo.AddCommentAsync(thread.Id, "Architecture decision discussion", "alice");

        // Search for document content, not discussion content
        var results = await _documents.SearchAsync("Hello");
        var match = results.FirstOrDefault(r => r.Slug == "search-disc-free");
        Assert.NotNull(match);
        // DocumentSearchResult has no discussion-related properties
        Assert.Equal("Test Document", match!.Title);
    }

    // ──────────────────────────────────────────────
    // Acceptance criteria 8 & 9: No general messages created
    // ──────────────────────────────────────────────
    [Fact]
    public async Task DiscussionOperations_DoNotCreateMessageRows()
    {
        await SeedDocumentAsync();

        await using var conn = await _testDb.Db.CreateConnectionAsync();
        async Task<int> MessageCount()
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM messages";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        var before = await MessageCount();

        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");
        await _repo.AddCommentAsync(thread.Id, "A comment", "alice");
        await _repo.AddReplyAsync(thread.Id, 1, "A reply", "bob");
        await _repo.AddCommentAsync(thread.Id, "Another", "charlie", commentKind: "question");

        var after = await MessageCount();
        Assert.Equal(before, after);
    }

    // ──────────────────────────────────────────────
    // Comment kind validation
    // ──────────────────────────────────────────────
    [Fact]
    public async Task AddComment_InvalidCommentKind_DbRejects()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent1");

        // Trying to pass an invalid kind: the DB CHECK should reject it
        // But our repository validates before hitting DB.
        // Let's verify that at least the DB constraint is there.
        await using var conn = await _testDb.Db.CreateConnectionAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discussion_comments (thread_id, author_identity, body_markdown, comment_kind)
            VALUES (@threadId, 'agent', 'test', 'invalid_kind')
            """;
        cmd.Parameters.AddWithValue("@threadId", thread.Id);
        var ex = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            cmd.ExecuteNonQueryAsync());
        Assert.Contains("CHECK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateDocumentThread_SupportsMetadata()
    {
        await SeedDocumentAsync();
        var meta = """{"source":"webhook","priority":"high"}""";
        var thread = await _repo.CreateDocumentThreadAsync(
            "test-proj", "test-doc", "feedback", "Feedback Thread", "agent",
            summary: "Collecting feedback", metadataJson: meta);

        Assert.Equal("feedback", thread.ThreadKey);
        Assert.Equal("Feedback Thread", thread.Title);
        Assert.Equal(meta, thread.MetadataJson);
    }

    [Fact]
    public async Task ListDocumentThreads_ReturnsAllThreads()
    {
        await SeedDocumentAsync();
        var t1 = await _repo.CreateDocumentThreadAsync("test-proj", "test-doc", "review", "Review", "agent1");
        var t2 = await _repo.CreateDocumentThreadAsync("test-proj", "test-doc", "questions", "Questions", "agent2");

        var threads = await _repo.ListDocumentThreadsAsync("test-proj", "test-doc");
        Assert.Equal(2, threads.Count);
        Assert.Contains(threads, t => t.ThreadKey == "review");
        Assert.Contains(threads, t => t.ThreadKey == "questions");
    }

    [Fact]
    public async Task GetCommentById_ReturnsComment()
    {
        await SeedDocumentAsync();
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("test-proj", "test-doc", "agent");
        var comment = await _repo.AddCommentAsync(thread.Id, "Check me", "alice",
            commentKind: "answer", mentionsJson: """["bob"]""", sourceRefsJson: """["ref1"]""");

        var fetched = await _repo.GetCommentByIdAsync(comment.Id);
        Assert.NotNull(fetched);
        Assert.Equal(comment.Id, fetched!.Id);
        Assert.Equal("Check me", fetched.BodyMarkdown);
        Assert.Equal("answer", fetched.CommentKind);
        Assert.Equal("active", fetched.Status);
        Assert.Equal("""["bob"]""", fetched.MentionsJson);
        Assert.Equal("""["ref1"]""", fetched.SourceRefsJson);
    }

    [Fact]
    public async Task ThreadForeignKey_CascadesOnProjectDelete()
    {
        // Create a separate project and document for this isolation test
        await _projects.CreateAsync(new Project { Id = "cascade-proj", Name = "Cascade Test" });
        await _documents.UpsertAsync(new Document
        {
            ProjectId = "cascade-proj",
            Slug = "cascade-doc",
            Title = "Cascade Doc",
            Content = "# Cascade"
        });
        var thread = await _repo.GetOrCreateDefaultDocumentThreadAsync("cascade-proj", "cascade-doc", "agent");
        await _repo.AddCommentAsync(thread.Id, "Will be deleted", "agent");

        // Delete the project — FK CASCADE should remove threads
        await _projects.DeleteSpaceAsync("cascade-proj");

        var fetched = await _repo.GetThreadByIdAsync(thread.Id);
        Assert.Null(fetched);
    }
}
