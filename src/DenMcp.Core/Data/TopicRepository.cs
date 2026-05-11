using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface ITopicRepository
{
    Task<ConsolidationTopic> CreateAsync(ConsolidationTopic topic);
    Task<ConsolidationTopic?> GetByIdAsync(int id);
    Task<ConsolidationTopic?> GetBySlugAsync(string slug);
    Task<List<ConsolidationTopicSummary>> ListAsync(string? owningSpace = null, bool includeInactive = false);
    Task<List<ConsolidationTopicSummary>> ListActiveAsync(string? owningSpace = null);
    Task<ConsolidationTopic> UpdateAsync(int id, ConsolidationTopic topic);
    Task<bool> DeleteAsync(int id);
    Task<TopicValidationResult> ValidateAsync(string tag, bool allowInactive = false);
    Task<List<TopicValidationResult>> ValidateManyAsync(IEnumerable<string> tags, bool allowInactive = false);
}

public sealed class TopicRepository : ITopicRepository
{
    private readonly DbConnectionFactory _db;

    public TopicRepository(DbConnectionFactory db) => _db = db;

    public async Task<ConsolidationTopic> CreateAsync(ConsolidationTopic topic)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO consolidation_topics (slug, display_name, description, aliases, status, owning_space)
            VALUES (@slug, @displayName, @description, @aliases, @status, @owningSpace)
            RETURNING id, slug, display_name, description, aliases, status, owning_space, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@slug", topic.Slug);
        cmd.Parameters.AddWithValue("@displayName", topic.DisplayName);
        cmd.Parameters.AddWithValue("@description", (object?)topic.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@aliases", topic.Aliases is { Count: > 0 } ? JsonSerializer.Serialize(topic.Aliases) : DBNull.Value);
        cmd.Parameters.AddWithValue("@status", topic.Status);
        cmd.Parameters.AddWithValue("@owningSpace", (object?)topic.OwningSpace ?? DBNull.Value);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            return ReadTopic(reader);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            var existing = await GetBySlugAsync(topic.Slug);
            if (existing is not null)
                throw new InvalidOperationException($"Topic with slug '{topic.Slug}' already exists.");
            throw;
        }
    }

    public async Task<ConsolidationTopic?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, slug, display_name, description, aliases, status, owning_space, created_at, updated_at
            FROM consolidation_topics WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTopic(reader) : null;
    }

    public async Task<ConsolidationTopic?> GetBySlugAsync(string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, slug, display_name, description, aliases, status, owning_space, created_at, updated_at
            FROM consolidation_topics WHERE slug = @slug
            """;
        cmd.Parameters.AddWithValue("@slug", slug);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTopic(reader) : null;
    }

    public async Task<List<ConsolidationTopicSummary>> ListAsync(string? owningSpace = null, bool includeInactive = false)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (owningSpace is not null)
        {
            conditions.Add("owning_space = @owningSpace");
            cmd.Parameters.AddWithValue("@owningSpace", owningSpace);
        }
        if (!includeInactive)
        {
            conditions.Add("status = 'active'");
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT id, slug, display_name, description, aliases, status, owning_space, updated_at
            FROM consolidation_topics {whereClause}
            ORDER BY display_name COLLATE NOCASE
            """;

        var results = new List<ConsolidationTopicSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadSummary(reader));
        return results;
    }

    public async Task<List<ConsolidationTopicSummary>> ListActiveAsync(string? owningSpace = null)
    {
        return await ListAsync(owningSpace, includeInactive: false);
    }

    public async Task<ConsolidationTopic> UpdateAsync(int id, ConsolidationTopic topic)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE consolidation_topics
            SET slug = @slug,
                display_name = @displayName,
                description = @description,
                aliases = @aliases,
                status = @status,
                owning_space = @owningSpace,
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, slug, display_name, description, aliases, status, owning_space, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@slug", topic.Slug);
        cmd.Parameters.AddWithValue("@displayName", topic.DisplayName);
        cmd.Parameters.AddWithValue("@description", (object?)topic.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@aliases", topic.Aliases is { Count: > 0 } ? JsonSerializer.Serialize(topic.Aliases) : DBNull.Value);
        cmd.Parameters.AddWithValue("@status", topic.Status);
        cmd.Parameters.AddWithValue("@owningSpace", (object?)topic.OwningSpace ?? DBNull.Value);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new KeyNotFoundException($"Topic with id {id} not found");
            return ReadTopic(reader);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            var existing = await GetBySlugAsync(topic.Slug);
            if (existing is not null && existing.Id != id)
                throw new InvalidOperationException($"Topic with slug '{topic.Slug}' already exists.");
            throw;
        }
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM consolidation_topics WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<TopicValidationResult> ValidateAsync(string tag, bool allowInactive = false)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            SELECT slug, status
            FROM consolidation_topics
            WHERE (slug = @tag OR EXISTS (
                SELECT 1 FROM json_each(aliases) WHERE json_each.value = @tag
            ))
            ORDER BY status = 'active' DESC, slug
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@tag", tag);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var canonicalSlug = reader.GetString(0);
            var status = reader.GetString(1);
            if (!allowInactive && status != "active")
            {
                return new TopicValidationResult
                {
                    Valid = false,
                    Input = tag,
                    CanonicalSlug = canonicalSlug,
                    Reason = $"Topic '{canonicalSlug}' is not active (status: {status})"
                };
            }
            return new TopicValidationResult
            {
                Valid = true,
                Input = tag,
                CanonicalSlug = canonicalSlug
            };
        }

        return new TopicValidationResult
        {
            Valid = false,
            Input = tag,
            Reason = $"Unknown topic tag: '{tag}'"
        };
    }

    public async Task<List<TopicValidationResult>> ValidateManyAsync(IEnumerable<string> tags, bool allowInactive = false)
    {
        var results = new List<TopicValidationResult>();
        foreach (var tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await ValidateAsync(tag, allowInactive));
        }
        return results;
    }

    private static ConsolidationTopic ReadTopic(SqliteDataReader reader)
    {
        var aliasesJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new ConsolidationTopic
        {
            Id = reader.GetInt32(0),
            Slug = reader.GetString(1),
            DisplayName = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            Aliases = aliasesJson is not null ? JsonSerializer.Deserialize<List<string>>(aliasesJson) : null,
            Status = reader.GetString(5),
            OwningSpace = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = DateTime.Parse(reader.GetString(7)),
            UpdatedAt = DateTime.Parse(reader.GetString(8))
        };
    }

    private static ConsolidationTopicSummary ReadSummary(SqliteDataReader reader)
    {
        var aliasesJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new ConsolidationTopicSummary
        {
            Id = reader.GetInt32(0),
            Slug = reader.GetString(1),
            DisplayName = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            Aliases = aliasesJson is not null ? JsonSerializer.Deserialize<List<string>>(aliasesJson) : null,
            Status = reader.GetString(5),
            OwningSpace = reader.IsDBNull(6) ? null : reader.GetString(6),
            UpdatedAt = DateTime.Parse(reader.GetString(7))
        };
    }
}
