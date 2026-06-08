using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using DenCore.Service.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Service.Tests;

public class MessageToolsTests
{
    private class FakeMessageRepo : IMessageRepository
    {
        private readonly List<Message> _messages = [];
        public Message? LastCreated { get; private set; }

        public Task<Message> CreateAsync(Message message)
        {
            var resolvedIntent = MessageIntentCompatibility.ResolveWriteIntent(message.Intent, message.Metadata);
            var created = new Message
            {
                Id = _messages.Count + 1,
                ProjectId = message.ProjectId,
                Sender = message.Sender,
                Content = message.Content,
                TaskId = message.TaskId,
                ThreadId = message.ThreadId,
                Intent = resolvedIntent,
                Metadata = message.Metadata,
                CreatedAt = DateTime.UtcNow
            };
            _messages.Add(created);
            LastCreated = created;
            return Task.FromResult(created);
        }

        public Task<Message?> GetByIdAsync(int id) => Task.FromResult<Message?>(null);
        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null, DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null)
            => Task.FromResult(new List<Message>());
        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null)
            => Task.FromResult(new List<MessageFeedItem>());
        public Task<DenCore.Models.Thread> GetThreadAsync(int threadId) => throw new NotSupportedException();
        public Task<int> MarkReadAsync(string agent, int[] messageIds) => Task.FromResult(0);
        public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(string? projectId = null, int? taskId = null, string? sender = null, string? metadataType = null, string? urgency = null, bool? isRead = null, string? readForAgent = null, int limit = 20, int offset = 0)
            => Task.FromResult(new List<NotificationFeedItem>());
        public Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds) => Task.FromResult(0);
        public Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null) => Task.FromResult(0);
        public Task<WaitForMessagesResult> WaitForMessagesAsync(string projectId, string unreadFor, int timeoutMs = 30000, int limit = 20, int? cursorMessageId = null) => throw new NotSupportedException();
    }

    private class FakeDispatchDetection : IDispatchDetectionService
    {
        public Task OnMessageCreatedAsync(Message message) => Task.CompletedTask;
        public Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy) => Task.CompletedTask;
    }

    [Fact]
    public async Task SendMessage_UnknownIntent_StoresGeneralAndPreservesRequestedIntent()
    {
        var repo = new FakeMessageRepo();
        var detection = new FakeDispatchDetection();

        await MessageTools.SendMessage(
            repo, detection, NullLogger<MessageTools>.Instance,
            project_id: "proj", sender: "codex", content: "Planning update",
            intent: "planning_update");

        Assert.NotNull(repo.LastCreated);
        Assert.Equal(MessageIntent.General, repo.LastCreated!.Intent);
        Assert.NotNull(repo.LastCreated.Metadata);
        var meta = repo.LastCreated.Metadata!.Value;
        Assert.True(meta.TryGetProperty("requested_intent", out var ri));
        Assert.Equal("planning_update", ri.GetString());
    }

    [Fact]
    public async Task SendMessage_CanonicalIntent_BehaviorUnchanged()
    {
        var repo = new FakeMessageRepo();
        var detection = new FakeDispatchDetection();

        await MessageTools.SendMessage(
            repo, detection, NullLogger<MessageTools>.Instance,
            project_id: "proj", sender: "codex", content: "Status update",
            intent: "status_update");

        Assert.NotNull(repo.LastCreated);
        Assert.Equal(MessageIntent.StatusUpdate, repo.LastCreated!.Intent);
    }

    [Fact]
    public async Task SendMessage_MissingIntent_DerivesFromMetadataType()
    {
        var repo = new FakeMessageRepo();
        var detection = new FakeDispatchDetection();
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"review_request","recipient":"pi"}""");

        await MessageTools.SendMessage(
            repo, detection, NullLogger<MessageTools>.Instance,
            project_id: "proj", sender: "codex", content: "Please review",
            metadata: metadata);

        Assert.NotNull(repo.LastCreated);
        Assert.Equal(MessageIntent.ReviewRequest, repo.LastCreated!.Intent);
    }

    [Fact]
    public async Task SendMessage_UnknownIntentWithRecognizedMetadataType_DerivesFromMetadataTypeAndPreservesLabel()
    {
        var repo = new FakeMessageRepo();
        var detection = new FakeDispatchDetection();
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"review_request","recipient":"pi"}""");

        await MessageTools.SendMessage(
            repo, detection, NullLogger<MessageTools>.Instance,
            project_id: "proj", sender: "codex", content: "Please review",
            metadata: metadata, intent: "diagnostic_update");

        Assert.NotNull(repo.LastCreated);
        Assert.Equal(MessageIntent.ReviewRequest, repo.LastCreated!.Intent);
        var meta = repo.LastCreated.Metadata!.Value;
        Assert.True(meta.TryGetProperty("requested_intent", out var ri));
        Assert.Equal("diagnostic_update", ri.GetString());
        Assert.True(meta.TryGetProperty("type", out var typeEl));
        Assert.Equal("review_request", typeEl.GetString());
    }

    [Fact]
    public async Task SendMessage_CanonicalIntentWithConflictingMetadataType_StillThrows()
    {
        // This tests that canonical/metadata conflict validation is preserved
        var repo = new FakeMessageRepo();
        var detection = new FakeDispatchDetection();
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"review_feedback"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MessageTools.SendMessage(
                repo, detection, NullLogger<MessageTools>.Instance,
                project_id: "proj", sender: "codex", content: "Feedback",
                metadata: metadata, intent: "review_request"));

        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
