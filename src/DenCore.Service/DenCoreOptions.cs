using DenCore.Models;

namespace DenCore.Service;

public sealed class DenCoreOptions
{
    public string DatabasePath { get; set; } = "";
    public string ListenUrl { get; set; } = "http://localhost:5199";
    public PiDockerLaunchProfileOptions PiSessionHost { get; set; } = new();
    public GatewayContractOptions GatewayContract { get; set; } = new();
    public BlockedTaskEscalationPolicyOptions BlockedTaskEscalation { get; set; } = new();

    /// <summary>
    /// Resolves the database path using the following priority:
    /// 1. Explicit <see cref="DatabasePath"/> when non-empty.
    /// 2. Legacy <c>~/.den-mcp/den.db</c> when it already exists (preserves existing data).
    /// 3. New default <c>~/.den-core/den.db</c>.
    /// </summary>
    public string GetResolvedDatabasePath()
    {
        if (!string.IsNullOrEmpty(DatabasePath))
            return DatabasePath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacyPath = Path.Combine(home, ".den-mcp", "den.db");
        if (File.Exists(legacyPath))
            return legacyPath;

        return Path.Combine(home, ".den-core", "den.db");
    }
}

public sealed class GatewayContractOptions
{
    /// <summary>
    /// Optional shared token for Gateway-to-Core service calls. When empty,
    /// Gateway contract endpoints are open for local/stub deployments.
    /// </summary>
    public string? ServiceToken { get; set; }
}
