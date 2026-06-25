using DenCore.Llm;
using DenCore.Service;
using Microsoft.Extensions.Configuration;

namespace DenCore.Tests;

/// <summary>
/// Tests for ConfigMerger — verifies that DenCore + legacy DenMcp sections
/// are merged with correct precedence: DenCore__* env > DenMcp__* env > appsettings defaults.
/// </summary>
public sealed class ConfigMergerTests
{
    // =========================================================================
    // Default appsettings-only (no env overrides)
    // =========================================================================

    [Fact]
    public void BuildOptions_WithDefaultsOnly_UsesAppsettings()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:Provider"] = "Sqlite",
            ["DenCore:ListenUrl"] = "http://localhost:5199",
            ["DenCore:DatabasePath"] = "",
            ["DenCore:ConnectionString"] = "",
        });
        var opts = ConfigMerger.BuildOptions(config);

        Assert.Equal("Sqlite", opts.Provider);
        Assert.Equal("http://localhost:5199", opts.ListenUrl);
        Assert.Equal("", opts.DatabasePath);
        Assert.Equal("", opts.ConnectionString);
    }

    [Fact]
    public void BuildOptions_WithDefaultsOnly_NoLegacy_Ignored()
    {
        // No DenMcp section at all — should work fine
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:ListenUrl"] = "http://localhost:5199",
            ["DenCore:DatabasePath"] = "/custom/path/den.db",
        });
        var opts = ConfigMerger.BuildOptions(config);
        Assert.Equal("http://localhost:5199", opts.ListenUrl);
        Assert.Equal("/custom/path/den.db", opts.DatabasePath);
    }

    // =========================================================================
    // Legacy override (DenMcp__* env only, no DenCore__* env)
    // =========================================================================

    [Fact]
    public void BuildOptions_LegacyOverride_WinsOverAppsettings()
    {
        // Production scenario: only DenMcp__* env vars, appsettings provides DenCore defaults
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:ListenUrl"] = "http://localhost:5199",
            ["DenCore:DatabasePath"] = "",
            ["DenMcp:ListenUrl"] = "http://127.0.0.1:5299",
            ["DenMcp:DatabasePath"] = "/data/services/den-core/data/den.db",
        });
        var opts = ConfigMerger.BuildOptions(config);

        // Legacy values should win because core values are at appsettings defaults
        Assert.Equal("http://127.0.0.1:5299", opts.ListenUrl);
        Assert.Equal("/data/services/den-core/data/den.db", opts.DatabasePath);
    }

    [Fact]
    public void BuildOptions_LegacyOverride_PartialOverlay()
    {
        // Only legacy ListenUrl is set; DatabasePath should stay at appsettings default
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:ListenUrl"] = "http://localhost:5199",
            ["DenCore:DatabasePath"] = "",
            ["DenMcp:ListenUrl"] = "http://127.0.0.1:5299",
            // No DenMcp:DatabasePath
        });
        var opts = ConfigMerger.BuildOptions(config);

        Assert.Equal("http://127.0.0.1:5299", opts.ListenUrl);
        Assert.Equal("", opts.DatabasePath);
    }

    // =========================================================================
    // Explicit DenCore__* env wins over legacy
    // =========================================================================

    [Fact]
    public void BuildOptions_ExplicitDenCore_WinsOverLegacy()
    {
        // Both DenCore__* and DenMcp__* env vars set — DenCore should win
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:ListenUrl"] = "http://127.0.0.1:5199",  // explicit DenCore
            ["DenCore:DatabasePath"] = "/data/custom/den.db",
            ["DenMcp:ListenUrl"] = "http://127.0.0.1:5299",
            ["DenMcp:DatabasePath"] = "/data/legacy/den.db",
        });
        var opts = ConfigMerger.BuildOptions(config);

        // DenCore values are not at appsettings default → should win
        Assert.Equal("http://127.0.0.1:5199", opts.ListenUrl);
        Assert.Equal("/data/custom/den.db", opts.DatabasePath);
    }

    [Fact]
    public void BuildOptions_ExplicitDenCoreListenUrl_WinsOverLegacy_Partial()
    {
        // DenCore ListenUrl explicitly set, DenMcp DatabasePath set but core also set
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:ListenUrl"] = "http://localhost:5299",
            ["DenCore:DatabasePath"] = "",
            ["DenMcp:ListenUrl"] = "http://127.0.0.1:5199",
            ["DenMcp:DatabasePath"] = "/data/legacy/den.db",
        });
        var opts = ConfigMerger.BuildOptions(config);

        // ListenUrl: DenCore explicitly provided (not default) → keep DenCore
        Assert.Equal("http://localhost:5299", opts.ListenUrl);
        // DatabasePath: core is empty (appsettings default) → legacy wins
        Assert.Equal("/data/legacy/den.db", opts.DatabasePath);
    }

    [Fact]
    public void BuildOptions_ExplicitPostgresConfig_BindsProviderAndConnectionString()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:Provider"] = "Postgres",
            ["DenCore:ConnectionString"] = "Host=localhost;Database=den_core;Username=den",
            ["DenCore:ListenUrl"] = "http://localhost:5199",
            ["DenCore:DatabasePath"] = "/ignored/when/postgres.db",
        });

        var opts = ConfigMerger.BuildOptions(config);

        Assert.Equal("Postgres", opts.Provider);
        Assert.Equal("Host=localhost;Database=den_core;Username=den", opts.ConnectionString);
        Assert.Equal("/ignored/when/postgres.db", opts.DatabasePath);
    }

    // =========================================================================
    // LlmConfig merging
    // =========================================================================

    [Fact]
    public void BuildLlmConfig_Defaults_NoLegacy()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:Llm:Endpoint"] = "",
            ["DenCore:Llm:Model"] = "",
        });
        var llm = ConfigMerger.BuildLlmConfig(config);

        Assert.Equal("", llm.Endpoint);
        Assert.Equal("", llm.Model);
    }

    [Fact]
    public void BuildLlmConfig_LegacyOverridesDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:Llm:Endpoint"] = "",
            ["DenCore:Llm:ApiKey"] = "",
            ["DenCore:Llm:Model"] = "",
            ["DenMcp:Llm:Endpoint"] = "https://api.deepseek.com",
            ["DenMcp:Llm:ApiKey"] = "sk-legacy-key",
            ["DenMcp:Llm:Model"] = "deepseek-v4-flash",
        });
        var llm = ConfigMerger.BuildLlmConfig(config);

        Assert.Equal("https://api.deepseek.com", llm.Endpoint);
        Assert.Equal("sk-legacy-key", llm.ApiKey);
        Assert.Equal("deepseek-v4-flash", llm.Model);
    }

    [Fact]
    public void BuildLlmConfig_ExplicitDenCore_WinsOverLegacy()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:Llm:Endpoint"] = "https://core.example.com",
            ["DenCore:Llm:ApiKey"] = "",
            ["DenCore:Llm:Model"] = "gpt-5",
            ["DenMcp:Llm:Endpoint"] = "https://legacy.example.com",
            ["DenMcp:Llm:ApiKey"] = "sk-legacy-key",
            ["DenMcp:Llm:Model"] = "deepseek-old",
        });
        var llm = ConfigMerger.BuildLlmConfig(config);

        // Endpoint and Model explicitly set in DenCore → DenCore wins
        Assert.Equal("https://core.example.com", llm.Endpoint);
        // ApiKey: DenCore is empty (default) → legacy wins
        Assert.Equal("sk-legacy-key", llm.ApiKey);
        Assert.Equal("gpt-5", llm.Model);
    }

    // =========================================================================
    // No legacy section at all
    // =========================================================================

    [Fact]
    public void BuildOptions_NoLegacySection_NoOp()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:ListenUrl"] = "http://localhost:5199",
            ["DenCore:DatabasePath"] = "",
        });
        var opts = ConfigMerger.BuildOptions(config);
        Assert.Equal("http://localhost:5199", opts.ListenUrl);
        Assert.Equal("", opts.DatabasePath);
    }

    [Fact]
    public void BuildLlmConfig_NoLegacySection_NoOp()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DenCore:Llm:Endpoint"] = "https://example.com",
            ["DenCore:Llm:Model"] = "my-model",
        });
        var llm = ConfigMerger.BuildLlmConfig(config);
        Assert.Equal("https://example.com", llm.Endpoint);
        Assert.Equal("my-model", llm.Model);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static IConfigurationRoot BuildConfig(Dictionary<string, string?> data)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(data!)
            .Build();
    }
}
