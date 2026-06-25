using DenCore.Service;

namespace DenCore.Tests;

/// <summary>
/// Tests for ProductionValidator — confirms that dangerous default config is
/// detected and valid production config passes.
/// </summary>
public sealed class ProductionValidatorTests
{
    [Fact]
    public void Validate_StandardProductionConfig_Passes()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://127.0.0.1:5299",
            Provider = "Postgres",
            ConnectionString = "Host=localhost;Database=den_core;Username=den",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_Port5199_Detected()
    {
        // Port 5199 is owned by den-mcp facade
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://localhost:5199",
            Provider = "Postgres",
            ConnectionString = "Host=localhost;Database=den_core;Username=den",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Contains(warnings, w => w.Contains("5199"));
    }

    [Fact]
    public void Validate_DefaultSqliteProvider_FailsClosed()
    {
        // Empty Provider still resolves to the legacy SQLite default for local
        // tests, but production must fail closed after the Postgres cutover.
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://127.0.0.1:5299",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Contains(warnings, w => w.Contains("production is Postgres-only"));
    }

    [Fact]
    public void Validate_BothDangerous_ReturnsTwoWarnings()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://localhost:5199",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("5199"));
        Assert.Contains(warnings, w => w.Contains("production is Postgres-only"));
    }

    [Fact]
    public void Validate_ValidAlternativePort_Passes()
    {
        // Port 5299 (internal) should pass
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://127.0.0.1:5299",
            Provider = "Postgres",
            ConnectionString = "Host=localhost;Database=den_core;Username=den",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_SqliteProviderWithProductionDbPath_FailsClosed()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://127.0.0.1:5299",
            Provider = "Sqlite",
            DatabasePath = "/data/services/den-core/data/den.db",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Contains(warnings, w => w.Contains("production is Postgres-only"));
    }

    [Fact]
    public void Validate_NullListenUrl_NoCrash()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = null!,  // shouldn't happen but guard against crash
            Provider = "Postgres",
            ConnectionString = "Host=localhost;Database=den_core;Username=den",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.DoesNotContain(warnings, w => w.Contains("5199"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_InvalidUriFormat_NoCrash()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = "not-a-valid-uri",
            Provider = "Postgres",
            ConnectionString = "Host=localhost;Database=den_core;Username=den",
        };
        // Should not throw
        var warnings = ProductionValidator.Validate(opts);
        Assert.DoesNotContain(warnings, w => w.Contains("5199"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_PostgresWithoutConnectionString_FailsClosed()
    {
        var opts = new DenCoreOptions
        {
            Provider = "Postgres",
            ListenUrl = "http://127.0.0.1:5299",
            ConnectionString = ""
        };

        var warnings = ProductionValidator.Validate(opts);

        Assert.Contains(warnings, w => w.Contains("DenCore:Provider=Postgres"));
        Assert.DoesNotContain(warnings, w => w.Contains("DatabasePath"));
    }

    [Fact]
    public void Validate_PostgresWithConnectionString_DoesNotRequireSqlitePath()
    {
        var opts = new DenCoreOptions
        {
            Provider = "Postgres",
            ListenUrl = "http://127.0.0.1:5299",
            ConnectionString = "Host=localhost;Database=den_core;Username=den"
        };

        var warnings = ProductionValidator.Validate(opts);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_PostgresWithInvalidConnectionString_FailsClosed()
    {
        var opts = new DenCoreOptions
        {
            Provider = "Postgres",
            ListenUrl = "http://127.0.0.1:5299",
            ConnectionString = "not a connection string"
        };

        var warnings = ProductionValidator.Validate(opts);

        Assert.Contains(warnings, w => w.Contains("invalid DenCore:ConnectionString"));
    }

    [Fact]
    public void Validate_InvalidProvider_FailsClosed()
    {
        var opts = new DenCoreOptions
        {
            Provider = "mysql",
            ListenUrl = "http://127.0.0.1:5299",
        };

        var warnings = ProductionValidator.Validate(opts);

        Assert.Contains(warnings, w => w.Contains("Unsupported DenCore:Provider"));
    }
}
