using System.Data.Common;
using Npgsql;

namespace DenCore.Data;

public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string connectionString, DatabaseProviderKind provider = DatabaseProviderKind.Postgres)
    {
        ValidateConnectionString(provider, connectionString);

        _connectionString = connectionString;
        Provider = provider;
        Sql = provider switch
        {
            DatabaseProviderKind.Postgres => DbSqlDialect.Postgres,
            _ => throw new NotSupportedException($"Unsupported database provider: {provider}")
        };
    }

    public DatabaseProviderKind Provider { get; }
    public DbSqlDialect Sql { get; }

    public async Task<DbConnection> CreateConnectionAsync()
    {
        DbConnection connection = Provider switch
        {
            DatabaseProviderKind.Postgres => new NpgsqlConnection(_connectionString),
            _ => throw new NotSupportedException($"Unsupported database provider: {Provider}")
        };
        await connection.OpenAsync();

        return connection;
    }

    public static void ValidateConnectionString(DatabaseProviderKind provider, string connectionString)
    {
        switch (provider)
        {
            case DatabaseProviderKind.Postgres:
                ValidatePostgresConnectionString(connectionString);
                return;
            default:
                throw new NotSupportedException($"Unsupported database provider: {provider}");
        }
    }

    private static void ValidatePostgresConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "DenCore:Provider=Postgres requires DenCore:ConnectionString to be set.",
                nameof(connectionString));
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                "DenCore:Provider=Postgres has an invalid DenCore:ConnectionString.",
                nameof(connectionString),
                ex);
        }
    }
}
