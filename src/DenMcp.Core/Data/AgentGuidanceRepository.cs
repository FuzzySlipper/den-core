using System.Text;
using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IAgentGuidanceRepository
{
    Task<AgentGuidanceEntry> UpsertAsync(AgentGuidanceEntry entry);
    Task<List<AgentGuidanceEntry>> ListAsync(string? projectId = null, bool includeGlobal = false);
    Task<bool> DeleteAsync(int id, string? projectId = null);
    Task<ResolvedAgentGuidance> ResolveAsync(string projectId);
}

public sealed class AgentGuidanceRepository : IAgentGuidanceRepository
{
    private readonly DbConnectionFactory _db;

    public AgentGuidanceRepository(DbConnectionFactory db) => _db = db;

    public async Task<AgentGuidanceEntry> UpsertAsync(AgentGuidanceEntry entry)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_guidance_entries (
                project_id,
                document_project_id,
                document_slug,
                importance,
                audience,
                sort_order,
                notes
            )
            VALUES (
                @projectId,
                @documentProjectId,
                @documentSlug,
                @importance,
                @audience,
                @sortOrder,
                @notes
            )
            ON CONFLICT(project_id, document_project_id, document_slug) DO UPDATE SET
                importance = excluded.importance,
                audience = excluded.audience,
                sort_order = excluded.sort_order,
                notes = excluded.notes,
                updated_at = datetime('now')
            RETURNING id, project_id, document_project_id, document_slug, importance, audience, sort_order, notes, created_at, updated_at
            """;
        AddEntryParameters(cmd, entry);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadEntry(reader);
    }

    public async Task<List<AgentGuidanceEntry>> ListAsync(string? projectId = null, bool includeGlobal = false)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = "";
        if (projectId is not null)
        {
            where = includeGlobal && projectId != "_global"
                ? "WHERE project_id IN ('_global', @projectId)"
                : "WHERE project_id = @projectId";
            cmd.Parameters.AddWithValue("@projectId", projectId);
        }

        cmd.CommandText = $"""
            SELECT id, project_id, document_project_id, document_slug, importance, audience, sort_order, notes, created_at, updated_at
            FROM agent_guidance_entries
            {where}
            ORDER BY
                CASE WHEN project_id = '_global' THEN 0 ELSE 1 END,
                sort_order ASC,
                CASE importance WHEN 'required' THEN 0 ELSE 1 END,
                document_project_id ASC,
                document_slug ASC,
                id ASC
            """;

        var entries = new List<AgentGuidanceEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(ReadEntry(reader));
        return entries;
    }

    public async Task<bool> DeleteAsync(int id, string? projectId = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = projectId is null
            ? "DELETE FROM agent_guidance_entries WHERE id = @id"
            : "DELETE FROM agent_guidance_entries WHERE id = @id AND project_id = @projectId";
        cmd.Parameters.AddWithValue("@id", id);
        if (projectId is not null)
            cmd.Parameters.AddWithValue("@projectId", projectId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<ResolvedAgentGuidance> ResolveAsync(string projectId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                g.id, g.project_id, g.document_project_id, g.document_slug,
                g.importance, g.audience, g.sort_order, g.notes, g.created_at, g.updated_at,
                d.id, d.project_id, d.slug, d.title, d.content, d.doc_type, d.tags, d.created_at, d.updated_at
            FROM agent_guidance_entries g
            JOIN documents d
              ON d.project_id = g.document_project_id
             AND d.slug = g.document_slug
            WHERE g.project_id = '_global'
               OR (@projectId != '_global' AND g.project_id = @projectId)
               OR (@projectId = '_global' AND g.project_id = '_global')
            ORDER BY
                CASE WHEN g.project_id = '_global' THEN 0 ELSE 1 END,
                g.sort_order ASC,
                CASE g.importance WHEN 'required' THEN 0 ELSE 1 END,
                g.document_project_id ASC,
                g.document_slug ASC,
                g.id ASC
            """;
        cmd.Parameters.AddWithValue("@projectId", projectId);

