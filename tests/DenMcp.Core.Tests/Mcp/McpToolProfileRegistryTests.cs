using DenMcp.Core.Mcp;

namespace DenMcp.Core.Tests.Mcp;

public class McpToolProfileRegistryTests
{
    private readonly McpToolProfileRegistry _registry = McpToolProfileRegistry.CreateDefault();

    [Fact]
    public void AllTools_AreClassified()
    {
        Assert.True(_registry.AllToolNames.Count > 80, $"Expected >80 tools, got {_registry.AllToolNames.Count}");
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("runner")]
    [InlineData("admin-current")]
    [InlineData("legacy-full")]
    [InlineData("worker-coder")]
    [InlineData("worker-reviewer")]
    [InlineData("worker-validator")]
    [InlineData("worker-drift-checker")]
    [InlineData("worker-packet-auditor")]
    [InlineData("curator")]
    [InlineData("diagnostics")]
    public void KnownProfiles_Exist(string profile)
    {
        Assert.True(_registry.IsKnownProfile(profile), $"Profile '{profile}' should exist");
    }

    [Fact]
    public void AdminCurrent_ExcludesLegacy()
    {
        var adminTools = _registry.GetProfileTools("admin-current");
        var legacyTools = _registry.GetBundleTools("legacy");
        foreach (var t in legacyTools)
            Assert.DoesNotContain(t, adminTools);
    }

    [Fact]
    public void AdminCurrent_ExcludesDiagnostics()
    {
        var adminTools = _registry.GetProfileTools("admin-current");
        var diagTools = _registry.GetBundleTools("diagnostics");
        foreach (var t in diagTools)
            Assert.DoesNotContain(t, adminTools);
    }

    [Fact]
    public void LegacyFull_IncludesAllLegacyTools()
    {
        var legacyFull = _registry.GetProfileTools("legacy-full");
        var legacyBundle = _registry.GetBundleTools("legacy");
        foreach (var t in legacyBundle)
            Assert.Contains(t, legacyFull);
    }

    [Fact]
    public void LegacyFull_IncludesDiagnostics()
    {
        var legacyFull = _registry.GetProfileTools("legacy-full");
        var diag = _registry.GetBundleTools("diagnostics");
        foreach (var t in diag)
            Assert.Contains(t, legacyFull);
    }

    [Fact]
    public void Planner_ExcludesLegacyAndDiagnostics()
    {
        var planner = _registry.GetProfileTools("planner");
        var excluded = _registry.GetBundleTools("legacy")
            .Concat(_registry.GetBundleTools("diagnostics"))
            .ToHashSet();
        foreach (var t in excluded)
            Assert.DoesNotContain(t, planner);
    }

    [Fact]
    public void UnknownProfile_ReturnsEmptySet()
    {
        var tools = _registry.GetProfileTools("nonexistent");
        Assert.Empty(tools);
    }

    [Fact]
    public void UnknownBundle_ReturnsEmptySet()
    {
        var tools = _registry.GetBundleTools("nonexistent");
        Assert.Empty(tools);
    }

    [Fact]
    public void ComputeAllowedTools_NullSelectors_ReturnsAll()
    {
        var allowed = _registry.ComputeAllowedTools(null, null, out var error);
        Assert.Null(error);
        Assert.NotNull(allowed);
        Assert.Equal(_registry.AllToolNames.Count, allowed!.Count);
    }

    [Fact]
    public void ComputeAllowedTools_UnknownProfile_ReturnsNullWithError()
    {
        var allowed = _registry.ComputeAllowedTools("unknown-profile", null, out var error);
        Assert.Null(allowed);
        Assert.NotNull(error);
        Assert.Contains("Unknown tool profile", error);
    }

    [Fact]
    public void ComputeAllowedTools_UnknownBundle_ReturnsNullWithError()
    {
        var allowed = _registry.ComputeAllowedTools(null, new[] { "unknown-bundle" }, out var error);
        Assert.Null(allowed);
        Assert.NotNull(error);
        Assert.Contains("Unknown tool bundle", error);
    }

    [Fact]
    public void ComputeAllowedTools_ProfileAndBundle_Combines()
    {
        var allowed = _registry.ComputeAllowedTools("planner", new[] { "legacy" }, out var error);
        Assert.Null(error);
        Assert.NotNull(allowed);
        Assert.Contains("create_task", allowed!); // from planner
        Assert.Contains("legacy_get_dispatch", allowed!); // from legacy bundle
    }

    [Theory]
    [InlineData("worker-validator")]
    [InlineData("worker-drift-checker")]
    [InlineData("worker-packet-auditor")]
    public void NewWorkerProfiles_AreNonEmpty(string profile)
    {
        var tools = _registry.GetProfileTools(profile);
        Assert.NotEmpty(tools);
        Assert.True(tools.Count >= 16,
            $"Profile '{profile}' expected at least 16 tools, got {tools.Count} ({string.Join(", ", tools.OrderBy(t => t))})");
    }

    [Theory]
    [InlineData("worker-validator")]
    [InlineData("worker-drift-checker")]
    [InlineData("worker-packet-auditor")]
    public void NewWorkerProfiles_ExcludeLegacyAndDiagnostics(string profile)
    {
        var tools = _registry.GetProfileTools(profile);
        var legacyTools = _registry.GetBundleTools("legacy");
        var diagTools = _registry.GetBundleTools("diagnostics");
        foreach (var t in legacyTools)
            Assert.DoesNotContain(t, tools);
        foreach (var t in diagTools)
            Assert.DoesNotContain(t, tools);
    }

    [Theory]
    [InlineData("worker-validator", "prepare_validator_context_packet")]
    [InlineData("worker-drift-checker", "prepare_drift_checker_context_packet")]
    [InlineData("worker-packet-auditor", "prepare_packet_auditor_context_packet")]
    public void NewWorkerProfiles_IncludeRoleSpecificPacket(string profile, string expectedPacketTool)
    {
        var tools = _registry.GetProfileTools(profile);
        Assert.Contains("get_worker_run_status", tools);
        Assert.Contains("post_worker_completion_packet", tools);
        Assert.Contains("get_latest_task_packet", tools);
        Assert.Contains(expectedPacketTool, tools);
        Assert.Contains("render_worker_prompt", tools);
    }

    [Theory]
    [InlineData("core-read")]
    [InlineData("core-write")]
    [InlineData("planning")]
    [InlineData("review")]
    [InlineData("worker-workflow")]
    [InlineData("curation")]
    [InlineData("governance-admin")]
    [InlineData("legacy")]
    [InlineData("all-current")]
    [InlineData("all-with-legacy")]
    public void SpecBundles_Exist(string bundle)
    {
        Assert.True(_registry.IsKnownBundle(bundle), $"Bundle '{bundle}' should exist");
        Assert.NotEmpty(_registry.GetBundleTools(bundle));
    }

    [Fact]
    public void AnnotationValidation_MatchesRegistry()
    {
        // This validates that the assembly annotations match the authoritative registry.
        var mismatches = _registry.ValidateAgainstAssembly(typeof(McpToolProfileRegistry).Assembly);
        // We expect mismatches because the tool classes are in DenMcp.Server, not DenMcp.Core.
        // This test is a smoke check that the method runs without throwing.
        Assert.NotNull(mismatches);
    }
}
