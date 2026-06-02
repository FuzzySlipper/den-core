using System.Reflection;

namespace DenCore.Service.Tests;

public class DenCoreOptionsTests
{
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
