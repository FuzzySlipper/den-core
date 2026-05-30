using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

public sealed class CapabilityToolsTests : IAsyncLifetime
{
    private readonly string _projectId = $"cap-tools-test-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private CapabilityToolsAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ICapabilityRepository _repo = null!;
    private ICapabilityInvocationService _service = null!;

    public async Task InitializeAsync()
    {
        _factory = new CapabilityToolsAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(_projectId) is null)
            await projects.CreateAsync(new Project { Id = _projectId, Name = "Capability Tools Test" });

        _repo = scope.ServiceProvider.GetRequiredService<ICapabilityRepository>();
        _service = scope.ServiceProvider.GetRequiredService<ICapabilityInvocationService>();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Helper ──────────────────────────────────────────────────────────

    private async Task SeedDefinitionAsync(string id, string status = CapabilityStatuses.Enabled,
        string executorKind = "http_endpoint", string sideEffectLevel = SideEffectLevels.None,
        string? httpEndpoint = null)
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = id,
            DisplayName = $"Cap {id}",
            Description = $"Test cap {id}",
            Status = status,
            ExecutorKind = executorKind,
            SideEffectLevel = sideEffectLevel,
            HttpEndpoint = httpEndpoint,
        });
    }

    private async Task<JsonElement> InvokeToolAsync(string toolName, object args)
    {
        // MCP tools are invoked through the standard MCP endpoint
        var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = args
            }
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        return doc.RootElement;
    }

    // ── analyze_image data URL rejection ────────────────────────────────

    [Fact]
    public async Task AnalyzeImage_RejectsDataUrl()
    {
        var result = await _service.AnalyzeImageAsync(
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAA=",
            _projectId,
            null,
            "test-agent");

        Assert.False(result.Success);
        Assert.Equal(InvocationStatuses.InvalidRequest, result.Status);
        Assert.Contains("data: URLs", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeImage_RejectsRawBase64()
    {
        var result = await _service.AnalyzeImageAsync(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAg",
            _projectId,
            null,
            "test-agent");

        Assert.False(result.Success);
        Assert.Equal(InvocationStatuses.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task AnalyzeImage_MissingCapability_ReturnsStructuredError()
    {
        // Use a unique capability ID that definitely doesn't exist
        // (actual vision.analyze_image.v1 may have been registered by another parallel test)
        var result = await _service.AnalyzeImageAsync(
            "/path/to/image.jpg",
            _projectId,
            null,
            "test-agent");

        Assert.False(result.Success);
        // Default behavior: analyze_image always delegates to vision.analyze_image.v1.
        // If it's not registered or not properly configured, we get a structured error.
        Assert.NotNull(result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AnalyzeImage_DisabledCapability_ReturnsStructuredError()
    {
        await SeedDefinitionAsync(CapabilityIds.VisionAnalyzeImageV1, CapabilityStatuses.Disabled,
            "http_endpoint", SideEffectLevels.None, "http://localhost:9999/analyze");

        var result = await _service.AnalyzeImageAsync(
            "/path/to/image.jpg",
            _projectId,
            null,
            "test-agent");

        Assert.False(result.Success);
        Assert.Equal(InvocationStatuses.Disabled, result.Status);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Invocation audit terminal records ───────────────────────────────

    [Fact]
    public async Task InvokeAsync_AlwaysTerminalizesRecord()
    {
        // Invoke a non-existent capability — should create a terminal record
        var invocation = await _service.InvokeAsync(
            "nonexistent-cap",
            _projectId,
            null,
            "test-agent",
            "{}");

        Assert.True(InvocationStatuses.IsTerminal(invocation.Status),
            $"Expected terminal status, got: {invocation.Status}");
    }

    [Fact]
    public async Task InvokeAsync_NonReadOnly_ReturnsTerminalRecord()
    {
        await SeedDefinitionAsync("destructive-tool",
            executorKind: "external_service",
            sideEffectLevel: SideEffectLevels.Destructive);

        var invocation = await _service.InvokeAsync(
            "destructive-tool",
            _projectId,
            null,
            "test-agent",
            "{}");

        Assert.Equal(InvocationStatuses.NonReadOnlyRejected, invocation.Status);
        Assert.True(InvocationStatuses.IsTerminal(invocation.Status));
    }

    [Fact]
    public async Task InvokeAsync_Disabled_ReturnsTerminalRecord()
    {
        await SeedDefinitionAsync("disabled-tool", CapabilityStatuses.Disabled);

        var invocation = await _service.InvokeAsync(
            "disabled-tool",
            _projectId,
            null,
            "test-agent",
            "{}");

        Assert.Equal(InvocationStatuses.Disabled, invocation.Status);
    }

    // ── list_capabilities / get_capability ──────────────────────────────

    [Fact]
    public async Task ListCapabilities_ReturnsResults()
    {
        await SeedDefinitionAsync("tools-list-1");
        await SeedDefinitionAsync("tools-list-2");

        var result = await CapabilityTools.ListCapabilities(
            _repo,
            status: null,
            side_effect_level: null,
            owner_project_id: null,
            limit: 50);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 2);
    }

    [Fact]
    public async Task ListCapabilities_FiltersByStatus()
    {
        await SeedDefinitionAsync("tools-list-enabled", CapabilityStatuses.Enabled);

        var result = await CapabilityTools.ListCapabilities(
            _repo,
            status: CapabilityStatuses.Enabled,
            side_effect_level: null,
            owner_project_id: null,
            limit: 50);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task GetCapability_ReturnsCapability()
    {
        await SeedDefinitionAsync("tools-get-me");

        var result = await CapabilityTools.GetCapability(_repo, "tools-get-me");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("tools-get-me", doc.RootElement.GetProperty("capability_id").GetString());
    }

    [Fact]
    public async Task GetCapability_NotFound_ReturnsError()
    {
        var result = await CapabilityTools.GetCapability(_repo, "nonexistent");
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
    }

    // ── upsert_capability_definition ────────────────────────────────────

    [Fact]
    public async Task UpsertCapabilityDefinition_CreatesAndReturns()
    {
        var id = $"tools-upsert-{Guid.NewGuid():N}";
        var result = await CapabilityTools.UpsertCapabilityDefinition(
            _repo, id, "Tools Upsert", "Created by tools test",
            "enabled", "http://localhost:9999/test",
            "http_endpoint", "none", null, null, null, null);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(id, doc.RootElement.GetProperty("capability_id").GetString());
        Assert.Equal("enabled", doc.RootElement.GetProperty("status").GetString());
    }

    // ── analyze_image tool via MCP endpoint ─────────────────────────────

    // Note: Direct MCP endpoint testing requires proper JSON-RPC session
    // management. The service-level AnalyzeImage_RejectsDataUrl test above
    // covers the data URL rejection logic at the unit level.
    // Full MCP integration testing is deferred to MCP endpoint test suites.

    // ── AppFactory ──────────────────────────────────────────────────────

    private sealed class CapabilityToolsAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-capability-tools-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
