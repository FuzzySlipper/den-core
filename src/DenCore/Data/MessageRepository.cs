using System.Text.Json;
using DenCore.Models;
using Microsoft.Data.Sqlite;
using Thread = DenCore.Models.Thread;

namespace DenCore.Data;

public interface IMessageRepository
{
    Task<Message> CreateAsync(Message message);
    Task<Message?> GetByIdAsync(int id);
    Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null,
        DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null);
    Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null);
    Task<Thread> GetThreadAsync(int threadId);
    Task<int> MarkReadAsync(string agent, int[] messageIds);
    Task<List<NotificationFeedItem>> GetNotificationFeedAsync(
        string? projectId = null,
        int? taskId = null,
        string? sender = null,
        string? metadataType = null,
        string? urgency = null,
        bool? isRead = null,
        string? readForAgent = null,
        int limit = 20,
        int offset = 0);
    Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds);
    Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null);
    Task<WaitForMessagesResult> WaitForMessagesAsync(string projectId, string unreadFor,
        int timeoutMs = 30000, int limit = 20, int? cursorMessageId = null);
}

public sealed class MessageRepository : IMessageRepository
{
    private readonly DbConnectionFactory _db;

    public MessageRepository(DbConnectionFactory db) => _db = db;

    public async Task<Message> CreateAsync(Message message)
    {
        var resolvedIntent = MessageIntentCompatibility.ResolveWriteIntent(message.Intent, message.Metadata);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (project_id, task_id, thread_id, sender, content, intent, metadata)
            VALUES (@projectId, @taskId, @threadId, @sender, @content, @intent, @metadata)
            RETURNING id, project_id, task_id, thread_id, sender, content, intent, metadata, created_at
            """;
        cmd.Parameters.AddWithValue("@projectId", message.ProjectId);
        cmd.Parameters.AddWithValue("@taskId", (object?)message.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@threadId", (object?)message.ThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sender", message.Sender);
        cmd.Parameters.AddWithValue("@content", message.Content);
        cmd.Parameters.AddWithValue("@intent", resolvedIntent.ToDbValue());
        cmd.Parameters.AddWithValue("@metadata",
            message.Metadata.HasValue ? message.Metadata.Value.GetRawText() : DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadMessage(reader);
    }

    public async Task<Message?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_id, task_id, thread_id, sender, content, intent, metadata, created_at
            FROM messages WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadMessage(reader) : null;
    }

    public async Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null,
        DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null)
    {
        limit = Math.Clamp(limit, 1, 100);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { "m.project_id = @projectId" };
        cmd.Parameters.AddWithValue("@projectId", projectId);

        if (taskId is not null)
        {
            where.Add("m.task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", taskId.Value);
        }

        if (since is not null)
        {
            where.Add("m.created_at > @since");
            cmd.Parameters.AddWithValue("@since", since.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        if (unreadFor is not null)
        {
            where.Add("""
                NOT EXISTS (
                    SELECT 1 FROM message_reads mr
                    WHERE mr.message_id = m.id AND mr.agent = @unreadFor
                )
                """);
            where.Add("m.sender != @unreadFor");
            cmd.Parameters.AddWithValue("@unreadFor", unreadFor);
        }

        if (intent is not null)
        {
            where.Add("m.intent = @intent");
            cmd.Parameters.AddWithValue("@intent", intent.Value.ToDbValue());
        }

        cmd.CommandText = $"""
            SELECT m.id, m.project_id, m.task_id, m.thread_id, m.sender, m.content, m.intent, m.metadata, m.created_at
            FROM messages m
            WHERE {string.Join(" AND ", where)}
            ORDER BY m.created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var messages = new List<Message>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            messages.Add(ReadMessage(reader));
        return messages;
    }

