namespace DenCore.Data;

public sealed class DbSqlDialect
{
    public static readonly DbSqlDialect Sqlite = new(DatabaseProviderKind.Sqlite);
    public static readonly DbSqlDialect Postgres = new(DatabaseProviderKind.Postgres);

    private DbSqlDialect(DatabaseProviderKind provider)
    {
        Provider = provider;
    }

    public DatabaseProviderKind Provider { get; }

    public string CurrentTimestamp => Provider switch
    {
        DatabaseProviderKind.Sqlite => "datetime('now')",
        DatabaseProviderKind.Postgres => "CURRENT_TIMESTAMP",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string LastInsertedIdSelect => Provider switch
    {
        DatabaseProviderKind.Sqlite => "SELECT last_insert_rowid();",
        DatabaseProviderKind.Postgres => throw new NotSupportedException("Postgres identity reads must use INSERT ... RETURNING via ReturningIdClause."),
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public bool SupportsReturningClause => Provider switch
    {
        DatabaseProviderKind.Sqlite or DatabaseProviderKind.Postgres => true,
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string ReturningIdClause(string columnExpression = "id") => Provider switch
    {
        DatabaseProviderKind.Sqlite or DatabaseProviderKind.Postgres => $" RETURNING {columnExpression}",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string InsertIgnoreInto(string tableName) => Provider switch
    {
        DatabaseProviderKind.Sqlite => $"INSERT OR IGNORE INTO {tableName}",
        DatabaseProviderKind.Postgres => $"INSERT INTO {tableName}",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string OnConflictDoNothing => Provider switch
    {
        DatabaseProviderKind.Sqlite => "",
        DatabaseProviderKind.Postgres => " ON CONFLICT DO NOTHING",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string JsonText(string columnExpression, string jsonPath) => Provider switch
    {
        DatabaseProviderKind.Sqlite => $"json_extract({columnExpression}, '{jsonPath}')",
        DatabaseProviderKind.Postgres => $"{columnExpression}::jsonb #>> '{{{ToPostgresJsonPath(jsonPath)}}}'",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string JsonArrayContains(string columnExpression, string valueExpression) => Provider switch
    {
        DatabaseProviderKind.Sqlite =>
            $"EXISTS (SELECT 1 FROM json_each({columnExpression}) WHERE json_each.value = {valueExpression})",
        DatabaseProviderKind.Postgres =>
            $"EXISTS (SELECT 1 FROM jsonb_array_elements_text({columnExpression}::jsonb) AS value WHERE value = {valueExpression})",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string AddSeconds(string timestampExpression, string secondsExpression) => Provider switch
    {
        DatabaseProviderKind.Sqlite => $"datetime({timestampExpression}, '+' || {secondsExpression} || ' seconds')",
        DatabaseProviderKind.Postgres => $"{timestampExpression} + ({secondsExpression} * INTERVAL '1 second')",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string TableExistsSql => Provider switch
    {
        DatabaseProviderKind.Sqlite => "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
        DatabaseProviderKind.Postgres => "SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string IndexExistsSql => Provider switch
    {
        DatabaseProviderKind.Sqlite => "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name",
        DatabaseProviderKind.Postgres => "SELECT 1 FROM pg_catalog.pg_indexes WHERE schemaname = current_schema() AND indexname = @name",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string KnowledgeFtsUpsertCommandText => Provider switch
    {
        DatabaseProviderKind.Sqlite => """
            INSERT OR REPLACE INTO knowledge_entries_fts(rowid, slug, title, summary, body_markdown)
            VALUES (@entryId, @slug, @title, @summary, @body)
            """,
        DatabaseProviderKind.Postgres => """
            /* Postgres FTS replacement is owned by task #3324. */
            INSERT INTO knowledge_entries_fts(rowid, slug, title, summary, body_markdown)
            VALUES (@entryId, @slug, @title, @summary, @body)
            ON CONFLICT (rowid) DO UPDATE SET
                slug = EXCLUDED.slug,
                title = EXCLUDED.title,
                summary = EXCLUDED.summary,
                body_markdown = EXCLUDED.body_markdown
            """,
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    public string FtsMatchExpression(string ftsTable, string parameterName) => Provider switch
    {
        DatabaseProviderKind.Sqlite => $"{ftsTable} MATCH {parameterName}",
        DatabaseProviderKind.Postgres => $"to_tsvector('english', {ftsTable}) @@ websearch_to_tsquery('english', {parameterName})",
        _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
    };

    private static string ToPostgresJsonPath(string jsonPath)
    {
        var trimmed = jsonPath.Trim();
        if (trimmed == "$")
            return "";
        if (!trimmed.StartsWith("$.", StringComparison.Ordinal))
            throw new ArgumentException($"Only simple JSON paths are supported: {jsonPath}", nameof(jsonPath));
        return string.Join(",", trimmed[2..].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
