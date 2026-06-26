using DenCore.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Collections.Concurrent;

namespace DenCore.Service.Tests;

internal sealed class DatabaseInitializer : IDatabaseInitializer, IAsyncDisposable
{
    public const string ConnectionStringEnvironmentVariable = "DEN_CORE_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly ConcurrentDictionary<string, SchemaLease> Leases = new();

    private readonly string _leaseKey;
    private readonly SchemaLease _lease;

    public DatabaseInitializer(string leaseKey, NullLogger<DatabaseInitializer> ignoredLogger)
    {
        _leaseKey = leaseKey;
        _lease = GetOrCreateLease(leaseKey);
        ConnectionString = _lease.ConnectionString;
    }

    public string ConnectionString { get; }

    public static string GetConnectionString(string leaseKey) =>
        GetOrCreateLease(leaseKey).ConnectionString;

    public async Task InitializeAsync()
    {
        var db = new DbConnectionFactory(ConnectionString, DatabaseProviderKind.Postgres);
        var initializer = new PostgresDatabaseInitializer(db, NullLogger<PostgresDatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
    }

    public ValueTask DisposeAsync() => DisposeLeaseAsync(_leaseKey);

    public static async ValueTask DisposeLeaseAsync(string leaseKey)
    {
        if (!Leases.TryRemove(leaseKey, out var lease))
            return;

        await using var admin = new NpgsqlConnection(lease.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS {QuoteIdentifier(lease.SchemaName)} CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    private static SchemaLease GetOrCreateLease(string leaseKey) =>
        Leases.GetOrAdd(leaseKey, static _ =>
        {
            var adminConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(adminConnectionString))
                throw new InvalidOperationException($"Set {ConnectionStringEnvironmentVariable} to run Postgres-backed service tests.");

            var schemaName = $"den_core_test_{Guid.NewGuid():N}";
            using var admin = new NpgsqlConnection(adminConnectionString);
            admin.Open();
            using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE SCHEMA {QuoteIdentifier(schemaName)}";
                create.ExecuteNonQuery();
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                SearchPath = schemaName
            };
            return new SchemaLease(adminConnectionString, schemaName, builder.ConnectionString);
        });

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record SchemaLease(
        string AdminConnectionString,
        string SchemaName,
        string ConnectionString);
}
