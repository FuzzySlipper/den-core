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
        // If the test fails, print detailed info
        if (mismatches.Count > 0)
        {
            foreach (var m in mismatches)
                Console.Error.WriteLine("VALIDATION_MISMATCH: " + m);
        }
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

    [Fact]
    public void DiscussionTools_AreScopedByRole()
    {
        var readOnlyDiscussionTools = new[]
        {
            "get_document_discussion",
            "list_discussion_threads",
            "get_discussion_thread"
        };
        var writeDiscussionTools = new[]
        {
            "comment_on_document",
            "create_discussion_comment"
        };

        foreach (var tool in readOnlyDiscussionTools)
        {
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.Planner), tool);
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.Runner), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerCoder), tool);
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.WorkerReviewer), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerValidator), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerDriftChecker), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerPacketAuditor), tool);
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.AdminCurrent), tool);
        }

        foreach (var tool in writeDiscussionTools)
        {
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.Planner), tool);
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.Runner), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerCoder), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerReviewer), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerValidator), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerDriftChecker), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerPacketAuditor), tool);
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.AdminCurrent), tool);
        }

        Assert.True(_registry.ToolInProfile("update_discussion_thread", McpToolProfiles.AdminCurrent));
        Assert.False(_registry.ToolInProfile("update_discussion_thread", McpToolProfiles.Planner));
        Assert.False(_registry.ToolInProfile("update_discussion_thread", McpToolProfiles.Runner));
        Assert.False(_registry.ToolInProfile("update_discussion_thread", McpToolProfiles.WorkerReviewer));
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