        var items = new List<AgentGuidanceEntryWithDocument>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new AgentGuidanceEntryWithDocument
            {
                Entry = ReadEntry(reader, offset: 0),
                Document = ReadDocument(reader, offset: 10)
            });
        }

        var sources = items.Select(item => new ResolvedAgentGuidanceSource
        {
            ScopeProjectId = item.Entry.ProjectId,
            DocumentProjectId = item.Document.ProjectId,
            Slug = item.Document.Slug,
            Title = item.Document.Title,
            DocType = item.Document.DocType,
            Tags = item.Document.Tags,
            Importance = item.Entry.Importance,
            Audience = item.Entry.Audience,
            SortOrder = item.Entry.SortOrder,
            Notes = item.Entry.Notes,
            UpdatedAt = item.Document.UpdatedAt
        }).ToList();

        return new ResolvedAgentGuidance
        {
            ProjectId = projectId,
            ResolvedAt = DateTime.UtcNow,
            Sources = sources,
            Content = BuildContent(projectId, items)
        };
    }

    private static void AddEntryParameters(SqliteCommand cmd, AgentGuidanceEntry entry)
    {
        cmd.Parameters.AddWithValue("@projectId", entry.ProjectId);
        cmd.Parameters.AddWithValue("@documentProjectId", entry.DocumentProjectId);
        cmd.Parameters.AddWithValue("@documentSlug", entry.DocumentSlug);
        cmd.Parameters.AddWithValue("@importance", entry.Importance.ToDbValue());
        cmd.Parameters.AddWithValue("@audience",
            entry.Audience is { Count: > 0 } ? JsonSerializer.Serialize(entry.Audience) : DBNull.Value);
        cmd.Parameters.AddWithValue("@sortOrder", entry.SortOrder);
        cmd.Parameters.AddWithValue("@notes", (object?)entry.Notes ?? DBNull.Value);
    }

    private static AgentGuidanceEntry ReadEntry(SqliteDataReader reader, int offset = 0)
    {
        var audienceJson = reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5);
        return new AgentGuidanceEntry
        {
            Id = reader.GetInt32(offset),
            ProjectId = reader.GetString(offset + 1),
            DocumentProjectId = reader.GetString(offset + 2),
            DocumentSlug = reader.GetString(offset + 3),
            Importance = EnumExtensions.ParseAgentGuidanceImportance(reader.GetString(offset + 4)),
            Audience = audienceJson is not null ? JsonSerializer.Deserialize<List<string>>(audienceJson) : null,
            SortOrder = reader.GetInt32(offset + 6),
            Notes = reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
            CreatedAt = DateTime.Parse(reader.GetString(offset + 8)),
            UpdatedAt = DateTime.Parse(reader.GetString(offset + 9))
        };
    }

    private static Document ReadDocument(SqliteDataReader reader, int offset)
    {
        var tagsJson = reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6);
        return new Document
        {
            Id = reader.GetInt32(offset),
            ProjectId = reader.GetString(offset + 1),
            Slug = reader.GetString(offset + 2),
            Title = reader.GetString(offset + 3),
            Content = reader.GetString(offset + 4),
            DocType = EnumExtensions.ParseDocType(reader.GetString(offset + 5)),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            CreatedAt = DateTime.Parse(reader.GetString(offset + 7)),
            UpdatedAt = DateTime.Parse(reader.GetString(offset + 8))
        };
    }

    private static string BuildContent(string projectId, IReadOnlyList<AgentGuidanceEntryWithDocument> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Den Agent Guidance for {projectId}");
        builder.AppendLine();
        builder.AppendLine("This guidance packet was resolved by Den from first-class agent guidance entries.");
        builder.AppendLine("Update the Den guidance entries or the referenced Den documents for policy changes; do not edit generated AGENTS.md snapshots as the source of truth.");
        builder.AppendLine();

        if (items.Count == 0)
        {
            builder.AppendLine("No agent guidance entries are configured for this project yet.");
            return builder.ToString().TrimEnd() + "\n";
        }

        builder.AppendLine("## Included Sources");
        builder.AppendLine();
        for (var i = 0; i < items.Count; i++)
        {
            var entry = items[i].Entry;
            var doc = items[i].Document;
            var audience = entry.Audience is { Count: > 0 } ? $"; audience: {string.Join(", ", entry.Audience)}" : "";
            builder.AppendLine($"{i + 1}. `{entry.Importance.ToDbValue()}` `{entry.ProjectId}` -> `{doc.ProjectId}/{doc.Slug}` ({doc.DocType.ToDbValue()}; order {entry.SortOrder}{audience})");
        }

        builder.AppendLine();
        builder.AppendLine("## Guidance Content");
        builder.AppendLine();

        foreach (var item in items)
        {
            var entry = item.Entry;
            var doc = item.Document;
            builder.AppendLine($"### {doc.Title}");
            builder.AppendLine();
            builder.AppendLine($"Source: `{doc.ProjectId}/{doc.Slug}`; scope: `{entry.ProjectId}`; type: `{doc.DocType.ToDbValue()}`; importance: `{entry.Importance.ToDbValue()}`; order: `{entry.SortOrder}`");
            if (doc.Tags is { Count: > 0 })
                builder.AppendLine($"Tags: {string.Join(", ", doc.Tags)}");
            if (entry.Audience is { Count: > 0 })
                builder.AppendLine($"Audience: {string.Join(", ", entry.Audience)}");
            if (!string.IsNullOrWhiteSpace(entry.Notes))
                builder.AppendLine($"Notes: {entry.Notes}");
            builder.AppendLine();
            builder.AppendLine(doc.Content.TrimEnd());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + "\n";
    }
}
