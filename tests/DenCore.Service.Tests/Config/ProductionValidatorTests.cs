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
            DatabasePath = "/data/services/den-core/data/den.db",
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
            DatabasePath = "/data/services/den-core/data/den.db",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Contains(warnings, w => w.Contains("5199"));
    }

    [Fact]
    public void Validate_FallbackDbPath_Detected()
    {
        // Default fallback path ~/.den-core/den.db should be flagged
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://127.0.0.1:5299",
            DatabasePath = "",  // triggers GetResolvedDatabasePath fallback
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Contains(warnings, w => w.Contains("DatabasePath"));
    }

    [Fact]
    public void Validate_BothDangerous_ReturnsTwoWarnings()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://localhost:5199",
            DatabasePath = "",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Validate_ValidAlternativePort_Passes()
    {
        // Port 5299 (internal) should pass
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://127.0.0.1:5299",
            DatabasePath = "/data/services/den-core/data/den.db",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.DoesNotContain(warnings, w => w.Contains("5199"));
    }

    [Fact]
    public void Validate_CustomDbPathInProduction_Passes()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = "http://127.0.0.1:5299",
            DatabasePath = "/data/services/den-core/data/den.db",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.DoesNotContain(warnings, w => w.Contains("DatabasePath"));
    }

    [Fact]
    public void Validate_NullListenUrl_NoCrash()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = null!,  // shouldn't happen but guard against crash
            DatabasePath = "/data/services/den-core/data/den.db",
        };
        var warnings = ProductionValidator.Validate(opts);
        Assert.DoesNotContain(warnings, w => w.Contains("5199"));
    }

    [Fact]
    public void Validate_InvalidUriFormat_NoCrash()
    {
        var opts = new DenCoreOptions
        {
            ListenUrl = "not-a-valid-uri",
            DatabasePath = "/data/services/den-core/data/den.db",
        };
        // Should not throw
        var warnings = ProductionValidator.Validate(opts);
        Assert.DoesNotContain(warnings, w => w.Contains("5199"));
    }
}
