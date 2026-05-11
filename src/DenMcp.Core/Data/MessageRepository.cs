using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;
using Thread = DenMcp.Core.Models.Thread;

namespace DenMcp.Core.Data;

public interface IMessageRepository
{
    Task<Message> CreateAsync(Message message);
    Task<Message?> GetByIdAsync(int id);
    Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null,
        DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null);
    Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null);
    Task<Thread> GetThreadAsync(int threadId);
    Task<int> MarkReadAsync(string agent, int[] messageIds);
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
