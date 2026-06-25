using DenCore.Data;

namespace DenCore.Tests.Data;

public class DbSqlDialectTests
{
    [Fact]
    public void SqliteDialect_UsesCurrentRuntimeForms()
    {
        var sql = DbSqlDialect.Sqlite;

        Assert.Equal(DatabaseProviderKind.Sqlite, sql.Provider);
        Assert.Equal("datetime('now')", sql.CurrentTimestamp);
        Assert.Equal("SELECT last_insert_rowid();", sql.LastInsertedIdSelect);
        Assert.True(sql.SupportsReturningClause);
        Assert.Equal(" RETURNING id", sql.ReturningIdClause());
        Assert.Equal("INSERT OR IGNORE INTO message_reads", sql.InsertIgnoreInto("message_reads"));
        Assert.Equal("", sql.OnConflictDoNothing);
        Assert.Equal("json_extract(metadata, '$.run_id')", sql.JsonText("metadata", "$.run_id"));
        Assert.Equal("EXISTS (SELECT 1 FROM json_each(tags_json) WHERE json_each.value = @tag)", sql.JsonArrayContains("tags_json", "@tag"));
        Assert.Equal("created_at <= datetime('now', '-15 minutes')", sql.OlderThanMinutes("created_at", 15));
        Assert.Equal("GROUP_CONCAT(worker_identity)", sql.StringAggregate("worker_identity", "id"));
        Assert.Equal("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name", sql.TableExistsSql);
    }

    [Fact]
    public void PostgresDialect_ProvidesProviderSpecificForms()
    {
        var sql = DbSqlDialect.Postgres;

        Assert.Equal(DatabaseProviderKind.Postgres, sql.Provider);
        Assert.Equal("CURRENT_TIMESTAMP::text", sql.CurrentTimestamp);
        Assert.Throws<NotSupportedException>(() => sql.LastInsertedIdSelect);
        Assert.True(sql.SupportsReturningClause);
        Assert.Equal(" RETURNING id", sql.ReturningIdClause());
        Assert.Equal(" RETURNING usage_events.id", sql.ReturningIdClause("usage_events.id"));
        Assert.Equal("INSERT INTO message_reads", sql.InsertIgnoreInto("message_reads"));
        Assert.Equal(" ON CONFLICT DO NOTHING", sql.OnConflictDoNothing);
        Assert.Equal("metadata::jsonb #>> '{run_id}'", sql.JsonText("metadata", "$.run_id"));
        Assert.Equal("EXISTS (SELECT 1 FROM jsonb_array_elements_text(tags_json::jsonb) AS value WHERE value = @tag)", sql.JsonArrayContains("tags_json", "@tag"));
        Assert.Equal("((last_heartbeat)::timestamptz + (stale_after_seconds * INTERVAL '1 second'))::text", sql.AddSeconds("last_heartbeat", "stale_after_seconds"));
        Assert.Equal("(created_at)::timestamptz <= (CURRENT_TIMESTAMP - INTERVAL '15 minutes')", sql.OlderThanMinutes("created_at", 15));
        Assert.Equal("string_agg((worker_identity)::text, ',' ORDER BY id)", sql.StringAggregate("worker_identity", "id"));
        Assert.Equal("SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name", sql.TableExistsSql);
        Assert.Throws<NotSupportedException>(() => sql.KnowledgeFtsUpsertCommandText);
    }
}
