using DenMcp.Core.Models;

namespace DenMcp.Server;

public sealed class DenMcpOptions
{
    public string DatabasePath { get; set; } = "";
    public string ListenUrl { get; set; } = "http://localhost:5199";
    public PiDockerLaunchProfileOptions PiSessionHost { get; set; } = new();

    public string GetResolvedDatabasePath()
    {
        if (!string.IsNullOrEmpty(DatabasePath))
            return DatabasePath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".den-mcp", "den.db");
    }
}
