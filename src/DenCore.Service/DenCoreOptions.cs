using DenCore.Data;
using DenCore.Models;

namespace DenCore.Service;

public sealed class DenCoreOptions
{
    public string Provider { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string ListenUrl { get; set; } = "http://localhost:5199";
    public GatewayContractOptions GatewayContract { get; set; } = new();
    public BlockedTaskEscalationPolicyOptions BlockedTaskEscalation { get; set; } = new();

    public DatabaseProviderKind GetDatabaseProvider()
    {
        var value = string.IsNullOrWhiteSpace(Provider)
            ? nameof(DatabaseProviderKind.Postgres)
            : Provider.Trim();

        if (Enum.TryParse<DatabaseProviderKind>(value, ignoreCase: true, out var provider) &&
            Enum.IsDefined(provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"Unsupported DenCore:Provider value '{Provider}'. Expected 'Postgres'.");
    }

    public string GetRequiredPostgresConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("DenCore:Provider=Postgres requires DenCore:ConnectionString to be set.");

        return ConnectionString;
    }

    // DatabasePath is retained only as a deserialization sink for archived
    // rollback env files. It is not used by the live Postgres runtime.
}

public sealed class GatewayContractOptions
{
    /// <summary>
    /// Optional shared token for Gateway-to-Core service calls. When empty,
    /// Gateway contract endpoints are open for local/stub deployments.
    /// </summary>
    public string? ServiceToken { get; set; }
}
