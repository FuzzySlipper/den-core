using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenCore.Service.Tests;

public sealed class DiscussionToolsTests : IAsyncLifetime
{
    private readonly string _projectId = $"discussion-tools-test-{Guid.NewGuid():N}";
    private DiscussionToolsAppFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new DiscussionToolsAppFactory();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = _projectId, Name = "Discussion Tools Test" });

        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        await docs.UpsertAsync(new Document
        {
            ProjectId = _projectId,
            Slug = "tool-doc",
            Title = "Tool Doc",
            Content = "# Tool Doc\n\nCanonical body."
        });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetDocument_RemainsCanonicalAfterDiscussionComment()
    {
        using var scope = _factory.Services.CreateScope();
        var discussions = scope.ServiceProvider.GetRequiredService<IDiscussionRepository>();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        await DiscussionTools.CommentOnDocument(
            discussions,
            docs,
            _projectId,
            "tool-doc",
            "tester",
            "Discussion comment",
            verbose: true);

        var documentJson = await DocumentTools.GetDocument(docs, _projectId, "tool-doc");
        using var parsed = JsonDocument.Parse(documentJson);
        var root = parsed.RootElement;

        Assert.Equal("tool-doc", root.GetProperty("slug").GetString());
        Assert.False(root.TryGetProperty("discussion_threads", out _));
        Assert.False(root.TryGetProperty("comments", out _));
        Assert.False(root.TryGetProperty("body_markdown", out _));
    }

    [Fact]
    public async Task CommentTools_AcceptMentionsAndStructuredSourceRefs()
    {
        using var scope = _factory.Services.CreateScope();
        var discussions = scope.ServiceProvider.GetRequiredService<IDiscussionRepository>();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        var rootJson = await DiscussionTools.CommentOnDocument(
            discussions,
            docs,
            _projectId,
            "tool-doc",
            "tester",
            "Root comment",
            mentions: new[] { "den-mcp-runner" },
            source_refs: new { type = "task", project_id = _projectId, id = 1679 },
            verbose: true);
        var root = JsonSerializer.Deserialize<DiscussionComment>(rootJson, JsonOpts.Default);
        Assert.NotNull(root);
        Assert.Contains("den-mcp-runner", root!.MentionsJson);
        Assert.Contains("task", root.SourceRefsJson);

        var replyJson = await DiscussionTools.CreateDiscussionComment(
            discussions,
            root.ThreadId,
            "reviewer",
            "Reply comment",
            parent_comment_id: root.Id,
            source_refs: new[] { new { type = "comment", id = root.Id } },
            verbose: true);
        var reply = JsonSerializer.Deserialize<DiscussionComment>(replyJson, JsonOpts.Default);
        Assert.NotNull(reply);
        Assert.Equal(root.Id, reply!.ParentCommentId);
        Assert.Contains("comment", reply.SourceRefsJson);
    }

    [Fact]
    public async Task AnchorParameter_UsesSectionThreadKey()
    {
        using var scope = _factory.Services.CreateScope();
        var discussions = scope.ServiceProvider.GetRequiredService<IDiscussionRepository>();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        await DiscussionTools.CommentOnDocument(
            discussions,
            docs,
            _projectId,
            "tool-doc",
            "tester",
            "Anchored comment",
            anchor: "routing",
            verbose: true);

        var threads = await discussions.ListDocumentThreadsAsync(_projectId, "tool-doc");
        Assert.Contains(threads, t => t.ThreadKey == "section:routing");

        var anchoredJson = await DiscussionTools.GetDocumentDiscussion(
            discussions,
            docs,
            _projectId,
            "tool-doc",
            anchor: "routing",
            verbose: true);
        using var parsed = JsonDocument.Parse(anchoredJson);
        var defaultThread = parsed.RootElement.GetProperty("default_thread");
        Assert.Equal("section:routing", defaultThread.GetProperty("thread_key").GetString());
    }

    private sealed class DiscussionToolsAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-core-discussion-tools-{Guid.NewGuid():N}.db");

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
