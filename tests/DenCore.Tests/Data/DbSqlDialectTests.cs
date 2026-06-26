using DenCore.Data;

namespace DenCore.Tests.Data;

public class DbSqlDialectTests
{
    [Fact]
    public void PostgresDialect_ProvidesProviderSpecificForms()
    {
        var sql = DbSqlDialect.Postgres;

        Assert.Equal(DatabaseProviderKind.Postgres, sql.Provider);
        Assert.Equal("to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')", sql.CurrentTimestamp);
        Assert.Throws<NotSupportedException>(() => sql.LastInsertedIdSelect);
        Assert.True(sql.SupportsReturningClause);
        Assert.Equal(" RETURNING id", sql.ReturningIdClause());
        Assert.Equal(" RETURNING usage_events.id", sql.ReturningIdClause("usage_events.id"));
        Assert.Equal("INSERT INTO message_reads", sql.InsertIgnoreInto("message_reads"));
        Assert.Equal(" ON CONFLICT DO NOTHING", sql.OnConflictDoNothing);
        Assert.Equal("metadata::jsonb #>> '{run_id}'", sql.JsonText("metadata", "$.run_id"));
        Assert.Equal("EXISTS (SELECT 1 FROM jsonb_array_elements_text(tags_json::jsonb) AS value WHERE value = @tag)", sql.JsonArrayContains("tags_json", "@tag"));
        Assert.Equal("to_char((last_heartbeat)::timestamp + (stale_after_seconds * INTERVAL '1 second'), 'YYYY-MM-DD HH24:MI:SS')", sql.AddSeconds("last_heartbeat", "stale_after_seconds"));
        Assert.Equal("(created_at)::timestamp <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC' - INTERVAL '15 minutes')", sql.OlderThanMinutes("created_at", 15));
        Assert.Equal("string_agg((worker_identity)::text, ',' ORDER BY id)", sql.StringAggregate("worker_identity", "id"));
        Assert.Equal("SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name", sql.TableExistsSql);
        Assert.Throws<NotSupportedException>(() => sql.KnowledgeFtsUpsertCommandText);
    }
}
