using System.Reflection;
using DenMcp.Core.Mcp;
using DenMcp.Server.Tools;

namespace DenMcp.Server.Tests;

public class McpToolProfileAnnotationTests
{
    private readonly McpToolProfileRegistry _registry = McpToolProfileRegistry.CreateDefault();

    [Fact]
    public void AssemblyAnnotations_MatchRegistry()
    {
        var mismatches = _registry.ValidateAgainstAssembly(typeof(AgentTools).Assembly);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void EveryTool_HasBundleAttribute()
    {
        var methods = typeof(AgentTools).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttributesData()
                .Any(a => a.AttributeType.Name == "McpServerToolAttribute"))
            .ToList();

        var missing = new List<string>();
        foreach (var method in methods)
        {
            var bundleAttr = method.GetCustomAttribute<McpToolBundleAttribute>();
            var profileAttr = method.GetCustomAttribute<McpToolProfileAttribute>();
            if (bundleAttr is null || profileAttr is null)
            {
                var toolName = GetToolName(method);
                if (!string.IsNullOrEmpty(toolName))
                    missing.Add(toolName);
            }
        }

        Assert.True(missing.Count == 0,
            $"Tools missing [McpToolBundle] or [McpToolProfile]: {string.Join(", ", missing)}");
    }

    private static string? GetToolName(MethodInfo method)
    {
        var attr = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == "McpServerToolAttribute");
        if (attr is null) return null;
        var named = attr.NamedArguments.FirstOrDefault(na => na.MemberName == "Name");
        var ctor = attr.ConstructorArguments.FirstOrDefault();
        return named.TypedValue.Value as string ?? ctor.Value as string;
    }
}
