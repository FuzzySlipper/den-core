using System.ComponentModel;
using System.Reflection;
using DenCore.Mcp;
using DenCore.Service.Tools;

namespace DenCore.Service.Tests;

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

    [Fact]
    public void CurrentWorkerTools_DoNotUsePiWordingInDescriptions()
    {
        var currentWorkerTools = new[]
        {
            "prepare_coder_context_packet",
            "prepare_reviewer_context_packet",
            "prepare_validator_context_packet",
            "render_worker_prompt",
            "register_worker_run",
            "get_worker_run",
            "list_worker_runs",
            "get_worker_run_status",
            "cleanup_worker_run",
            "abort_worker_run",
            "rerun_worker_run",
            "post_worker_completion_packet"
        };

        var descriptionsWithPiWording = currentWorkerTools
            .Select(tool => (tool, description: GetToolDescription(tool)))
            .Where(item => item.description?.Contains("Pi", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => $"{item.tool}: {item.description}")
            .ToList();

        Assert.Empty(descriptionsWithPiWording);
    }

    [Fact]
    public void LegacyPiLaunchTools_AreAdminOnly()
    {
        var legacyPiTools = new[]
        {
            "legacy_launch_pi_worker",
            "legacy_launch_coder_worker",
            "legacy_launch_reviewer_worker",
            "legacy_launch_validator_worker",
            "legacy_launch_drift_checker_worker",
            "legacy_launch_packet_auditor_worker",
            "legacy_start_coder_worker_path",
            "legacy_start_reviewer_worker_path",
            "legacy_publish_worker_branch"
        };

        foreach (var tool in legacyPiTools)
        {
            Assert.True(_registry.ToolInProfile(tool, McpToolProfiles.LegacyFull), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.AdminCurrent), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.Planner), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.Runner), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerCoder), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerReviewer), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerValidator), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerDriftChecker), tool);
            Assert.False(_registry.ToolInProfile(tool, McpToolProfiles.WorkerPacketAuditor), tool);
        }
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

    private static string? GetToolDescription(string toolName)
    {
        var method = typeof(AgentTools).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .FirstOrDefault(m => string.Equals(GetToolName(m), toolName, StringComparison.Ordinal));
        return method?.GetCustomAttribute<DescriptionAttribute>()?.Description;
    }
}
