using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IBlackboardRepository
{
    Task<BlackboardEntry> UpsertAsync(BlackboardEntry entry);
    Task<BlackboardEntry?> GetAsync(string slug);
    Task<List<BlackboardEntrySummary>> ListAsync(string[]? tags = null);
    Task<bool> DeleteAsync(string slug);
    Task<int> DeleteExpiredAsync();
}

public sealed class BlackboardRepository : IBlackboardRepository
{
    private readonly DbConnectionFactory _db;

    public BlackboardRepository(DbConnectionFactory db) => _db = db;

    public async Task<BlackboardEntry> UpsertAsync(BlackboardEntry entry)
    {
        ValidateIdleTtl(entry.IdleTtlSeconds);

        await using var conn = await _db.CreateConnectionAsync();
        await DeleteExpiredAsync(conn);

        var now = DateTime.UtcNow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO blackboard_entries (slug, title, content, tags, idle_ttl_seconds, last_accessed_at)
            VALUES (@slug, @title, @content, @tags, @idleTtlSeconds, @now)
            ON CONFLICT(slug) DO UPDATE SET
                title = excluded.title,
                content = excluded.content,
                tags = excluded.tags,
                idle_ttl_seconds = excluded.idle_ttl_seconds,
                last_accessed_at = excluded.last_accessed_at,
                updated_at = @now
            RETURNING id, slug, title, content, tags, idle_ttl_seconds, created_at, updated_at, last_accessed_at
            """;
        cmd.Parameters.AddWithValue("@slug", entry.Slug);
        cmd.Parameters.AddWithValue("@title", entry.Title);
        cmd.Parameters.AddWithValue("@content", entry.Content);
        cmd.Parameters.AddWithValue("@tags", entry.Tags is { Count: > 0 } ? JsonSerializer.Serialize(entry.Tags) : DBNull.Value);
        cmd.Parameters.AddWithValue("@idleTtlSeconds", entry.IdleTtlSeconds is not null ? entry.IdleTtlSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@now", ToDbString(now));

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadEntry(reader);
    }

    public async Task<BlackboardEntry?> GetAsync(string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await DeleteExpiredAsync(conn);

        var now = DateTime.UtcNow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE blackboard_entries
            SET last_accessed_at = CASE WHEN idle_ttl_seconds IS NOT NULL THEN @now ELSE last_accessed_at END
            WHERE slug = @slug
            RETURNING id, slug, title, content, tags, idle_ttl_seconds, created_at, updated_at, last_accessed_at
            """;
        cmd.Parameters.AddWithValue("@slug", slug);
        cmd.Parameters.AddWithValue("@now", ToDbString(now));

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEntry(reader) : null;
    }

    public async Task<List<BlackboardEntrySummary>> ListAsync(string[]? tags = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await DeleteExpiredAsync(conn);

        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (tags is { Length: > 0 })
        {
            for (var i = 0; i < tags.Length; i++)
            {
                var p = $"@tag{i}";
                where.Add($"EXISTS (SELECT 1 FROM json_each(tags) WHERE json_each.value = {p})");
                cmd.Parameters.AddWithValue(p, tags[i]);
            }
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
        cmd.CommandText = $"""
            SELECT id, slug, title, tags, idle_ttl_seconds, updated_at, last_accessed_at
            FROM blackboard_entries {whereClause}
            ORDER BY updated_at DESC, id DESC
            """;

        var results = new List<BlackboardEntrySummary>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                results.Add(ReadSummary(reader));
        }

        if (results.Count > 0)
        {
            var touchedAt = TruncateToSeconds(DateTime.UtcNow);
            await TouchIdleTtlEntriesAsync(conn, results.Select(entry => entry.Slug), touchedAt);
            foreach (var entry in results.Where(entry => entry.IdleTtlSeconds is not null))
                entry.LastAccessedAt = touchedAt;
        }

        return results;
    }

    public async Task<bool> DeleteAsync(string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM blackboard_entries WHERE slug = @slug";
        cmd.Parameters.AddWithValue("@slug", slug);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> DeleteExpiredAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();
        return await DeleteExpiredAsync(conn);
    }

    private static async Task<int> DeleteExpiredAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM blackboard_entries
            WHERE idle_ttl_seconds IS NOT NULL
              AND datetime(last_accessed_at, '+' || idle_ttl_seconds || ' seconds') <= @now
            """;
        cmd.Parameters.AddWithValue("@now", ToDbString(DateTime.UtcNow));
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task TouchIdleTtlEntriesAsync(SqliteConnection conn, IEnumerable<string> slugs, DateTime touchedAt)
    {
        var slugList = slugs.Distinct(StringComparer.Ordinal).ToList();
        if (slugList.Count == 0) return;

        await using var cmd = conn.CreateCommand();
        var parameters = new List<string>();
        for (var i = 0; i < slugList.Count; i++)
        {
            var p = $"@slug{i}";
            parameters.Add(p);
            cmd.Parameters.AddWithValue(p, slugList[i]);
        }

        cmd.CommandText = $"""
            UPDATE blackboard_entries
            SET last_accessed_at = @now
            WHERE idle_ttl_seconds IS NOT NULL AND slug IN ({string.Join(", ", parameters)})
            """;
        cmd.Parameters.AddWithValue("@now", ToDbString(touchedAt));
        await cmd.ExecuteNonQueryAsync();
    }

    private static void ValidateIdleTtl(int? idleTtlSeconds)
    {
        if (idleTtlSeconds is <= 0)
            throw new ArgumentException("Idle TTL must be a positive number of seconds when provided.", nameof(idleTtlSeconds));
    }

    private static BlackboardEntry ReadEntry(SqliteDataReader reader)
    {
        var tagsJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new BlackboardEntry
        {
            Id = reader.GetInt32(0),
            Slug = reader.GetString(1),
            Title = reader.GetString(2),
            Content = reader.GetString(3),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            IdleTtlSeconds = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            CreatedAt = DateTime.Parse(reader.GetString(6)),
            UpdatedAt = DateTime.Parse(reader.GetString(7)),
            LastAccessedAt = DateTime.Parse(reader.GetString(8))
        };
    }

    private static BlackboardEntrySummary ReadSummary(SqliteDataReader reader)
    {
        var tagsJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new BlackboardEntrySummary
        {
            Id = reader.GetInt32(0),
            Slug = reader.GetString(1),
            Title = reader.GetString(2),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            IdleTtlSeconds = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            UpdatedAt = DateTime.Parse(reader.GetString(5)),
            LastAccessedAt = DateTime.Parse(reader.GetString(6))
        };
    }

    private static DateTime TruncateToSeconds(DateTime value) => new(
        value.ToUniversalTime().Ticks - value.ToUniversalTime().Ticks % TimeSpan.TicksPerSecond,
        DateTimeKind.Utc);

    private static string ToDbString(DateTime value) => TruncateToSeconds(value).ToString("yyyy-MM-dd HH:mm:ss");
}
