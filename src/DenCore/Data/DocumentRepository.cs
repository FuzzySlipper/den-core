using System.Text.Json;
using System.Data.Common;
using DenCore.Models;

namespace DenCore.Data;

public interface IDocumentRepository
{
    Task<Document> UpsertAsync(Document document);
    Task<Document?> GetAsync(string projectId, string slug);
    Task<List<DocumentSummary>> ListAsync(string? projectId = null, DocType? docType = null, string[]? tags = null, DocumentVisibility? visibility = null);
    Task<List<DocumentSearchResult>> SearchAsync(string query, string? projectId = null);
    Task<bool> DeleteAsync(string projectId, string slug);
    Task<Document?> UpdateVisibilityAsync(string projectId, string slug, DocumentVisibility visibility);
    Task<List<DocumentSummary>> ListArchivedAsync(string? projectId = null, DocType? docType = null, string[]? tags = null);
    Task<List<DocumentSearchResult>> SearchArchivedAsync(string query, string? projectId = null);
    Task<DocumentArchivePreflightResult> ArchivePreflightAsync(string projectId, string slug);
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
            INSERT INTO documents (project_id, slug, title, content, doc_type, visibility, tags, summary)
            VALUES (@projectId, @slug, @title, @content, @docType, @visibility, @tags, @summary)
            ON CONFLICT(project_id, slug) DO UPDATE SET
                title = excluded.title,
                content = excluded.content,
                doc_type = excluded.doc_type,
                tags = excluded.tags,
                summary = excluded.summary,
                updated_at = datetime('now')
            RETURNING id, project_id, slug, title, content, doc_type, visibility, tags, summary, created_at, updated_at
            """;
        cmd.AddParameterWithValue("@projectId", document.ProjectId);
        cmd.AddParameterWithValue("@slug", document.Slug);
        cmd.AddParameterWithValue("@title", document.Title);
        cmd.AddParameterWithValue("@content", document.Content);
        cmd.AddParameterWithValue("@docType", document.DocType.ToDbValue());
        cmd.AddParameterWithValue("@visibility", document.Visibility.ToDbValue());
        cmd.AddParameterWithValue("@tags",
            document.Tags is { Count: > 0 } ? JsonSerializer.Serialize(document.Tags) : DBNull.Value);
        cmd.AddParameterWithValue("@summary",
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
            SELECT id, project_id, slug, title, content, doc_type, visibility, tags, summary, created_at, updated_at
            FROM documents WHERE project_id = @projectId AND slug = @slug
            """;
        cmd.AddParameterWithValue("@projectId", projectId);
        cmd.AddParameterWithValue("@slug", slug);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDocument(reader) : null;
    }

    public async Task<List<DocumentSummary>> ListAsync(
        string? projectId = null, DocType? docType = null, string[]? tags = null,
        DocumentVisibility? visibility = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();

        // Default: only return normal-visibility documents
        // (hidden and archived require explicit visibility filter)
        if (visibility is not null)
        {
            where.Add("visibility = @visibility");
            cmd.AddParameterWithValue("@visibility", visibility.Value.ToDbValue());
        }
        else
        {
            where.Add("visibility = 'normal'");
        }

        if (projectId is not null)
        {
            where.Add("project_id = @projectId");
            cmd.AddParameterWithValue("@projectId", projectId);
        }

        if (docType is not null)
        {
            where.Add("doc_type = @docType");
            cmd.AddParameterWithValue("@docType", docType.Value.ToDbValue());
        }

        if (tags is { Length: > 0 })
        {
            for (var i = 0; i < tags.Length; i++)
            {
                var p = $"@tag{i}";
                where.Add(_db.Sql.JsonArrayContains("tags", p));
                cmd.AddParameterWithValue(p, tags[i]);
            }
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
        cmd.CommandText = $"""
            SELECT id, project_id, slug, title, doc_type, visibility, tags, summary, updated_at
            FROM documents {whereClause}
            ORDER BY updated_at DESC
            """;

        var results = new List<DocumentSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tagsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            results.Add(new DocumentSummary
            {
                Id = reader.GetInt32(0),
                ProjectId = reader.GetString(1),
                Slug = reader.GetString(2),
                Title = reader.GetString(3),
                DocType = EnumExtensions.ParseDocType(reader.GetString(4)),
                Visibility = EnumExtensions.ParseDocumentVisibility(reader.GetString(5)),
                Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
                Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt = DateTime.Parse(reader.GetString(8))
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
            SELECT d.project_id, d.slug, d.title, d.doc_type, d.visibility, d.summary,
                   snippet(documents_fts, 1, '<b>', '</b>', '...', 32) as snippet,
                   rank
            FROM documents_fts fts
            JOIN documents d ON d.id = fts.rowid
            WHERE documents_fts MATCH @query {projectFilter}
              AND d.visibility = 'normal'
            ORDER BY rank
            """;
        cmd.AddParameterWithValue("@query", query);
        if (projectId is not null)
            cmd.AddParameterWithValue("@projectId", projectId);

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
                Visibility = EnumExtensions.ParseDocumentVisibility(reader.GetString(4)),
                Summary = reader.IsDBNull(5) ? null : reader.GetString(5),
                Snippet = reader.GetString(6),
                Rank = reader.GetDouble(7)
            });
        }
        return results;
    }

    public async Task<bool> DeleteAsync(string projectId, string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE project_id = @projectId AND slug = @slug";
        cmd.AddParameterWithValue("@projectId", projectId);
        cmd.AddParameterWithValue("@slug", slug);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<Document?> UpdateVisibilityAsync(string projectId, string slug, DocumentVisibility visibility)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE documents SET visibility = @visibility, updated_at = datetime('now')
            WHERE project_id = @projectId AND slug = @slug
            RETURNING id, project_id, slug, title, content, doc_type, visibility, tags, summary, created_at, updated_at
            """;
        cmd.AddParameterWithValue("@projectId", projectId);
        cmd.AddParameterWithValue("@slug", slug);
        cmd.AddParameterWithValue("@visibility", visibility.ToDbValue());

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDocument(reader) : null;
    }

    public async Task<List<DocumentSummary>> ListArchivedAsync(
        string? projectId = null, DocType? docType = null, string[]? tags = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { "visibility = 'archived'" };

        if (projectId is not null)
        {
            where.Add("project_id = @projectId");
            cmd.AddParameterWithValue("@projectId", projectId);
        }

        if (docType is not null)
        {
            where.Add("doc_type = @docType");
            cmd.AddParameterWithValue("@docType", docType.Value.ToDbValue());
        }

        if (tags is { Length: > 0 })
        {
            for (var i = 0; i < tags.Length; i++)
            {
                var p = $"@tag{i}";
                where.Add(_db.Sql.JsonArrayContains("tags", p));
                cmd.AddParameterWithValue(p, tags[i]);
            }
        }

        var whereClause = $"WHERE {string.Join(" AND ", where)}";
        cmd.CommandText = $"""
            SELECT id, project_id, slug, title, doc_type, visibility, tags, summary, updated_at
            FROM documents {whereClause}
            ORDER BY updated_at DESC
            """;

        var results = new List<DocumentSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tagsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            results.Add(new DocumentSummary
            {
                Id = reader.GetInt32(0),
                ProjectId = reader.GetString(1),
                Slug = reader.GetString(2),
                Title = reader.GetString(3),
                DocType = EnumExtensions.ParseDocType(reader.GetString(4)),
                Visibility = EnumExtensions.ParseDocumentVisibility(reader.GetString(5)),
                Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
                Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt = DateTime.Parse(reader.GetString(8))
            });
        }
        return results;
    }

    public async Task<List<DocumentSearchResult>> SearchArchivedAsync(string query, string? projectId = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var projectFilter = projectId is not null ? "AND d.project_id = @projectId" : "";

        cmd.CommandText = $"""
            SELECT d.project_id, d.slug, d.title, d.doc_type, d.visibility, d.summary,
                   snippet(documents_fts, 1, '<b>', '</b>', '...', 32) as snippet,
                   rank
            FROM documents_fts fts
            JOIN documents d ON d.id = fts.rowid
            WHERE documents_fts MATCH @query {projectFilter}
              AND d.visibility = 'archived'
            ORDER BY rank
            """;
        cmd.AddParameterWithValue("@query", query);
        if (projectId is not null)
            cmd.AddParameterWithValue("@projectId", projectId);

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
                Visibility = EnumExtensions.ParseDocumentVisibility(reader.GetString(4)),
                Summary = reader.IsDBNull(5) ? null : reader.GetString(5),
                Snippet = reader.GetString(6),
                Rank = reader.GetDouble(7)
            });
        }
        return results;
    }

    public async Task<DocumentArchivePreflightResult> ArchivePreflightAsync(string projectId, string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Verify document exists
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT visibility FROM documents WHERE project_id = @projectId AND slug = @slug
            """;
        checkCmd.AddParameterWithValue("@projectId", projectId);
        checkCmd.AddParameterWithValue("@slug", slug);
        await using var checkReader = await checkCmd.ExecuteReaderAsync();
        if (!await checkReader.ReadAsync())
        {
            return new DocumentArchivePreflightResult
            {
                ProjectId = projectId,
                Slug = slug,
                CanArchive = false,
                ReferencedBy = []
            };
        }
        await checkReader.CloseAsync();

        var references = new List<DocumentReference>();

        // Check agent_guidance_entries referencing this document
        await using var guidanceCmd = conn.CreateCommand();
        guidanceCmd.CommandText = """
            SELECT g.project_id, g.document_project_id, g.document_slug
            FROM agent_guidance_entries g
            WHERE g.document_project_id = @projectId AND g.document_slug = @slug
            """;
        guidanceCmd.AddParameterWithValue("@projectId", projectId);
        guidanceCmd.AddParameterWithValue("@slug", slug);
        await using var guidanceReader = await guidanceCmd.ExecuteReaderAsync();
        while (await guidanceReader.ReadAsync())
        {
            var scope = guidanceReader.GetString(0);
            references.Add(new DocumentReference
            {
                RefKind = "agent_guidance",
                Description = $"Agent guidance entry in scope '{scope}' references document '{projectId}/{slug}'",
                ScopeProjectId = scope
            });
        }

        return new DocumentArchivePreflightResult
        {
            ProjectId = projectId,
            Slug = slug,
            CanArchive = references.Count == 0,
            ReferencedBy = references
        };
    }

    private static Document ReadDocument(DbDataReader reader)
    {
        var tagsJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        return new Document
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            Slug = reader.GetString(2),
            Title = reader.GetString(3),
            Content = reader.GetString(4),
            DocType = EnumExtensions.ParseDocType(reader.GetString(5)),
            Visibility = EnumExtensions.ParseDocumentVisibility(reader.GetString(6)),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            Summary = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt = DateTime.Parse(reader.GetString(9)),
            UpdatedAt = DateTime.Parse(reader.GetString(10))
        };
    }
}
