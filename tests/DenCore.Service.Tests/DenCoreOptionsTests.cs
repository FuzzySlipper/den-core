using System.Reflection;
using DenCore.Data;
using DenCore.Service;

namespace DenCore.Service.Tests;

public class DenCoreOptionsTests
{
    [Fact]
    public void GetDatabaseProvider_DefaultsToPostgres()
    {
        var options = new DenCoreOptions();

        Assert.Equal(DatabaseProviderKind.Postgres, options.GetDatabaseProvider());
    }

    [Fact]
    public void GetDatabaseProvider_ParsesPostgresCaseInsensitively()
    {
        var options = new DenCoreOptions { Provider = "postgres" };

        Assert.Equal(DatabaseProviderKind.Postgres, options.GetDatabaseProvider());
    }

    [Fact]
    public void GetDatabaseProvider_InvalidProvider_ThrowsClearError()
    {
        var options = new DenCoreOptions { Provider = "mysql" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.GetDatabaseProvider());
        Assert.Contains("DenCore:Provider", ex.Message);
        Assert.Contains("Postgres", ex.Message);
    }

    [Fact]
    public void GetRequiredPostgresConnectionString_Missing_ThrowsClearError()
    {
        var options = new DenCoreOptions { Provider = "Postgres", ConnectionString = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.GetRequiredPostgresConnectionString());
        Assert.Contains("DenCore:ConnectionString", ex.Message);
    }

    [Fact]
    public void GetRequiredPostgresConnectionString_Configured_ReturnsValue()
    {
        var options = new DenCoreOptions
        {
            Provider = "Postgres",
            ConnectionString = "Host=localhost;Database=den_core_test;Username=den"
        };

        Assert.Equal(options.ConnectionString, options.GetRequiredPostgresConnectionString());
    }

}