    public async Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null)
    {
        limit = Math.Clamp(limit, 1, 100);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var intentClause = intent is not null ? "AND m.intent = @intent" : "";
        var latestIntentClause = intent is not null ? "AND m2.intent = @intent" : "";
        cmd.CommandText = """
            WITH conversation_roots AS (
                SELECT
                    COALESCE(m.thread_id, m.id) AS root_id,
                    MAX(m.created_at) AS latest_activity_at,
                    COUNT(*) - 1 AS reply_count
                FROM messages m
                WHERE m.project_id = @projectId
            """ + $"""
                  {intentClause}
            """ + """
                GROUP BY COALESCE(m.thread_id, m.id)
            )
            SELECT
                root.id, root.project_id, root.task_id, root.thread_id, root.sender, root.content, root.intent, root.metadata, root.created_at,
                latest.id, latest.project_id, latest.task_id, latest.thread_id, latest.sender, latest.content, latest.intent, latest.metadata, latest.created_at,
                cr.reply_count,
                cr.latest_activity_at
            FROM conversation_roots cr
            JOIN messages root ON root.id = cr.root_id
            JOIN messages latest ON latest.id = (
                SELECT m2.id
                FROM messages m2
                WHERE m2.project_id = @projectId
                  AND COALESCE(m2.thread_id, m2.id) = cr.root_id
            """ + $"""
                  {latestIntentClause}
            """ + """
                ORDER BY m2.created_at DESC, m2.id DESC
                LIMIT 1
            )
            ORDER BY cr.latest_activity_at DESC, cr.root_id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (intent is not null)
            cmd.Parameters.AddWithValue("@intent", intent.Value.ToDbValue());

        var items = new List<MessageFeedItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new MessageFeedItem
            {
                RootMessage = ReadMessage(reader, 0),
                LatestMessage = ReadMessage(reader, 9),
                ReplyCount = reader.GetInt32(18),
                LatestActivityAt = DateTime.Parse(reader.GetString(19))
            });
        }

        return items;
    }

    public async Task<Thread> GetThreadAsync(int threadId)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Get root message
        await using var rootCmd = conn.CreateCommand();
        rootCmd.CommandText = "SELECT id, project_id, task_id, thread_id, sender, content, intent, metadata, created_at FROM messages WHERE id = @id";
        rootCmd.Parameters.AddWithValue("@id", threadId);
        await using var rootReader = await rootCmd.ExecuteReaderAsync();
        if (!await rootReader.ReadAsync())
            throw new KeyNotFoundException($"Message {threadId} not found");
        var root = ReadMessage(rootReader);
        await rootReader.CloseAsync();

        // Get replies
        await using var repliesCmd = conn.CreateCommand();
        repliesCmd.CommandText = """
            SELECT id, project_id, task_id, thread_id, sender, content, intent, metadata, created_at
            FROM messages WHERE thread_id = @threadId
            ORDER BY created_at ASC
            """;
        repliesCmd.Parameters.AddWithValue("@threadId", threadId);

        var replies = new List<Message>();
        await using var repliesReader = await repliesCmd.ExecuteReaderAsync();
        while (await repliesReader.ReadAsync())
            replies.Add(ReadMessage(repliesReader));

        return new Thread { Root = root, Replies = replies };
    }

    public async Task<int> MarkReadAsync(string agent, int[] messageIds)
    {
        if (messageIds.Length == 0) return 0;

        await using var conn = await _db.CreateConnectionAsync();
        var count = 0;

        foreach (var msgId in messageIds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO message_reads (message_id, agent) VALUES (@msgId, @agent)";
            cmd.Parameters.AddWithValue("@msgId", msgId);
            cmd.Parameters.AddWithValue("@agent", agent);
            count += await cmd.ExecuteNonQueryAsync();
        }

        return count;
    }

    public async Task<List<NotificationFeedItem>> GetNotificationFeedAsync(
        string? projectId = null,
        int? taskId = null,
        string? sender = null,
        string? metadataType = null,
        string? urgency = null,
        bool? isRead = null,
        string? readForAgent = null,
        int limit = 20,
        int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { "m.intent = 'notification'" };

        if (projectId is not null)
        {
            where.Add("m.project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", projectId);
        }

        if (taskId is not null)
        {
            where.Add("m.task_id = @taskId");
            cmd.Parameters.AddWithValue("@taskId", taskId.Value);
        }

        if (sender is not null)
        {
            where.Add("m.sender = @sender");
            cmd.Parameters.AddWithValue("@sender", sender);
        }

        if (metadataType is not null)
        {
            where.Add("json_extract(m.metadata, '$.type') = @metadataType");
            cmd.Parameters.AddWithValue("@metadataType", metadataType);
        }

        if (urgency is not null)
        {
            where.Add("json_extract(m.metadata, '$.urgency') = @urgency");
            cmd.Parameters.AddWithValue("@urgency", urgency);
        }

        if (isRead is not null && readForAgent is not null)
        {
            if (isRead.Value)
            {
                where.Add("""
                    EXISTS (
                        SELECT 1 FROM message_reads mr
                        WHERE mr.message_id = m.id AND mr.agent = @readForAgent
                    )
                    """);
            }
            else
            {
                where.Add("""
                    NOT EXISTS (
                        SELECT 1 FROM message_reads mr
                        WHERE mr.message_id = m.id AND mr.agent = @readForAgent
                    )
                    """);
            }
            cmd.Parameters.AddWithValue("@readForAgent", readForAgent);
        }

        var isReadExpr = readForAgent is not null
            ? """
                CASE WHEN EXISTS (
                    SELECT 1 FROM message_reads mr
                    WHERE mr.message_id = m.id AND mr.agent = @readForAgentResult
                ) THEN 1 ELSE 0 END
                """
            : "0";

        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        if (readForAgent is not null)
            cmd.Parameters.AddWithValue("@readForAgentResult", readForAgent);

        cmd.CommandText = $"""
            SELECT
                m.id, m.project_id, m.task_id, m.thread_id, m.sender, m.content,
                m.metadata, m.created_at,
                json_extract(m.metadata, '$.urgency') AS urgency,
                {isReadExpr} AS is_read
            FROM messages m
            WHERE {string.Join(" AND ", where)}
            ORDER BY m.created_at DESC, m.id DESC
            LIMIT @limit OFFSET @offset
            """;

        var items = new List<NotificationFeedItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var metaJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            items.Add(new NotificationFeedItem
            {
                Id = reader.GetInt32(0),
                ProjectId = reader.GetString(1),
                TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                ThreadId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Sender = reader.GetString(4),
                Content = reader.GetString(5),
                Metadata = metaJson is not null ? JsonSerializer.Deserialize<JsonElement>(metaJson) : null,
                CreatedAt = DateTime.Parse(reader.GetString(7)),
                Urgency = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsRead = reader.GetInt32(9) == 1
            });
        }

        return items;
    }

    public async Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds)
    {
        if (notificationIds is null || notificationIds.Length == 0) return 0;

        await using var conn = await _db.CreateConnectionAsync();
        var count = 0;

        foreach (var msgId in notificationIds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO message_reads (message_id, agent)
                SELECT @msgId, @agent
                WHERE EXISTS (
                    SELECT 1 FROM messages m WHERE m.id = @msgId AND m.intent = 'notification'
                )
                """;
            cmd.Parameters.AddWithValue("@msgId", msgId);
            cmd.Parameters.AddWithValue("@agent", agent);
            count += await cmd.ExecuteNonQueryAsync();
        }

        return count;
    }

    public async Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var taskClause = taskId is not null ? "AND m.task_id = @taskId" : "";
        cmd.CommandText = $"""
            INSERT OR IGNORE INTO message_reads (message_id, agent)
            SELECT m.id, @agent
            FROM messages m
            WHERE m.intent = 'notification'
              AND m.project_id = @projectId
              {taskClause}
            """;
        cmd.Parameters.AddWithValue("@agent", agent);
        cmd.Parameters.AddWithValue("@projectId", projectId);
        if (taskId is not null)
            cmd.Parameters.AddWithValue("@taskId", taskId.Value);

        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<WaitForMessagesResult> WaitForMessagesAsync(string projectId, string unreadFor,
        int timeoutMs = 30000, int limit = 20, int? cursorMessageId = null)
    {
        const int maxTimeout = 60_000;
        const int minTimeout = 500;
        const int pollIntervalMs = 500;

        timeoutMs = Math.Clamp(timeoutMs, minTimeout, maxTimeout);
        limit = Math.Clamp(limit, 1, 100);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastHighWaterMark = cursorMessageId ?? 0;

        while (true)
        {
            var messages = await GetUnreadMessagesAfterCursorAsync(projectId, unreadFor, limit, lastHighWaterMark);
            var newMessages = messages
                .Select(m => new WaitForMessagesItem
                {
                    Id = m.Id,
                    Sender = m.Sender,
                    TaskId = m.TaskId,
                    ContentPreview = m.Content is { Length: > 500 } c ? c[..500] : m.Content ?? "",
                    CreatedAt = m.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                })
                .ToList();

            if (newMessages.Count > 0)
            {
                sw.Stop();
                return new WaitForMessagesResult
                {
                    TimedOut = false,
                    WaitedMs = (int)sw.ElapsedMilliseconds,
                    Messages = newMessages,
                };
            }

            var remainingMs = timeoutMs - sw.ElapsedMilliseconds;
            if (remainingMs <= 0)
                break;

            await Task.Delay((int)Math.Min(pollIntervalMs, remainingMs));
        }

        sw.Stop();
        return new WaitForMessagesResult
        {
            TimedOut = true,
            WaitedMs = (int)sw.ElapsedMilliseconds,
            Messages = [],
        };
    }

    private async Task<List<Message>> GetUnreadMessagesAfterCursorAsync(string projectId, string unreadFor, int limit, int cursorMessageId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.project_id, m.task_id, m.thread_id, m.sender, m.content, m.intent, m.metadata, m.created_at
            FROM messages m
            WHERE m.project_id = @projectId
              AND m.id > @cursorMessageId
              AND NOT EXISTS (
                  SELECT 1 FROM message_reads mr
                  WHERE mr.message_id = m.id AND mr.agent = @unreadFor
              )
              AND m.sender != @unreadFor
            ORDER BY m.created_at DESC, m.id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@cursorMessageId", cursorMessageId);
        cmd.Parameters.AddWithValue("@unreadFor", unreadFor);
        cmd.Parameters.AddWithValue("@limit", limit);

        var messages = new List<Message>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            messages.Add(ReadMessage(reader));
        return messages;
    }

    private static Message ReadMessage(SqliteDataReader reader, int offset = 0)
    {
        var metaJson = reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7);
        return new Message
        {
            Id = reader.GetInt32(offset + 0),
            ProjectId = reader.GetString(offset + 1),
            TaskId = reader.IsDBNull(offset + 2) ? null : reader.GetInt32(offset + 2),
            ThreadId = reader.IsDBNull(offset + 3) ? null : reader.GetInt32(offset + 3),
            Sender = reader.GetString(offset + 4),
            Content = reader.GetString(offset + 5),
            Intent = EnumExtensions.ParseMessageIntent(reader.GetString(offset + 6)),
            Metadata = metaJson is not null ? JsonSerializer.Deserialize<JsonElement>(metaJson) : null,
            CreatedAt = DateTime.Parse(reader.GetString(offset + 8))
        };
    }
}
