namespace DenMcp.Core;

public static class BuildInfo
{
    public static string Version => GeneratedBuildInfo.Version;

    public static string InformationalVersion => GeneratedBuildInfo.InformationalVersion;

    public static string Commit => GeneratedBuildInfo.Commit;

    public static string DisplayVersion =>
        string.Equals(Commit, "unknown", StringComparison.Ordinal)
            ? Version
            : $"{Version} (commit {Commit})";
}
