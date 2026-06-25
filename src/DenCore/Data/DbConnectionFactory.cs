using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace DenCore.Data;

public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string connectionString, DatabaseProviderKind provider = DatabaseProviderKind.Sqlite)
    {
        _connectionString = connectionString;
        Provider = provider;
        Sql = provider switch
        {
            DatabaseProviderKind.Sqlite => DbSqlDialect.Sqlite,
            DatabaseProviderKind.Postgres => DbSqlDialect.Postgres,
            _ => throw new NotSupportedException($"Unsupported database provider: {provider}")
        };
    }

    public DatabaseProviderKind Provider { get; }
    public DbSqlDialect Sql { get; }

    public async Task<DbConnection> CreateConnectionAsync()
    {
        if (Provider != DatabaseProviderKind.Sqlite)
            throw new NotSupportedException("Only the SQLite runtime provider is available before task #3322.");

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        await cmd.ExecuteNonQueryAsync();

        return connection;
    }
}
