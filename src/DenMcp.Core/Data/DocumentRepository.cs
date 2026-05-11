using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IDocumentRepository
{
    Task<Document> UpsertAsync(Document document);
    Task<Document?> GetAsync(string projectId, string slug);
    Task<List<DocumentSummary>> ListAsync(string? projectId = null, DocType? docType = null, string[]? tags = null);
    Task<List<DocumentSearchResult>> SearchAsync(string query, string? projectId = null);
    Task<bool> DeleteAsync(string projectId, string slug);
}

public sealed class DocumentRepository : IDocumentRepository
{
    private readonly DbConnectionFactory _db;

    public DocumentRepository(DbConnectionFactory db) => _db = db;

    public async Task<Document> UpsertAsync(Document document)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (project_id, slug, title, content, doc_type, tags, summary)
            VALUES (@projectId, @slug, @title, @content, @docType, @tags, @summary)
            ON CONFLICT(project_id, slug) DO UPDATE SET
                title = excluded.title,
                content = excluded.content,
                doc_type = excluded.doc_type,
                tags = excluded.tags,
                summary = excluded.summary,
                updated_at = datetime('now')
            RETURNING id, project_id, slug, title, content, doc_type, tags, summary, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@projectId", document.ProjectId);
        cmd.Parameters.AddWithValue("@slug", document.Slug);
        cmd.Parameters.AddWithValue("@title", document.Title);
        cmd.Parameters.AddWithValue("@content", document.Content);
        cmd.Parameters.AddWithValue("@docType", document.DocType.ToDbValue());
        cmd.Parameters.AddWithValue("@tags",
            document.Tags is { Count: > 0 } ? JsonSerializer.Serialize(document.Tags) : DBNull.Value);
        cmd.Parameters.AddWithValue("@summary",
            document.Summary is not null ? document.Summary : DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadDocument(reader);
    }

    public async Task<Document?> GetAsync(string projectId, string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_id, slug, title, content, doc_type, tags, summary, created_at, updated_at
            FROM documents WHERE project_id = @projectId AND slug = @slug
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@slug", slug);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDocument(reader) : null;
    }

    public async Task<List<DocumentSummary>> ListAsync(string? projectId = null, DocType? docType = null, string[]? tags = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();

        if (projectId is not null)
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", projectId);
        }

        if (docType is not null)
        {
            where.Add("doc_type = @docType");
            cmd.Parameters.AddWithValue("@docType", docType.Value.ToDbValue());
        }

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
            SELECT id, project_id, slug, title, doc_type, tags, summary, updated_at
            FROM documents {whereClause}
            ORDER BY updated_at DESC
            """;

        var results = new List<DocumentSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tagsJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            results.Add(new DocumentSummary
            {
                Id = reader.GetInt32(0),
                ProjectId = reader.GetString(1),
                Slug = reader.GetString(2),
                Title = reader.GetString(3),
                DocType = EnumExtensions.ParseDocType(reader.GetString(4)),
                Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
                Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
                UpdatedAt = DateTime.Parse(reader.GetString(7))
            });
        }
        return results;
    }

    public async Task<List<DocumentSearchResult>> SearchAsync(string query, string? projectId = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var projectFilter = projectId is not null ? "AND d.project_id = @projectId" : "";

        cmd.CommandText = $"""
            SELECT d.project_id, d.slug, d.title, d.doc_type, d.summary,
                   snippet(documents_fts, 1, '<b>', '</b>', '...', 32) as snippet,
                   rank
            FROM documents_fts fts
            JOIN documents d ON d.id = fts.rowid
            WHERE documents_fts MATCH @query {projectFilter}
            ORDER BY rank
            """;
        cmd.Parameters.AddWithValue("@query", query);
        if (projectId is not null)
            cmd.Parameters.AddWithValue("@projectId", projectId);

        var results = new List<DocumentSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DocumentSearchResult
            {
                ProjectId = reader.GetString(0),
                Slug = reader.GetString(1),
                Title = reader.GetString(2),
                DocType = EnumExtensions.ParseDocType(reader.GetString(3)),
                Summary = reader.IsDBNull(4) ? null : reader.GetString(4),
                Snippet = reader.GetString(5),
                Rank = reader.GetDouble(6)
            });
        }
        return results;
    }

    public async Task<bool> DeleteAsync(string projectId, string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE project_id = @projectId AND slug = @slug";
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@slug", slug);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static Document ReadDocument(SqliteDataReader reader)
    {
        var tagsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new Document
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            Slug = reader.GetString(2),
            Title = reader.GetString(3),
            Content = reader.GetString(4),
            DocType = EnumExtensions.ParseDocType(reader.GetString(5)),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = DateTime.Parse(reader.GetString(8)),
            UpdatedAt = DateTime.Parse(reader.GetString(9))
        };
    }
}
