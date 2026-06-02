using DenCore.Mcp;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace DenCore.Service;

/// <summary>
/// Helpers for applying Core MCP tool profile/bundle filtering to an ASP.NET Core-hosted MCP session.
/// </summary>
public static class McpToolProfileServerExtensions
{
    /// <summary>
    /// Parse profile and bundle selectors from the current HTTP request.
    /// Supports query parameters and headers with the following precedence:
    /// query overrides header for each selector independently.
    /// </summary>
    public static (string? Profile, string[]? Bundles) ParseSelectors(this HttpContext httpContext)
    {
        var profile = ParseFirst(httpContext.Request.Query, "tool_profile")
            ?? ParseFirst(httpContext.Request.Headers, "X-Den-MCP-Tool-Profile");

        var bundlesRaw = ParseFirst(httpContext.Request.Query, "tool_bundles")
            ?? ParseFirst(httpContext.Request.Headers, "X-Den-MCP-Tool-Bundles");

        string[]? bundles = null;
        if (!string.IsNullOrWhiteSpace(bundlesRaw))
        {
            bundles = bundlesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(b => b.Trim().ToLowerInvariant())
                .Where(b => b.Length > 0)
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(profile))
            profile = profile.Trim().ToLowerInvariant();

        return (profile, bundles);
    }

    private static string? ParseFirst(IQueryCollection query, string key)
    {
        if (query.TryGetValue(key, out var values) && values.Count > 0)
            return values[0];
        return null;
    }

    private static string? ParseFirst(IHeaderDictionary headers, string key)
    {
        if (headers.TryGetValue(key, out var values) && values.Count > 0)
            return values[0];
        return null;
    }

    /// <summary>
    /// Apply profile/bundle filtering to the per-session <see cref="McpServerOptions.ToolCollection"/>.
    /// When no selectors are present, the collection is left unchanged (full-compatible behavior).
    /// </summary>
    public static void ApplyToolFiltering(
        this McpServerOptions options,
        McpToolProfileRegistry registry,
        HttpContext httpContext)
    {
        var (profile, bundles) = httpContext.ParseSelectors();

        // No selectors -> full-compatible behavior
        if (string.IsNullOrWhiteSpace(profile) && (bundles is null || bundles.Length == 0))
            return;

        var allowed = registry.ComputeAllowedTools(profile, bundles, out var error);
        if (allowed is null || error is not null)
        {
            // Fail closed: if selectors are present but invalid, clear everything.
            // The caller should have already validated, but as defense-in-depth we clear.
            options.ToolCollection?.Clear();
            return;
        }

        // We need to rebuild ToolCollection from the globally-registered tools.
        // The options instance currently contains the full set (populated by McpServerOptionsSetup).
        var allTools = options.ToolCollection?.ToArray() ?? Array.Empty<McpServerTool>();
        options.ToolCollection?.Clear();

        foreach (var tool in allTools)
        {
            var name = tool.ProtocolTool?.Name;
            if (name is not null && allowed.Contains(name))
            {
                options.ToolCollection?.Add(tool);
            }
        }
    }
}
