using DenCore.Data;
using Npgsql;

namespace DenCore.Tests;

/// <summary>
/// Creates an isolated Postgres schema for provider-harness tests when explicitly configured.
/// This does not run den_core migrations; #3323 owns schema translation.
/// </summary>
public sealed class PostgresTestDb : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable = "DEN_CORE_TEST_POSTGRES_CONNECTION_STRING";

    private string? _adminConnectionString;

    public string SchemaName { get; } = $"den_core_test_{Guid.NewGuid():N}";
    public string ConnectionString { get; private set; } = "";
    public DbConnectionFactory Db { get; private set; } = null!;

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable));

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            throw new InvalidOperationException(
                $"Set {ConnectionStringEnvironmentVariable} to run Postgres provider harness tests.");
        }

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();

        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA {QuoteIdentifier(SchemaName)}";
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            SearchPath = SchemaName
        };
        ConnectionString = builder.ConnectionString;
        Db = new DbConnectionFactory(ConnectionString, DatabaseProviderKind.Postgres);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
            return;

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS {QuoteIdentifier(SchemaName)} CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
