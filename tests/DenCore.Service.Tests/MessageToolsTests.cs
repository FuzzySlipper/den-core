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
    public async Task SendMessage_IsTombstonedAfterMessagesCutover()
    {
        var repo = new FakeMessageRepo();
        var detection = new FakeDispatchDetection();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MessageTools.SendMessage(
                repo, detection, NullLogger<MessageTools>.Instance,
                project_id: "proj", sender: "codex", content: "Planning update"));

        Assert.Contains("moved from den-core to den-services/messages", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(repo.LastCreated);
    }

    [Fact]
    public async Task SendUserNotification_IsTombstonedAfterMessagesCutover()
    {
        var repo = new FakeMessageRepo();
        var detection = new FakeDispatchDetection();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MessageTools.SendUserNotification(
                repo, detection, NullLogger<MessageTools>.Instance,
                project_id: "proj", sender: "codex", content: "Attention"));

        Assert.Contains("send_user_notification", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(repo.LastCreated);
    }

    [Fact]
    public async Task MarkRead_IsTombstonedAfterMessagesCutover()
    {
        var repo = new FakeMessageRepo();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MessageTools.MarkRead(repo, agent: "codex", message_ids: "1,2"));

        Assert.Contains("mark_read", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkNotificationsRead_IsTombstonedAfterMessagesCutover()
    {
        var repo = new FakeMessageRepo();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MessageTools.MarkNotificationsRead(repo, agent: "codex", notification_ids: "1,2"));

        Assert.Contains("mark_notifications_read", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
