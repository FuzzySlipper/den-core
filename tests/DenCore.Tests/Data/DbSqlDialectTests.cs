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
        Assert.Equal("INSERT OR IGNORE INTO message_reads", sql.InsertIgnoreInto("message_reads"));
        Assert.Equal("", sql.OnConflictDoNothing);
        Assert.Equal("json_extract(metadata, '$.run_id')", sql.JsonText("metadata", "$.run_id"));
        Assert.Equal("EXISTS (SELECT 1 FROM json_each(tags_json) WHERE json_each.value = @tag)", sql.JsonArrayContains("tags_json", "@tag"));
        Assert.Equal("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name", sql.TableExistsSql);
    }

    [Fact]
    public void PostgresDialect_ProvidesProviderSpecificForms()
    {
        var sql = DbSqlDialect.Postgres;

        Assert.Equal(DatabaseProviderKind.Postgres, sql.Provider);
        Assert.Equal("CURRENT_TIMESTAMP", sql.CurrentTimestamp);
        Assert.Equal("INSERT INTO message_reads", sql.InsertIgnoreInto("message_reads"));
        Assert.Equal(" ON CONFLICT DO NOTHING", sql.OnConflictDoNothing);
        Assert.Equal("metadata::jsonb #>> '{run_id}'", sql.JsonText("metadata", "$.run_id"));
        Assert.Equal("EXISTS (SELECT 1 FROM jsonb_array_elements_text(tags_json::jsonb) AS value WHERE value = @tag)", sql.JsonArrayContains("tags_json", "@tag"));
        Assert.Equal("SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name", sql.TableExistsSql);
        Assert.Contains("ON CONFLICT (rowid) DO UPDATE", sql.KnowledgeFtsUpsertCommandText);
    }
}
