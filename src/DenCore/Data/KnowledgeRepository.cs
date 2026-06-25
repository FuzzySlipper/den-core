using System.Text.Json;
using System.Data.Common;
using DenCore.Models;

namespace DenCore.Data;

public interface IKnowledgeRepository
{
    Task<KnowledgeEntry> UpsertAsync(KnowledgeEntry entry, string? changeNote = null);
    Task<KnowledgeEntry?> GetBySlugAsync(string slug, bool includeArchived = false);
    Task<KnowledgeEntry?> GetByIdAsync(int id, bool includeArchived = false);
    Task<List<KnowledgeEntrySummary>> ListAsync(KnowledgeListQuery query);
    Task<List<KnowledgeSearchResult>> SearchAsync(KnowledgeSearchQuery query);
    Task<List<KnowledgeRevisionSummary>> ListRevisionsAsync(string slug);
}

public sealed class KnowledgeRepository : IKnowledgeRepository
{
    private readonly DbConnectionFactory _db;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public KnowledgeRepository(DbConnectionFactory db) => _db = db;

    public async Task<KnowledgeEntry> UpsertAsync(KnowledgeEntry entry, string? changeNote = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = conn.BeginTransaction();

        // Check if this is an update (existing entry)
        int? existingId = null;
        int nextRevision = 1;
        KnowledgeEntry? existing = null;

        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.Transaction = tx;
            checkCmd.CommandText = """
                SELECT id, title, summary, body_markdown, kind, status, curation_state,
                       audience_json, aliases_json, source_refs_json, accuracy_notes,
                       replacement_slug, last_reviewed_at, review_due_at, created_by, updated_by,
                       created_at, updated_at
                FROM knowledge_entries WHERE slug = @slug
                """;
            checkCmd.AddParameterWithValue("@slug", entry.Slug);
            await using var reader = await checkCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                existingId = reader.GetInt32(0);
                existing = new KnowledgeEntry
                {
                    Id = existingId.Value,
                    Slug = entry.Slug,
                    Title = reader.GetString(1),
                    Summary = reader.IsDBNull(2) ? null : reader.GetString(2),
                    BodyMarkdown = reader.GetString(3),
                    Kind = reader.GetString(4),
                    Status = reader.GetString(5),
                    CurationState = reader.GetString(6),
                };
                // Get current max revision
                nextRevision = 1;
            }
        }

        if (existingId.HasValue)
        {
            // Get the current max revision number
            await using var revCmd = conn.CreateCommand();
            revCmd.Transaction = tx;
            revCmd.CommandText = "SELECT COALESCE(MAX(revision_number), 0) + 1 FROM knowledge_entry_revisions WHERE entry_id = @entryId";
            revCmd.AddParameterWithValue("@entryId", existingId.Value);
            var revResult = await revCmd.ExecuteScalarAsync();
            nextRevision = Convert.ToInt32(revResult);

            // Archive current state as revision
            await using var revInsertCmd = conn.CreateCommand();
            revInsertCmd.Transaction = tx;
            revInsertCmd.CommandText = """
                INSERT INTO knowledge_entry_revisions
                    (entry_id, revision_number, title, summary, body_markdown, kind, status,
                     curation_state, tags_json, audience_json, aliases_json, source_refs_json,
                     accuracy_notes, replacement_slug, changed_by, change_note)
                VALUES (@entryId, @revNum, @title, @summary, @body, @kind, @status,
                        @curationState, @tagsJson, @audienceJson, @aliasesJson, @sourceRefsJson,
                        @accuracyNotes, @replacementSlug, @changedBy, @changeNote)
                """;
            revInsertCmd.AddParameterWithValue("@entryId", existingId.Value);
            revInsertCmd.AddParameterWithValue("@revNum", nextRevision);
            revInsertCmd.AddParameterWithValue("@title", existing?.Title ?? entry.Title);
            revInsertCmd.AddParameterWithValue("@summary", (object?)existing?.Summary ?? DBNull.Value);
            revInsertCmd.AddParameterWithValue("@body", existing?.BodyMarkdown ?? entry.BodyMarkdown);
            revInsertCmd.AddParameterWithValue("@kind", existing?.Kind ?? entry.Kind);
            revInsertCmd.AddParameterWithValue("@status", existing?.Status ?? entry.Status);
            revInsertCmd.AddParameterWithValue("@curationState", existing?.CurationState ?? entry.CurationState);
            revInsertCmd.AddParameterWithValue("@tagsJson", DBNull.Value); // loaded separately
            revInsertCmd.AddParameterWithValue("@audienceJson", DBNull.Value);
            revInsertCmd.AddParameterWithValue("@aliasesJson", DBNull.Value);
            revInsertCmd.AddParameterWithValue("@sourceRefsJson", DBNull.Value);
            revInsertCmd.AddParameterWithValue("@accuracyNotes", DBNull.Value);
            revInsertCmd.AddParameterWithValue("@replacementSlug", DBNull.Value);
            revInsertCmd.AddParameterWithValue("@changedBy", (object?)entry.UpdatedBy ?? DBNull.Value);
            revInsertCmd.AddParameterWithValue("@changeNote", (object?)changeNote ?? DBNull.Value);
            await revInsertCmd.ExecuteNonQueryAsync();
        }

        // Upsert the entry
        await using var upsertCmd = conn.CreateCommand();
        upsertCmd.Transaction = tx;
        upsertCmd.CommandText = """
            INSERT INTO knowledge_entries
                (slug, title, summary, body_markdown, kind, status, curation_state,
                 audience_json, aliases_json, source_refs_json, accuracy_notes,
                 replacement_slug, last_reviewed_at, review_due_at, created_by, updated_by)
            VALUES (@slug, @title, @summary, @body, @kind, @status, @curationState,
                    @audienceJson, @aliasesJson, @sourceRefsJson, @accuracyNotes,
                    @replacementSlug, @lastReviewedAt, @reviewDueAt, @createdBy, @updatedBy)
            ON CONFLICT(slug) DO UPDATE SET
                title = excluded.title,
                summary = excluded.summary,
                body_markdown = excluded.body_markdown,
                kind = excluded.kind,
                status = excluded.status,
                curation_state = excluded.curation_state,
                audience_json = excluded.audience_json,
                aliases_json = excluded.aliases_json,
                source_refs_json = excluded.source_refs_json,
                accuracy_notes = excluded.accuracy_notes,
                replacement_slug = excluded.replacement_slug,
                last_reviewed_at = excluded.last_reviewed_at,
                review_due_at = excluded.review_due_at,
                updated_by = excluded.updated_by,
                updated_at = datetime('now')
            RETURNING id, slug, title, summary, body_markdown, kind, status, curation_state,
                      audience_json, aliases_json, source_refs_json, accuracy_notes,
                      replacement_slug, last_reviewed_at, review_due_at,
                      created_by, updated_by, created_at, updated_at
            """;

        upsertCmd.AddParameterWithValue("@slug", entry.Slug);
        upsertCmd.AddParameterWithValue("@title", entry.Title);
        upsertCmd.AddParameterWithValue("@summary", (object?)entry.Summary ?? DBNull.Value);
        upsertCmd.AddParameterWithValue("@body", entry.BodyMarkdown);
        upsertCmd.AddParameterWithValue("@kind", entry.Kind);
        upsertCmd.AddParameterWithValue("@status", entry.Status);
        upsertCmd.AddParameterWithValue("@curationState", entry.CurationState);
        upsertCmd.AddParameterWithValue("@audienceJson",
            entry.Audience is { Count: > 0 } ? JsonSerializer.Serialize(entry.Audience) : DBNull.Value);
        upsertCmd.AddParameterWithValue("@aliasesJson",
            entry.Aliases is { Count: > 0 } ? JsonSerializer.Serialize(entry.Aliases) : DBNull.Value);
        upsertCmd.AddParameterWithValue("@sourceRefsJson",
            entry.SourceRefs is { Count: > 0 } ? JsonSerializer.Serialize(entry.SourceRefs) : DBNull.Value);
        upsertCmd.AddParameterWithValue("@accuracyNotes",
            (object?)entry.AccuracyNotes ?? DBNull.Value);
        upsertCmd.AddParameterWithValue("@replacementSlug",
            (object?)entry.ReplacementSlug ?? DBNull.Value);
        upsertCmd.AddParameterWithValue("@lastReviewedAt",
            entry.LastReviewedAt is DateTime lr ? lr.ToString("o") : DBNull.Value);
        upsertCmd.AddParameterWithValue("@reviewDueAt",
            entry.ReviewDueAt is DateTime rd ? rd.ToString("o") : DBNull.Value);
        upsertCmd.AddParameterWithValue("@createdBy",
            (object?)entry.CreatedBy ?? DBNull.Value);
        upsertCmd.AddParameterWithValue("@updatedBy",
            (object?)entry.UpdatedBy ?? DBNull.Value);

        await using var upsertReader = await upsertCmd.ExecuteReaderAsync();
        await upsertReader.ReadAsync();
        var result = ReadEntry(upsertReader);
        await upsertReader.CloseAsync();

        // Manage tags: delete old, insert new
        if (existingId.HasValue)
        {
            await using var delTags = conn.CreateCommand();
            delTags.Transaction = tx;
            delTags.CommandText = "DELETE FROM knowledge_entry_tags WHERE entry_id = @entryId";
            delTags.AddParameterWithValue("@entryId", existingId.Value);
            await delTags.ExecuteNonQueryAsync();
        }

        if (entry.Tags is { Count: > 0 })
        {
            foreach (var tag in entry.Tags)
            {
                await using var tagCmd = conn.CreateCommand();
                tagCmd.Transaction = tx;
                tagCmd.CommandText = """
                    INSERT INTO knowledge_entry_tags (entry_id, tag)
                    VALUES (@entryId, @tag)
                    """;
                tagCmd.AddParameterWithValue("@entryId", result.Id);
                tagCmd.AddParameterWithValue("@tag", tag);
                await tagCmd.ExecuteNonQueryAsync();
            }
        }

        // Standalone FTS: keep in sync with current entry state
        await RefreshFtsRowAsync(conn, tx, result.Id, entry);

        await tx.CommitAsync();
        result.Tags = entry.Tags;
        return result;
    }

    public async Task<KnowledgeEntry?> GetBySlugAsync(string slug, bool includeArchived = false)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var archivedFilter = includeArchived ? "" : "AND ke.status != 'archived'";
        cmd.CommandText = $"""
            SELECT ke.id, ke.slug, ke.title, ke.summary, ke.body_markdown, ke.kind,
                   ke.status, ke.curation_state,
                   ke.audience_json, ke.aliases_json, ke.source_refs_json,
                   ke.accuracy_notes, ke.replacement_slug,
                   ke.last_reviewed_at, ke.review_due_at,
                   ke.created_by, ke.updated_by, ke.created_at, ke.updated_at
            FROM knowledge_entries ke
            WHERE ke.slug = @slug {archivedFilter}
            """;
        cmd.AddParameterWithValue("@slug", slug);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var entry = ReadEntry(reader);
        entry.Tags = await LoadTagsAsync(conn, entry.Id);
        return entry;
    }

    public async Task<KnowledgeEntry?> GetByIdAsync(int id, bool includeArchived = false)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var archivedFilter = includeArchived ? "" : "AND ke.status != 'archived'";
        cmd.CommandText = $"""
            SELECT ke.id, ke.slug, ke.title, ke.summary, ke.body_markdown, ke.kind,
                   ke.status, ke.curation_state,
                   ke.audience_json, ke.aliases_json, ke.source_refs_json,
                   ke.accuracy_notes, ke.replacement_slug,
                   ke.last_reviewed_at, ke.review_due_at,
                   ke.created_by, ke.updated_by, ke.created_at, ke.updated_at
            FROM knowledge_entries ke
            WHERE ke.id = @id {archivedFilter}
            """;
        cmd.AddParameterWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var entry = ReadEntry(reader);
        entry.Tags = await LoadTagsAsync(conn, entry.Id);
        return entry;
    }

    public async Task<List<KnowledgeEntrySummary>> ListAsync(KnowledgeListQuery query)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        BuildStatusFilter(where, cmd, query.IncludeDeprecated, query.IncludeUnreviewed, query.IncludeArchived, query.Status);

        if (query.Kind is not null)
        {
            where.Add("ke.kind = @kind");
            cmd.AddParameterWithValue("@kind", query.Kind);
        }

        if (query.Audience is { Length: > 0 })
        {
            foreach (var (aud, i) in query.Audience.Select((a, i) => (a, i)))
            {
                var p = $"@aud{i}";
                where.Add($"ke.audience_json IS NOT NULL AND json_each(ke.audience_json) IS NOT NULL AND EXISTS (SELECT 1 FROM json_each(ke.audience_json) WHERE json_each.value = {p})");
                cmd.AddParameterWithValue(p, aud);
            }
        }

        // Required tags: strict AND
        if (query.RequiredTags is { Length: > 0 })
        {
            for (var i = 0; i < query.RequiredTags.Length; i++)
            {
                var p = $"@reqTag{i}";
                where.Add($"EXISTS (SELECT 1 FROM knowledge_entry_tags ket WHERE ket.entry_id = ke.id AND ket.tag = {p})");
                cmd.AddParameterWithValue(p, query.RequiredTags[i]);
            }
        }

        // Any tags: OR filter
        if (query.AnyTags is { Length: > 0 })
        {
            var tagConditions = new List<string>();
            for (var i = 0; i < query.AnyTags.Length; i++)
            {
                var p = $"@anyTag{i}";
                tagConditions.Add($"EXISTS (SELECT 1 FROM knowledge_entry_tags ket2 WHERE ket2.entry_id = ke.id AND ket2.tag = {p})");
                cmd.AddParameterWithValue(p, query.AnyTags[i]);
            }
            where.Add($"({string.Join(" OR ", tagConditions)})");
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
        cmd.CommandText = $"""
            SELECT ke.id, ke.slug, ke.title, ke.summary, ke.kind, ke.status, ke.curation_state,
                   ke.audience_json, ke.aliases_json, ke.source_refs_json,
                   ke.accuracy_notes, ke.replacement_slug,
                   ke.last_reviewed_at, ke.review_due_at,
                   ke.created_by, ke.updated_by, ke.created_at, ke.updated_at
            FROM knowledge_entries ke
            {whereClause}
            ORDER BY ke.updated_at DESC
            LIMIT @limit OFFSET @offset
            """;
        cmd.AddParameterWithValue("@limit", Math.Min(query.Limit, 200));
        cmd.AddParameterWithValue("@offset", query.Offset);

        var results = new List<KnowledgeEntrySummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadSummary(reader));
        }

        // Load tags for each result
        foreach (var r in results)
        {
            r.Tags = await LoadTagsAsync(conn, r.Id);
        }

        return results;
    }

    public async Task<List<KnowledgeSearchResult>> SearchAsync(KnowledgeSearchQuery query)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Build WHERE conditions
        var ftsQuery = DenCore.Llm.FtsQuerySanitizer.Sanitize(query.Query);
        if (ftsQuery is null)
            return []; // No searchable terms
        var conditions = new List<string> { "knowledge_entries_fts MATCH @query" };
        cmd.AddParameterWithValue("@query", ftsQuery);

        // Status filter
        if (query.Status is not null)
        {
            var statuses = query.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sc = new List<string>();
            foreach (var (s, i) in statuses.Select((s, i) => (s, i)))
            {
                var p = $"@status{i}";
                sc.Add($"ke.status = {p}");
                cmd.AddParameterWithValue(p, s);
            }
            conditions.Add($"({string.Join(" OR ", sc)})");
        }
        else
        {
            conditions.Add("ke.status = 'reviewed'");
            if (query.IncludeDeprecated)
                conditions[^1] = $"({conditions[^1]} OR ke.status = 'deprecated')";
            if (query.IncludeUnreviewed)
                conditions[^1] = $"({conditions[^1]} OR ke.status = 'draft' OR ke.status = 'needs_review')";
        }
        if (!query.IncludeArchived)
            conditions.Add("ke.status != 'archived'");

        if (query.Kind is not null)
        {
            conditions.Add("ke.kind = @kind");
            cmd.AddParameterWithValue("@kind", query.Kind);
        }

        if (query.Audience is { Length: > 0 })
        {
            foreach (var (aud, i) in query.Audience.Select((a, i) => (a, i)))
            {
                var p = $"@aud{i}";
                conditions.Add($"ke.audience_json IS NOT NULL AND EXISTS (SELECT 1 FROM json_each(ke.audience_json) WHERE json_each.value = {p})");
                cmd.AddParameterWithValue(p, aud);
            }
        }

        // Required tags: strict AND
        if (query.RequiredTags is { Length: > 0 })
        {
            for (var i = 0; i < query.RequiredTags.Length; i++)
            {
                var p = $"@reqTag{i}";
                conditions.Add($"EXISTS (SELECT 1 FROM knowledge_entry_tags ket WHERE ket.entry_id = ke.id AND ket.tag = {p})");
                cmd.AddParameterWithValue(p, query.RequiredTags[i]);
            }
        }

        // Any tags: OR filter
        if (query.AnyTags is { Length: > 0 })
        {
            var tc = new List<string>();
            for (var i = 0; i < query.AnyTags.Length; i++)
            {
                var p = $"@anyTag{i}";
                tc.Add($"EXISTS (SELECT 1 FROM knowledge_entry_tags ket2 WHERE ket2.entry_id = ke.id AND ket2.tag = {p})");
                cmd.AddParameterWithValue(p, query.AnyTags[i]);
            }
            conditions.Add($"({string.Join(" OR ", tc)})");
        }

        var whereClause = $"WHERE {string.Join(" AND ", conditions)}";
        cmd.CommandText = $"""
            SELECT ke.slug, ke.title, ke.summary, ke.kind, ke.status, ke.curation_state,
                   ke.audience_json, ke.aliases_json, ke.source_refs_json,
                   CASE WHEN ke.summary IS NOT NULL THEN ke.summary ELSE substr(ke.body_markdown, 1, 200) END as snippet,
                   0.0 as rank,
                   ke.updated_at, ke.last_reviewed_at
            FROM knowledge_entries_fts fts
            JOIN knowledge_entries ke ON ke.id = fts.rowid
            {whereClause}
            ORDER BY rank
            LIMIT @limit
            """;
        cmd.AddParameterWithValue("@limit", Math.Min(query.Limit, 200));

        var results = new List<KnowledgeSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new KnowledgeSearchResult
            {
                Slug = reader.GetString(0),
                Title = reader.GetString(1),
                Summary = reader.IsDBNull(2) ? null : reader.GetString(2),
                Kind = reader.GetString(3),
                Status = reader.GetString(4),
                CurationState = reader.GetString(5),
                Audience = reader.IsDBNull(6) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [],
                Aliases = reader.IsDBNull(7) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [],
                SourceRefs = reader.IsDBNull(8) ? [] : JsonSerializer.Deserialize<List<KnowledgeSourceRef>>(reader.GetString(8), JsonOpts) ?? [],
                Snippet = reader.GetString(9),
                Rank = reader.GetDouble(10),
                UpdatedAt = DateTime.Parse(reader.GetString(11)),
                LastReviewedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12))
            });
        }

        // Load tags for each result
        foreach (var r in results)
        {
            r.Tags = await LoadTagsBySlugAsync(conn, r.Slug);
        }

        return results;
    }

    public async Task<List<KnowledgeRevisionSummary>> ListRevisionsAsync(string slug)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT kr.id, kr.entry_id, kr.revision_number, kr.title, kr.kind, kr.status,
                   kr.curation_state, kr.change_note, kr.changed_by, kr.created_at
            FROM knowledge_entry_revisions kr
            JOIN knowledge_entries ke ON ke.id = kr.entry_id
            WHERE ke.slug = @slug
            ORDER BY kr.revision_number DESC
            """;
        cmd.AddParameterWithValue("@slug", slug);

        var results = new List<KnowledgeRevisionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new KnowledgeRevisionSummary
            {
                Id = reader.GetInt32(0),
                EntryId = reader.GetInt32(1),
                RevisionNumber = reader.GetInt32(2),
                Title = reader.GetString(3),
                Kind = reader.GetString(4),
                Status = reader.GetString(5),
                CurationState = reader.GetString(6),
                ChangeNote = reader.IsDBNull(7) ? null : reader.GetString(7),
                ChangedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAt = DateTime.Parse(reader.GetString(9))
            });
        }
        return results;
    }

    // ── Private helpers ──

    private static KnowledgeEntry ReadEntry(DbDataReader reader)
    {
        return new KnowledgeEntry
        {
            Id = reader.GetInt32(0),
            Slug = reader.GetString(1),
            Title = reader.GetString(2),
            Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
            BodyMarkdown = reader.GetString(4),
            Kind = reader.GetString(5),
            Status = reader.GetString(6),
            CurationState = reader.GetString(7),
            Audience = reader.IsDBNull(8) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [],
            Aliases = reader.IsDBNull(9) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? [],
            SourceRefs = reader.IsDBNull(10) ? [] : JsonSerializer.Deserialize<List<KnowledgeSourceRef>>(reader.GetString(10), JsonOpts) ?? [],
            AccuracyNotes = reader.IsDBNull(11) ? null : reader.GetString(11),
            ReplacementSlug = reader.IsDBNull(12) ? null : reader.GetString(12),
            LastReviewedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
            ReviewDueAt = reader.IsDBNull(14) ? null : DateTime.Parse(reader.GetString(14)),
            CreatedBy = reader.IsDBNull(15) ? null : reader.GetString(15),
            UpdatedBy = reader.IsDBNull(16) ? null : reader.GetString(16),
            CreatedAt = DateTime.Parse(reader.GetString(17)),
            UpdatedAt = DateTime.Parse(reader.GetString(18))
        };
    }

    private static KnowledgeEntrySummary ReadSummary(DbDataReader reader)
    {
        return new KnowledgeEntrySummary
        {
            Id = reader.GetInt32(0),
            Slug = reader.GetString(1),
            Title = reader.GetString(2),
            Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
            Kind = reader.GetString(4),
            Status = reader.GetString(5),
            CurationState = reader.GetString(6),
            Audience = reader.IsDBNull(7) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [],
            Aliases = reader.IsDBNull(8) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [],
            SourceRefs = reader.IsDBNull(9) ? [] : JsonSerializer.Deserialize<List<KnowledgeSourceRef>>(reader.GetString(9), JsonOpts) ?? [],
            AccuracyNotes = reader.IsDBNull(10) ? null : reader.GetString(10),
            ReplacementSlug = reader.IsDBNull(11) ? null : reader.GetString(11),
            LastReviewedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
            ReviewDueAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
            CreatedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
            UpdatedBy = reader.IsDBNull(15) ? null : reader.GetString(15),
            CreatedAt = DateTime.Parse(reader.GetString(16)),
            UpdatedAt = DateTime.Parse(reader.GetString(17))
        };
    }

    private static async Task<List<string>> LoadTagsAsync(DbConnection conn, int entryId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag FROM knowledge_entry_tags WHERE entry_id = @entryId ORDER BY tag";
        cmd.AddParameterWithValue("@entryId", entryId);
        var tags = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tags.Add(reader.GetString(0));
        return tags;
    }

    private static async Task<List<string>> LoadTagsBySlugAsync(DbConnection conn, string slug)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ket.tag FROM knowledge_entry_tags ket
            JOIN knowledge_entries ke ON ke.id = ket.entry_id
            WHERE ke.slug = @slug
            ORDER BY ket.tag
            """;
        cmd.AddParameterWithValue("@slug", slug);
        var tags = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tags.Add(reader.GetString(0));
        return tags;
    }

    internal static async Task RefreshFtsRowAsync(DbConnection conn, DbTransaction tx, int entryId, KnowledgeEntry entry)
    {
        // Standalone FTS table: use INSERT OR REPLACE for atomic upsert
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO knowledge_entries_fts(rowid, slug, title, summary, body_markdown)
            VALUES (@entryId, @slug, @title, @summary, @body)
            """;
        cmd.AddParameterWithValue("@entryId", entryId);
        cmd.AddParameterWithValue("@slug", entry.Slug);
        cmd.AddParameterWithValue("@title", entry.Title);
        cmd.AddParameterWithValue("@summary", (object?)entry.Summary ?? "");
        cmd.AddParameterWithValue("@body", entry.BodyMarkdown);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void BuildStatusFilter(List<string> where, DbCommand cmd,
        bool includeDeprecated, bool includeUnreviewed, bool includeArchived, string? explicitStatus)
    {
        if (explicitStatus is not null)
        {
            // Explicit status filter overrides defaults
            var statuses = explicitStatus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var statusConditions = new List<string>();
            foreach (var (s, i) in statuses.Select((s, i) => (s, i)))
            {
                var p = $"@status{i}";
                statusConditions.Add($"ke.status = {p}");
                cmd.AddParameterWithValue(p, s);
            }
            where.Add($"({string.Join(" OR ", statusConditions)})");

            // Still apply archived filter if not explicitly included
            if (!includeArchived && !statuses.Contains(KnowledgeEntryStatuses.Archived))
                where.Add("ke.status != 'archived'");
        }
        else
        {
            // Default: reviewed only
            where.Add("ke.status = 'reviewed'");

            if (includeDeprecated)
            {
                where[^1] = $"({where[^1]} OR ke.status = 'deprecated')";
            }

            if (includeUnreviewed)
            {
                where[^1] = $"({where[^1]} OR ke.status = 'draft' OR ke.status = 'needs_review')";
            }

            if (!includeArchived)
            {
                where.Add("ke.status != 'archived'");
            }
        }
    }

    private static void BuildSearchStatusFilter(List<string> where, DbCommand cmd, string tableAlias,
        bool includeDeprecated, bool includeUnreviewed, bool includeArchived, string? explicitStatus)
    {
        if (explicitStatus is not null)
        {
            var statuses = explicitStatus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var statusConditions = new List<string>();
            foreach (var (s, i) in statuses.Select((s, i) => (s, i)))
            {
                var p = $"@status{i}";
                statusConditions.Add($"{tableAlias}.status = {p}");
                cmd.AddParameterWithValue(p, s);
            }
            where.Add($"({string.Join(" OR ", statusConditions)})");

            if (!includeArchived && !statuses.Contains(KnowledgeEntryStatuses.Archived))
                where.Add($"{tableAlias}.status != 'archived'");
        }
        else
        {
            where.Add($"{tableAlias}.status = 'reviewed'");

            if (includeDeprecated)
            {
                where[^1] = $"({where[^1]} OR {tableAlias}.status = 'deprecated')";
            }

            if (includeUnreviewed)
            {
                where[^1] = $"({where[^1]} OR {tableAlias}.status = 'draft' OR {tableAlias}.status = 'needs_review')";
            }

            if (!includeArchived)
            {
                where.Add($"{tableAlias}.status != 'archived'");
            }
        }
    }
}
