using DenCore.Data;

namespace DenCore.Tests.Data;

public class DbConnectionFactoryTests
{
    [Fact]
    public void Constructor_DefaultsToPostgresProvider()
    {
        var factory = new DbConnectionFactory("Host=localhost;Database=den_core_test;Username=den");

        Assert.Equal(DatabaseProviderKind.Postgres, factory.Provider);
        Assert.Equal(DatabaseProviderKind.Postgres, factory.Sql.Provider);
    }

    [Fact]
    public void Constructor_PostgresRequiresConnectionString()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DbConnectionFactory("", DatabaseProviderKind.Postgres));

        Assert.Contains("DenCore:Provider=Postgres", ex.Message);
        Assert.Contains("DenCore:ConnectionString", ex.Message);
    }

    [Fact]
    public void Constructor_PostgresRejectsInvalidConnectionString()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DbConnectionFactory("not a connection string", DatabaseProviderKind.Postgres));

        Assert.Contains("invalid DenCore:ConnectionString", ex.Message);
    }

    [Fact]
    public void Constructor_PostgresStoresProviderAndDialect()
    {
        var factory = new DbConnectionFactory(
            "Host=localhost;Database=den_core_test;Username=den",
            DatabaseProviderKind.Postgres);

        Assert.Equal(DatabaseProviderKind.Postgres, factory.Provider);
        Assert.Equal(DatabaseProviderKind.Postgres, factory.Sql.Provider);
    }
}
