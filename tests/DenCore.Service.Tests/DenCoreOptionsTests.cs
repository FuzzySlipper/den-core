using System.Reflection;
using DenCore.Data;
using DenCore.Service;

namespace DenCore.Service.Tests;

public class DenCoreOptionsTests
{
    [Fact]
    public void GetDatabaseProvider_DefaultsToSqlite()
    {
        var options = new DenCoreOptions();

        Assert.Equal(DatabaseProviderKind.Sqlite, options.GetDatabaseProvider());
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
        Assert.Contains("Sqlite", ex.Message);
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

    [Fact]
    public void GetResolvedDatabasePath_ExplicitPath_ReturnsExplicit()
    {
        var options = new DenCoreOptions { DatabasePath = "/custom/path.db" };
        Assert.Equal("/custom/path.db", options.GetResolvedDatabasePath());
    }

    [Fact]
    public void GetResolvedDatabasePath_NoExplicitPath_ReturnsDenCoreDefault()
    {
        // The default path should end with den.db under a .den-core directory.
        // This test verifies the method produces a reasonable path when no explicit
        // path is set. On this machine a legacy ~/.den-mcp/den.db may or may not exist,
        // so we only verify the structure of the returned path.
        var options = new DenCoreOptions { DatabasePath = "" };
        var result = options.GetResolvedDatabasePath();
        Assert.False(string.IsNullOrEmpty(result));
        Assert.EndsWith("den.db", result);
    }

    [Fact]
    public void GetResolvedDatabasePath_NoExplicitPath_ResultIsUnderHome()
    {
        var options = new DenCoreOptions { DatabasePath = "" };
        var result = options.GetResolvedDatabasePath();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(result.StartsWith(home, StringComparison.Ordinal),
            $"Expected path under {home}, got {result}");
    }
}
