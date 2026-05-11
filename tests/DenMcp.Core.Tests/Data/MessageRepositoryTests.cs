using DenMcp.Core.Data;
using DenMcp.Core.Models;
using System.Text.Json;

namespace DenMcp.Core.Tests.Data;

public class MessageRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private MessageRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new MessageRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task SendAndGet_RoundTrips()
    {
        var msg = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "claude-code", Content = "Hello!" });
        Assert.True(msg.Id > 0);
        Assert.Equal(MessageIntent.General, msg.Intent);

        var messages = await _repo.GetMessagesAsync("proj");
        Assert.Single(messages);
        Assert.Equal("Hello!", messages[0].Content);
        Assert.Equal(MessageIntent.General, messages[0].Intent);
    }

    [Fact]
    public async Task GetMessages_FiltersByUnread()
    {
        var m1 = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "Msg 1" });
        var m2 = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "Msg 2" });
        await _repo.MarkReadAsync("claude-code", [m1.Id]);

        var unread = await _repo.GetMessagesAsync("proj", unreadFor: "claude-code");
        Assert.Single(unread);
        Assert.Equal("Msg 2", unread[0].Content);
    }

    [Fact]
    public async Task GetMessages_ExcludesOwnMessages()
    {
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "claude-code", Content = "My own msg" });
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "From codex" });

        var unread = await _repo.GetMessagesAsync("proj", unreadFor: "claude-code");
        Assert.Single(unread);
        Assert.Equal("From codex", unread[0].Content);
    }

    [Fact]
    public async Task Threading_Works()
    {
        var root = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "alice", Content = "Thread root" });
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "bob", Content = "Reply 1", ThreadId = root.Id });
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "alice", Content = "Reply 2", ThreadId = root.Id });

        var thread = await _repo.GetThreadAsync(root.Id);
        Assert.Equal("Thread root", thread.Root.Content);
        Assert.Equal(2, thread.Replies.Count);
        Assert.Equal("Reply 1", thread.Replies[0].Content);
        Assert.Equal("Reply 2", thread.Replies[1].Content);
    }

    [Fact]
    public async Task GetFeedAsync_GroupsRepliesUnderRootAndKeepsStandaloneMessages()
    {
        var olderRoot = await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "alice",
            Content = "Older thread root"
        });
        var standalone = await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "carol",
            Content = "Standalone update"
        });
        await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "bob",
            Content = "Latest reply in older thread",
            ThreadId = olderRoot.Id
        });

        var feed = await _repo.GetFeedAsync("proj");

        Assert.Equal(2, feed.Count);

        var threadedItem = Assert.Single(feed, item => item.RootMessage.Id == olderRoot.Id);
        Assert.Equal("Latest reply in older thread", threadedItem.LatestMessage.Content);
        Assert.Equal(1, threadedItem.ReplyCount);

        var standaloneItem = Assert.Single(feed, item => item.RootMessage.Id == standalone.Id);
        Assert.Equal(0, standaloneItem.ReplyCount);
        Assert.Equal(standalone.Id, standaloneItem.LatestMessage.Id);
    }

    [Fact]
    public async Task GetFeedAsync_UsesRootMessageForThreadSummary()
    {
        var root = await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "alice",
            Content = "Planning summary"
        });
        var reply = await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "bob",
            Content = "Follow-up question",
            ThreadId = root.Id
        });

        var feed = await _repo.GetFeedAsync("proj");

        var item = Assert.Single(feed);
        Assert.Equal(root.Id, item.RootMessage.Id);
        Assert.Equal(reply.Id, item.LatestMessage.Id);
        Assert.Equal("Planning summary", item.RootMessage.Content);
        Assert.Equal("Follow-up question", item.LatestMessage.Content);
    }

    [Fact]
    public async Task GetFeedAsync_FiltersByIntentWhileKeepingThreadRootContext()
    {
        var root = await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "alice",
            Content = "General thread root"
        });
        await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "bob",
            Content = "Review feedback reply",
            ThreadId = root.Id,
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"review_feedback","recipient":"claude-code"}""")
        });
        await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "carol",
            Content = "Standalone note",
            Intent = MessageIntent.Note
        });

        var feed = await _repo.GetFeedAsync("proj", intent: MessageIntent.ReviewFeedback);

        var item = Assert.Single(feed);
        Assert.Equal(root.Id, item.RootMessage.Id);
        Assert.Equal("General thread root", item.RootMessage.Content);
        Assert.Equal("Review feedback reply", item.LatestMessage.Content);
        Assert.Equal(0, item.ReplyCount);
    }

    [Fact]
    public async Task MarkRead_IsIdempotent()
    {
        var msg = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "Test" });
        var count1 = await _repo.MarkReadAsync("claude-code", [msg.Id]);
        var count2 = await _repo.MarkReadAsync("claude-code", [msg.Id]);

        Assert.Equal(1, count1);
        Assert.Equal(0, count2); // already read
    }

    [Fact]
    public async Task CreateAsync_DerivesIntentFromLegacyMetadataType()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"merge_request","recipient":"codex"}""");

        var msg = await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "claude-code",
            Content = "Approved for merge",
            Metadata = metadata
        });

        Assert.Equal(MessageIntent.ReviewApproval, msg.Intent);
    }

    [Fact]
    public async Task GetMessages_FiltersByIntent()
    {
        await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "General note",
            Intent = MessageIntent.Note
        });
        await _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Review feedback",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"review_feedback","recipient":"claude-code"}""")
        });

        var notes = await _repo.GetMessagesAsync("proj", intent: MessageIntent.Note);
        var feedback = await _repo.GetMessagesAsync("proj", intent: MessageIntent.ReviewFeedback);

        Assert.Single(notes);
        Assert.Equal("General note", notes[0].Content);
        Assert.Equal(MessageIntent.Note, notes[0].Intent);

        Assert.Single(feedback);
        Assert.Equal("Review feedback", feedback[0].Content);
        Assert.Equal(MessageIntent.ReviewFeedback, feedback[0].Intent);
    }

    [Fact]
    public async Task CreateAsync_RejectsConflictingIntentAndLegacyMetadataType()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"review_feedback","recipient":"claude-code"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Conflicting intent",
            Intent = MessageIntent.ReviewRequest,
            Metadata = metadata
        }));

        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
