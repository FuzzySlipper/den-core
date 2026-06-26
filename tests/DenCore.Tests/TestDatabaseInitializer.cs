using DenCore.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DenCore.Tests;

internal sealed class DatabaseInitializer : IDatabaseInitializer, IAsyncDisposable
{
    private readonly string? _adminConnectionString;
    private readonly string _schemaName = $"den_core_test_{Guid.NewGuid():N}";

    public DatabaseInitializer(string ignoredDatabasePath, NullLogger<DatabaseInitializer> ignoredLogger)
    {
        _adminConnectionString = Environment.GetEnvironmentVariable(PostgresTestDb.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
            throw new InvalidOperationException($"Set {PostgresTestDb.ConnectionStringEnvironmentVariable} to run Postgres-backed repository tests.");

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            SearchPath = _schemaName
        };
        ConnectionString = builder.ConnectionString;
    }

    public string ConnectionString { get; }

    public async Task InitializeAsync()
    {
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA {QuoteIdentifier(_schemaName)}";
            await create.ExecuteNonQueryAsync();
        }

        var db = new DbConnectionFactory(ConnectionString, DatabaseProviderKind.Postgres);
        var initializer = new PostgresDatabaseInitializer(db, NullLogger<PostgresDatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
            return;

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS {QuoteIdentifier(_schemaName)} CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
