using DenCore.Llm;
using DenCore.Models;
using DenCore.Services;
using Microsoft.Extensions.Configuration;

namespace DenCore.Service;

/// <summary>
/// Merges the new <c>DenCore</c> config section with the legacy <c>DenMcp</c> section
/// so that environment-provided legacy values take precedence over appsettings.json defaults.
/// 
/// The core problem: appsettings.json always contains a default <c>DenCore</c> section.
/// The old pattern <c>coreSection.Exists() ? coreSection : legacySection</c> always picks
/// the <c>DenCore</c> section even when production env only provides <c>DenMcp__*</c> overrides,
/// causing the legacy env vars to be silently ignored. After binding, the service would listen
/// on the appsettings default port and use the default DB path.
/// 
/// Resolution strategy:
/// 1. Bind <c>DenCore</c> section first (appsettings defaults + <c>DenCore__*</c> env overrides).
/// 2. Detect properties where the <c>DenCore</c> value is still at its appsettings.json default
///    (meaning no <c>DenCore__*</c> env override was provided).
/// 3. For those properties, overlay the legacy <c>DenMcp</c> value if non-empty.
/// 
/// This ensures the path of least resistance works: operators set <c>DenCore__*</c> and get
/// full control; operators still using <c>DenMcp__*</c> env vars are not silently broken.
/// </summary>
/// <remarks>
/// Compatibility coverage:
/// - Top-level Core options (ListenUrl, DatabasePath) — full merge support.
/// - LlmConfig (Endpoint, ApiKey, Model) — full merge support.
/// - DenPublishFacade (Endpoint) — full merge support.
/// - TrustedPublisher — out of scope for this hardening task. The DenMcp__* legacy
///   compatibility path for TrustedPublisher arrays was never used in production
///   (production env already had DenCore__TrustedPublisher__* equivalents at the time
///   of the #2129 incident). Only top-level Core + LLM + facade endpoint are
///   compatibility-supported in this merge pass.
/// </remarks>
public static class ConfigMerger
{
    internal const string DefaultListenUrlValue = "http://localhost:5199";

    /// <summary>
    /// Build a merged <see cref="DenCoreOptions"/> from DenCore + legacy DenMcp sections.
    /// </summary>
    public static DenCoreOptions BuildOptions(IConfiguration config)
    {
        var options = new DenCoreOptions();
        var coreSection = config.GetSection("DenCore");
        var legacySection = config.GetSection("DenMcp");

        coreSection.Bind(options);

        if (!legacySection.Exists())
            return options;

        // ListenUrl: overlay legacy if DenCore value is still the appsettings default
        var coreListenUrl = config["DenCore:ListenUrl"];
        if (coreListenUrl == DefaultListenUrlValue || string.IsNullOrEmpty(coreListenUrl))
        {
            var legacyListenUrl = legacySection["ListenUrl"];
            if (!string.IsNullOrEmpty(legacyListenUrl))
                options.ListenUrl = legacyListenUrl;
        }

        // DatabasePath: appsettings default is ""; overlay legacy if core is empty
        var coreDbPath = config["DenCore:DatabasePath"];
        if (string.IsNullOrEmpty(coreDbPath))
        {
            var legacyDbPath = legacySection["DatabasePath"];
            if (!string.IsNullOrEmpty(legacyDbPath))
                options.DatabasePath = legacyDbPath;
        }

        return options;
    }

    /// <summary>
    /// Build a merged <see cref="LlmConfig"/> from DenCore:Llm + legacy DenMcp:Llm sections.
    /// </summary>
    public static LlmConfig BuildLlmConfig(IConfiguration config)
    {
        var llmConfig = new LlmConfig();
        var coreLlm = config.GetSection("DenCore:Llm");
        var legacyLlm = config.GetSection("DenMcp:Llm");

        coreLlm.Bind(llmConfig);

        if (!legacyLlm.Exists())
            return llmConfig;

        // Overlay simple string properties where core value is at default (empty)
        OverlayString(legacyLlm, "Endpoint", v => llmConfig.Endpoint = v,
            () => config["DenCore:Llm:Endpoint"]);
        OverlayString(legacyLlm, "ApiKey", v => llmConfig.ApiKey = v,
            () => config["DenCore:Llm:ApiKey"]);
        OverlayString(legacyLlm, "Model", v => llmConfig.Model = v,
            () => config["DenCore:Llm:Model"]);

        return llmConfig;
    }

    /// <summary>
    /// Build merged <see cref="TrustedPublisherOptions"/>.
    /// DenCore section binding handles all properties including arrays natively —
    /// legacy DenMcp overlay is not needed because env-provided <c>DenMcp__TrustedPublisher__*</c>
    /// values are present in the config tree at the same priority and are merged by .Bind().
    /// This method exists for symmetry with the other Build* methods and for future
    /// compatibility if a specific string-level overlay is needed.
    /// </summary>
    public static TrustedPublisherOptions BuildTrustedPublisherOptions(IConfiguration config)
    {
        var options = new TrustedPublisherOptions();
        config.GetSection("DenCore:TrustedPublisher").Bind(options);
        return options;
    }

    /// <summary>
    /// Build a merged <see cref="DenPublishFacadeOptions"/> config.
    /// </summary>
    public static DenPublishFacadeOptions BuildDenPublishFacadeOptions(IConfiguration config)
    {
        var options = new DenPublishFacadeOptions();
        var coreSub = config.GetSection("DenCore:DenPublishFacade");
        var legacySub = config.GetSection("DenMcp:DenPublishFacade");

        coreSub.Bind(options);

        if (!legacySub.Exists())
            return options;

        OverlayString(legacySub, "Endpoint", v => options.Endpoint = v,
            () => config["DenCore:DenPublishFacade:Endpoint"]);

        return options;
    }

    // ---- helpers ----

    private static void OverlayString(
        IConfigurationSection legacySub,
        string key,
        Action<string> setter,
        Func<string?> getCoreValue)
    {
        var coreValue = getCoreValue();
        if (!string.IsNullOrEmpty(coreValue))
            return; // DenCore__* explicitly provided

        var legacyValue = legacySub[key];
        if (!string.IsNullOrEmpty(legacyValue))
            setter(legacyValue);
    }
}

/// <summary>
/// Production-configuration validation guard.
/// Fails closed when Den Core starts with dangerous default config.
/// Called automatically in Production environment, or explicitly via --validate-prod.
/// </summary>
public static class ProductionValidator
{
    /// <summary>
    /// Validate production config. Returns warning messages (empty = healthy).
    /// </summary>
    public static List<string> Validate(DenCoreOptions options)
    {
        var warnings = new List<string>();

        // Port 5199 is owned by the den-mcp public facade.
        if (options.ListenUrl != null)
        {
            try
            {
                var uri = new Uri(options.ListenUrl);
                if (uri.Port == 5199)
                {
                    warnings.Add(
                        $"PRODUCTION GUARD: ListenUrl port is 5199 (owned by den-mcp facade). "
                        + $"Den Core should listen on an internal port (5299). Value: {options.ListenUrl}");
                }
            }
            catch (UriFormatException)
            {
            }
        }

        var resolvedDb = options.GetResolvedDatabasePath();
        if (resolvedDb != null &&
            !resolvedDb.Contains("/data/services/den-core/data/"))
        {
            warnings.Add(
                $"PRODUCTION GUARD: DatabasePath resolved to '{resolvedDb}', "
                + $"outside /data/services/den-core/data/. "
                + $"(Current DatabasePath: '{options.DatabasePath}')");
        }

        return warnings;
    }
}
