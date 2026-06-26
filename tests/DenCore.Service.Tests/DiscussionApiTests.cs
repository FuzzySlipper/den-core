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

public sealed class DiscussionApiTests : IAsyncLifetime
{
    private readonly string _projectId = $"disc-api-test-{Guid.NewGuid():N}";
    private const string Slug = "discussion-doc";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private DiscussionAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new DiscussionAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(_projectId) is null)
            await projects.CreateAsync(new Project { Id = _projectId, Name = "Discussion API Test" });

        // Seed a document for discussion tests
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        await docs.UpsertAsync(new Document
        {
            ProjectId = _projectId,
            Slug = Slug,
            Title = "Discussion Test Document",
            Content = "# Hello discussions\n"
        });

        // Seed a second document for missing-doc tests
        await docs.UpsertAsync(new Document
        {
            ProjectId = _projectId,
            Slug = "other-doc",
            Title = "Other Document",
            Content = "# Other\n"
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Generic discussion-threads routes ──

    [Fact]
    public async Task PostAndGet_Thread_RoundTrips()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "review",
            title = "Review Thread",
            created_by = "test-agent"
        });
        createResponse.EnsureSuccessStatusCode();
        Assert.Equal(201, (int)createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal("document", created!.TargetType);
        Assert.Equal(_projectId, created.TargetProjectId);
        Assert.Equal(Slug, created.TargetSlug);
        Assert.Equal("review", created.ThreadKey);
        Assert.Equal("Review Thread", created.Title);
        Assert.Equal("open", created.Status);
        Assert.Equal("test-agent", created.CreatedBy);

        var getResponse = await _client.GetAsync($"/api/discussion-threads/{created.Id}");
        getResponse.EnsureSuccessStatusCode();

        var detail = await getResponse.Content.ReadFromJsonAsync<DiscussionDetailResponse>(JsonOpts);
        Assert.NotNull(detail);
        Assert.NotNull(detail.Thread);
        Assert.NotNull(detail.Comments);
        Assert.Equal(created.Id, detail.Thread.Id);
        Assert.Equal("Review Thread", detail.Thread.Title);
        Assert.Empty(detail.Comments);
    }

    [Fact]
    public async Task GetThread_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/discussion-threads/99999");
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task ListThreads_ByTarget_ReturnsFiltered()
    {
        await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "qa",
            title = "QA Thread",
            created_by = "tester"
        });

        var listResponse = await _client.GetAsync(
            $"/api/discussion-threads?targetType=document&targetProjectId={_projectId}&targetSlug={Slug}");
        listResponse.EnsureSuccessStatusCode();

        var list = await listResponse.Content.ReadFromJsonAsync<List<DiscussionThreadResponse>>(JsonOpts);
        Assert.NotNull(list);
        Assert.Contains(list!, t => t.ThreadKey == "qa");
    }

    [Fact]
    public async Task ListThreads_RequiresTargetParams()
    {
        var response = await _client.GetAsync("/api/discussion-threads");
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task ListThreads_FiltersByStatus()
    {
        await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = "other-doc",
            thread_key = "open-thread",
            title = "Open Thread",
            created_by = "agent"
        });

        var create2 = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = "other-doc",
            thread_key = "resolve-me",
            title = "Will Resolve",
            created_by = "agent"
        });
        var created2 = await create2.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        // Resolve the second thread
        var patchResponse = await _client.PatchAsJsonAsync($"/api/discussion-threads/{created2!.Id}", new
        {
            status = "resolved"
        });
        patchResponse.EnsureSuccessStatusCode();

        // List only open
        var openResponse = await _client.GetAsync(
            $"/api/discussion-threads?targetType=document&targetProjectId={_projectId}&targetSlug=other-doc&status=open");
        openResponse.EnsureSuccessStatusCode();
        var openList = await openResponse.Content.ReadFromJsonAsync<List<DiscussionThreadResponse>>(JsonOpts);
        Assert.NotNull(openList);
        Assert.Single(openList!);
        Assert.Equal("open-thread", openList![0].ThreadKey);

        // List only resolved
        var resolvedResponse = await _client.GetAsync(
            $"/api/discussion-threads?targetType=document&targetProjectId={_projectId}&targetSlug=other-doc&status=resolved");
        resolvedResponse.EnsureSuccessStatusCode();
        var resolvedList = await resolvedResponse.Content.ReadFromJsonAsync<List<DiscussionThreadResponse>>(JsonOpts);
        Assert.NotNull(resolvedList);
        Assert.Single(resolvedList!);
        Assert.Equal("resolve-me", resolvedList![0].ThreadKey);
    }

    [Fact]
    public async Task PostThread_DuplicateThreadKey_ReturnsExistingThread()
    {
        var payload = new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "stable-key",
            title = "Stable Thread",
            created_by = "agent"
        };

        var firstResponse = await _client.PostAsJsonAsync("/api/discussion-threads", payload);
        firstResponse.EnsureSuccessStatusCode();
        Assert.Equal(201, (int)firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        var secondResponse = await _client.PostAsJsonAsync("/api/discussion-threads", payload);
        secondResponse.EnsureSuccessStatusCode();
        Assert.Equal(200, (int)secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task CreateThread_MissingDocument_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = "nonexistent-doc",
            thread_key = "missing",
            title = "Missing Doc Thread",
            created_by = "agent"
        });
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task PostAndList_Comment_RoundTrips()
    {
        // Create a thread first
        var createResponse = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "comments-test",
            title = "Comments Test",
            created_by = "agent1"
        });
        var thread = await createResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        // Add a comment
        var commentResponse = await _client.PostAsJsonAsync($"/api/discussion-threads/{thread!.Id}/comments", new
        {
            body_markdown = "This is a comment",
            author_identity = "alice",
            comment_kind = "question"
        });
        commentResponse.EnsureSuccessStatusCode();
        Assert.Equal(201, (int)commentResponse.StatusCode);

        var comment = await commentResponse.Content.ReadFromJsonAsync<DiscussionCommentResponse>(JsonOpts);
        Assert.NotNull(comment);
        Assert.Equal(thread.Id, comment!.ThreadId);
        Assert.Equal("This is a comment", comment.BodyMarkdown);
        Assert.Equal("alice", comment.AuthorIdentity);
        Assert.Equal("question", comment.CommentKind);
        Assert.Equal("active", comment.Status);
        Assert.Null(comment.ParentCommentId);

        // Verify comment appears in thread detail
        var detailResponse = await _client.GetAsync($"/api/discussion-threads/{thread.Id}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<DiscussionDetailResponse>(JsonOpts);
        Assert.NotNull(detail);
        Assert.NotNull(detail.Comments);
        Assert.Single(detail.Comments);
        Assert.Equal("This is a comment", detail.Comments[0].BodyMarkdown);
    }

    [Fact]
    public async Task PostComment_WithParentPointer_CreatesReply()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "reply-test",
            title = "Reply Test",
            created_by = "agent1"
        });
        var thread = await createResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        var rootResponse = await _client.PostAsJsonAsync($"/api/discussion-threads/{thread!.Id}/comments", new
        {
            body_markdown = "Root comment",
            author_identity = "alice"
        });
        var root = await rootResponse.Content.ReadFromJsonAsync<DiscussionCommentResponse>(JsonOpts);

        var replyResponse = await _client.PostAsJsonAsync($"/api/discussion-threads/{thread.Id}/comments", new
        {
            body_markdown = "Reply comment",
            author_identity = "bob",
            parent_comment_id = root!.Id,
            mentions = new[] { "alice" },
            source_refs = new[] { new { type = "task", project_id = _projectId, id = 1678 } },
            metadata = new { client = "test" }
        });
        replyResponse.EnsureSuccessStatusCode();

        var reply = await replyResponse.Content.ReadFromJsonAsync<DiscussionCommentResponse>(JsonOpts);
        Assert.NotNull(reply);
        Assert.Equal(root.Id, reply!.ParentCommentId);
        Assert.Equal("Reply comment", reply.BodyMarkdown);
    }

    [Fact]
    public async Task AddComment_BadRequestOnMissingFields()
    {
        var threadResponse = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "missing-fields",
            title = "Missing Fields",
            created_by = "agent"
        });
        var thread = await threadResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        var response = await _client.PostAsJsonAsync($"/api/discussion-threads/{thread!.Id}/comments", new
        {
            body_markdown = "",
            author_identity = "alice"
        });
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task AddComment_NonexistentThread_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/discussion-threads/99999/comments", new
        {
            body_markdown = "comment",
            author_identity = "alice"
        });
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task PatchThread_UpdatesStatusAndResolution()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "patch-me",
            title = "Patch Test",
            created_by = "agent"
        });
        var thread = await createResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        var patchResponse = await _client.PatchAsJsonAsync($"/api/discussion-threads/{thread!.Id}", new
        {
            status = "resolved",
            title = "Resolved Thread",
            summary = "We agreed on the approach",
            resolution_summary = "Decision: use SQLite JSON columns"
        });
        patchResponse.EnsureSuccessStatusCode();

        var updated = await patchResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);
        Assert.NotNull(updated);
        Assert.Equal("resolved", updated!.Status);
        Assert.Equal("Resolved Thread", updated.Title);
        Assert.Equal("We agreed on the approach", updated.Summary);
        Assert.Equal("Decision: use SQLite JSON columns", updated.ResolutionSummary);
    }

    [Fact]
    public async Task PatchThread_Nonexistent_Returns404()
    {
        var response = await _client.PatchAsJsonAsync("/api/discussion-threads/99999", new
        {
            status = "resolved"
        });
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task PatchThread_InvalidStatus_Returns400()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "bogus-status",
            title = "Bogus Status",
            created_by = "agent"
        });
        var thread = await createResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);

        // The repository validates status — invalid status should throw
        var response = await _client.PatchAsJsonAsync($"/api/discussion-threads/{thread!.Id}", new
        {
            status = "bogus"
        });
        Assert.Equal(400, (int)response.StatusCode);
    }

    // ── Document convenience routes ──

    [Fact]
    public async Task GetDocument_DoesNotContainDiscussionData()
    {
        // Seed discussion on the document
        var threadResponse = await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = Slug,
            thread_key = "conceal-test",
            title = "Conceal Test",
            created_by = "agent"
        });
        var thread = await threadResponse.Content.ReadFromJsonAsync<DiscussionThreadResponse>(JsonOpts);
        await _client.PostAsJsonAsync($"/api/discussion-threads/{thread!.Id}/comments", new
        {
            body_markdown = "Hidden comment",
            author_identity = "alice"
        });

        // GET the document directly — should have no discussion fields
        var docResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents/{Slug}");
        docResponse.EnsureSuccessStatusCode();

        var docJson = await docResponse.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(docJson, JsonOpts);
        Assert.False(doc.TryGetProperty("discussion_threads", out _), "Document should not contain discussion_threads");
        Assert.False(doc.TryGetProperty("comments", out _), "Document should not contain comments");
        Assert.Equal("Discussion Test Document", doc.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetDocumentDiscussion_ReturnsThreadInfoWithoutComments()
    {
        // GET discussion for a document with no threads yet
        var response = await _client.GetAsync($"/api/projects/{_projectId}/documents/{Slug}/discussion");
        response.EnsureSuccessStatusCode();

        var detail = await response.Content.ReadFromJsonAsync<DiscussionDetailResponse>(JsonOpts);
        Assert.NotNull(detail);
        // No default thread exists yet — show empty info
        Assert.Null(detail.Thread);
        Assert.NotNull(detail.Comments);
        Assert.Empty(detail.Comments);
    }

    [Fact]
    public async Task PostDocumentComment_CreatesDefaultThreadAndComment()
    {
        var commentResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/documents/{Slug}/discussion/comments", new
            {
                body_markdown = "First document comment",
                author_identity = "bob",
                comment_kind = "comment"
            });
        commentResponse.EnsureSuccessStatusCode();
        Assert.Equal(201, (int)commentResponse.StatusCode);

        var comment = await commentResponse.Content.ReadFromJsonAsync<DiscussionCommentResponse>(JsonOpts);
        Assert.NotNull(comment);
        Assert.Equal("First document comment", comment!.BodyMarkdown);
        Assert.Equal("bob", comment.AuthorIdentity);
        Assert.Equal("comment", comment.CommentKind);

        // Now GET discussion should return the thread with the comment
        var discResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents/{Slug}/discussion");
        discResponse.EnsureSuccessStatusCode();

        var detail = await discResponse.Content.ReadFromJsonAsync<DiscussionDetailResponse>(JsonOpts);
        Assert.NotNull(detail);
        Assert.NotNull(detail.Comments);
        Assert.Single(detail.Comments);
        Assert.Equal("First document comment", detail.Comments[0].BodyMarkdown);
    }

    [Fact]
    public async Task PostDocumentComment_MissingDocument_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/documents/nonexistent-slug/discussion/comments", new
            {
                body_markdown = "Comment on missing doc",
                author_identity = "bob"
            });
        Assert.Equal(404, (int)response.StatusCode);

        // Verify no thread was created for the missing document
        var threadsResponse = await _client.GetAsync(
            $"/api/discussion-threads?targetType=document&targetProjectId={_projectId}&targetSlug=nonexistent-slug");
        var threads = await threadsResponse.Content.ReadFromJsonAsync<List<DiscussionThreadResponse>>(JsonOpts);
        Assert.NotNull(threads);
        Assert.Empty(threads!);
    }

    [Fact]
    public async Task PostDocumentComment_WithParentPointer_CreatesReplyInDefaultThread()
    {
        var rootResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/documents/{Slug}/discussion/comments", new
            {
                body_markdown = "Root document comment",
                author_identity = "alice"
            });
        var root = await rootResponse.Content.ReadFromJsonAsync<DiscussionCommentResponse>(JsonOpts);

        var replyResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/documents/{Slug}/discussion/comments", new
            {
                body_markdown = "Document reply",
                author_identity = "bob",
                parent_comment_id = root!.Id
            });
        replyResponse.EnsureSuccessStatusCode();

        var reply = await replyResponse.Content.ReadFromJsonAsync<DiscussionCommentResponse>(JsonOpts);
        Assert.NotNull(reply);
        Assert.Equal(root.Id, reply!.ParentCommentId);
    }

    [Fact]
    public async Task GetDocumentDiscussion_NotFound_Returns404()
    {
        var response = await _client.GetAsync($"/api/projects/{_projectId}/documents/no-such-slug/discussion");
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task GetDocumentDiscussionThreads_ListsThreads()
    {
        await _client.PostAsJsonAsync("/api/discussion-threads", new
        {
            target_type = "document",
            target_project_id = _projectId,
            target_slug = "other-doc",
            thread_key = "feedback",
            title = "Feedback",
            created_by = "agent1"
        });

        var listResponse = await _client.GetAsync($"/api/projects/{_projectId}/documents/other-doc/discussion/threads");
        listResponse.EnsureSuccessStatusCode();

        var list = await listResponse.Content.ReadFromJsonAsync<List<DiscussionThreadResponse>>(JsonOpts);
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("feedback", list![0].ThreadKey);
    }

    // ── Discussion stays out of message APIs ──

    [Fact]
    public async Task DiscussionComments_DoNotAppearInMessages()
    {
        // Add a comment via document discussion
        await _client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/documents/{Slug}/discussion/comments", new
            {
                body_markdown = "Should not be a message",
                author_identity = "alice"
            });

        // Verify message list is empty for this project
        var messagesResponse = await _client.GetAsync($"/api/projects/{_projectId}/messages");
        messagesResponse.EnsureSuccessStatusCode();

        var messages = await messagesResponse.Content.ReadFromJsonAsync<List<object>>(JsonOpts);
        Assert.NotNull(messages);
        Assert.Empty(messages!);
    }

    // ── Factory ──

    private sealed class DiscussionAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-discussion-api-{Guid.NewGuid()}.db");

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
