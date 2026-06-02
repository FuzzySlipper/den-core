namespace DenCore.Mcp;

/// <summary>
/// Annotates an MCP tool method with the profiles that include it.
/// This is metadata for documentation and validation; the authoritative runtime
/// mapping lives in <see cref="McpToolProfileRegistry"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpToolProfileAttribute : Attribute
{
    public IReadOnlyList<string> Profiles { get; }

    public McpToolProfileAttribute(params string[] profiles)
    {
        Profiles = profiles ?? Array.Empty<string>();
    }
}

/// <summary>
/// Annotates an MCP tool method with the bundles that include it.
/// This is metadata for documentation and validation; the authoritative runtime
/// mapping lives in <see cref="McpToolProfileRegistry"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpToolBundleAttribute : Attribute
{
    public IReadOnlyList<string> Bundles { get; }

    public McpToolBundleAttribute(params string[] bundles)
    {
        Bundles = bundles ?? Array.Empty<string>();
    }
}
