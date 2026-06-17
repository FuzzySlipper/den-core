using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Tools;

namespace DenCore.Service.Tests;

/// <summary>
/// Tests for artifact_kind-aware completion packet validation.
/// </summary>
public class ArtifactKindCompletionTests
{
    private static WorkerAssignment MakeAssignment(int id, string runId)
    {
        return new WorkerAssignment
        {
            Id = id,
            ProjectId = "test-proj",
            RunId = runId,
            WorkerIdentity = "test-worker-1",
            PoolMemberId = "test-worker-1",
            ProfileIdentity = "coder-worker",
            WorkerRole = "coder",
            Role = "coder",
            State = "ack",
            TaskId = 42,
            AssignedBy = "test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static FakeWorkerPoolRepository CreatePool(int id, string runId)
    {
        var pool = new FakeWorkerPoolRepository();
        pool.AddAssignment(MakeAssignment(id, runId));
        return pool;
    }

    private async Task<string> CallPostWorkerCompletion(
        string status = "completed",
        string? branch = null,
        string? headCommit = null,
        string? testsRun = null)
    {
        var pool = CreatePool(9100, "ak-test-run");
        var messages = new ArtifactKindMessageRepo();
        return await CompletionTools.PostWorkerCompletionPacket(
            pool, messages,
            project_id: "test-proj",
            run_id: "ak-test-run",
            requested_by: "test",
            status: status,
            role: "coder",
            packet_type: "implementation_packet",
            summary: "test",
            branch: branch,
            head_commit: headCommit,
            tests_run: testsRun,
            verbose: false);
    }

    private static bool IsMalformed(string json)
    {
        var doc = JsonDocument.Parse(json);
        var state = doc.RootElement.TryGetProperty("completion_state", out var el) ? el.GetString() ?? "" : "";
        return state == "malformed";
    }

    private static string GetState(string json)
    {
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("completion_state", out var el) ? el.GetString() ?? "" : "";
    }

    private static string? GetMetadataField(string json, string field)
    {
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("completion", out var c)) return null;
        if (!c.TryGetProperty("metadata", out var meta)) return null;
        return meta.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    // ── Artifact-kind tests (retired — artifact_kind parameter removed) ─────

    [Fact]
    public async Task CodeChange_WithAllMetadata_Valid()
    {
        var json = await CallPostWorkerCompletion(
            status: "completed",
            branch: "main", headCommit: "abc123", testsRun: "[\"npm test\"]");
        Assert.False(IsMalformed(json));
        Assert.Equal("present", GetState(json));
    }

    [Fact]
    public async Task FailedCompletion_NotMalformed()
    {
        var json = await CallPostWorkerCompletion(status: "failed");
        Assert.False(IsMalformed(json));
        Assert.Equal("present", GetState(json));
    }
}

/// <summary>
/// Minimal IMessageRepository for ArtifactKind tests.
/// Only implements members called by PostWorkerCompletionPacket.
/// </summary>
internal sealed class ArtifactKindMessageRepo : IMessageRepository
{
    public List<Message> Created { get; } = new();

    public Task<Message> CreateAsync(Message message)
    {
        message.Id = Created.Count + 100;
        message.Sender ??= "test";
        Created.Add(message);
        return Task.FromResult(message);
    }

    public Task<Message?> GetByIdAsync(int id) => Task.FromResult<Message?>(null);
    public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId, DateTime? since, string? unreadFor, int limit, MessageIntent? intent)
        => Task.FromResult(new List<Message>());
    public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit, MessageIntent? intent)
        => Task.FromResult(new List<MessageFeedItem>());
    public Task<Models.Thread> GetThreadAsync(int threadId)
        => Task.FromResult(new Models.Thread
        {
            Root = new Message { Id = 1, ProjectId = "p", Content = "", Sender = "t" },
            Replies = [new Message { Id = 1, ProjectId = "p", Content = "", Sender = "t" }],
        });
    public Task<Message?> CreateReplyAsync(int parentId, string projectId, int? taskId, string? sender, string content, MessageIntent? intent, string? metadata, string? dedupeKey, string? attachmentJson)
        => Task.FromResult<Message?>(null);
    public Task<int> MarkReadAsync(string projectId, params int[] messageIds) => Task.FromResult(0);
    public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(string? projectId, int? taskId, string? who, string? kind, string? entityType, bool? unread, string? cursorUtc, int limit, int offset)
        => Task.FromResult(new List<NotificationFeedItem>());
    public Task<int> MarkNotificationsReadAsync(string who, params int[]? notificationIds) => Task.FromResult(0);
    public Task<int> MarkAllNotificationsReadAsync(string who, string projectId, int? taskId) => Task.FromResult(0);
    public Task<WaitForMessagesResult> WaitForMessagesAsync(string projectId, string unreadFor, int limit, int timeoutSec, int? taskId)
        => Task.FromResult(new WaitForMessagesResult());
}
