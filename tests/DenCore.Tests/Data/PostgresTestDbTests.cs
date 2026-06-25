using DenCore.Data;

namespace DenCore.Tests.Data;

public class PostgresTestDbTests
{
    [Fact]
    [Trait("Category", "PostgresProvider")]
    public async Task Harness_WhenConfigured_CreatesDisposableSchemaBackedFactory()
    {
        if (!PostgresTestDb.IsConfigured)
            return;

        var testDb = new PostgresTestDb();
        await testDb.InitializeAsync();
        try
        {
            Assert.Equal(DatabaseProviderKind.Postgres, testDb.Db.Provider);

            await using var conn = await testDb.Db.CreateConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_schema()";

            Assert.Equal(testDb.SchemaName, await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }
}
