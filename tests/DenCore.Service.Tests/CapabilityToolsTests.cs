using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using DenCore.Service.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenCore.Service.Tests;

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

    private async Task SeedDefinitionAsync(string id, string status = CapabilityStatuses.Active,
        string implKind = ImplementationKinds.HttpEndpoint, string sideEffectLevel = SideEffectLevels.ReadOnly,
        string? serviceEndpoint = null)
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = id,
            DisplayName = $"Cap {id}",
            Description = $"Test cap {id}",
            Status = status,
            ImplementationKind = implKind,
            SideEffectLevel = sideEffectLevel,
            ServiceEndpoint = serviceEndpoint,
        });
    }

    private async Task<JsonElement> InvokeToolAsync(string toolName, object args)
    {
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
            null,
            "general",
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
            null,
            "general",
            _projectId,
            null,
            "test-agent");

        Assert.False(result.Success);
        Assert.Equal(InvocationStatuses.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task AnalyzeImage_MissingCapability_ReturnsStructuredError()
    {
        var result = await _service.AnalyzeImageAsync(
            "/path/to/image.jpg",
            null,
            "general",
            _projectId,
            null,
            "test-agent");

        Assert.False(result.Success);
        Assert.NotNull(result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AnalyzeImage_DisabledCapability_ReturnsStructuredError()
    {
        await SeedDefinitionAsync(CapabilityIds.VisionAnalyzeImageV1, CapabilityStatuses.Disabled,
            ImplementationKinds.HttpEndpoint, SideEffectLevels.ReadOnly, "http://localhost:9999/analyze");

        var result = await _service.AnalyzeImageAsync(
            "/path/to/image.jpg",
            null,
            "general",
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
        var invocation = await _service.InvokeAsync(
            "nonexistent-cap",
            "{}",
            _projectId,
            null,
            "test-agent");

        Assert.True(InvocationStatuses.IsTerminal(invocation.Status),
            $"Expected terminal status, got: {invocation.Status}");
    }

    [Fact]
    public async Task InvokeAsync_NonReadOnly_ReturnsTerminalRecord()
    {
        await SeedDefinitionAsync("destructive-tool",
            implKind: ImplementationKinds.HttpEndpoint,
            sideEffectLevel: SideEffectLevels.ExternalWrite);

        var invocation = await _service.InvokeAsync(
            "destructive-tool",
            "{}",
            _projectId,
            null,
            "test-agent");

        Assert.Equal(InvocationStatuses.Failed, invocation.Status);
        Assert.True(InvocationStatuses.IsTerminal(invocation.Status));
    }

    [Fact]
    public async Task InvokeAsync_Disabled_ReturnsTerminalRecord()
    {
        await SeedDefinitionAsync("disabled-tool", CapabilityStatuses.Disabled);

        var invocation = await _service.InvokeAsync(
            "disabled-tool",
            "{}",
            _projectId,
            null,
            "test-agent");

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
        await SeedDefinitionAsync("tools-list-active", CapabilityStatuses.Active);

        var result = await CapabilityTools.ListCapabilities(
            _repo,
            status: CapabilityStatuses.Active,
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
    public async Task GetCapability_ShowsNewFields()
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "tools-new-fields",
            DisplayName = "New Fields Test",
            ImplementationKind = ImplementationKinds.HttpEndpoint,
            SideEffectLevel = SideEffectLevels.BoundedWrite,
            Status = CapabilityStatuses.Experimental,
            HttpMethod = "PUT",
            ServiceEndpoint = "http://example.com/api",
            TimeoutMs = 60000,
            MaxRequestBytes = 5242880,
            DefaultModelJson = "{\"model\":\"gpt-4\"}",
        });

        var result = await CapabilityTools.GetCapability(_repo, "tools-new-fields");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("http_endpoint", doc.RootElement.GetProperty("implementation_kind").GetString());
        Assert.Equal("bounded_write", doc.RootElement.GetProperty("side_effect_level").GetString());
        Assert.Equal("experimental", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("PUT", doc.RootElement.GetProperty("http_method").GetString());
        Assert.Equal(60000, doc.RootElement.GetProperty("timeout_ms").GetInt32());
        Assert.Equal(5242880, doc.RootElement.GetProperty("max_request_bytes").GetInt32());
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
            _repo, id, "Tools Upsert", "Created by tools test", null,
            ImplementationKinds.HttpEndpoint, "http://localhost:9999/test", null,
            null, null, null, null,
            SideEffectLevels.ReadOnly, CapabilityStatuses.Experimental, null, null, null,
            30000, 10485760, null);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(id, doc.RootElement.GetProperty("capability_id").GetString());
        Assert.Equal("experimental", doc.RootElement.GetProperty("status").GetString());
    }

    // ── analyze_image tool via MCP endpoint ─────────────────────────────
    // Note: Direct MCP endpoint testing requires proper JSON-RPC session
    // management. The service-level AnalyzeImage_RejectsDataUrl test above
    // covers the data URL rejection logic at the unit level.

    // ── Invocation record shape ─────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ProducesInvocationId()
    {
        var invocation = await _service.InvokeAsync(
            "nonexistent-cap",
            "{}",
            _projectId,
            null,
            "test-agent");

        Assert.NotNull(invocation.InvocationId);
        Assert.StartsWith("capinv_", invocation.InvocationId);
    }

    [Fact]
    public async Task InvokeAsync_RejectsTooLargeRequest()
    {
        await SeedDefinitionAsync("tools-max-bytes",
            implKind: ImplementationKinds.HttpEndpoint,
            sideEffectLevel: SideEffectLevels.ReadOnly,
            serviceEndpoint: "http://localhost:9999/test");

        // The default max_request_bytes is 10485760, so a normal payload should work
        // This test checks that oversize is rejected; we need a definition with a very small limit
        var smallCapId = "tools-small-max";
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = smallCapId,
            DisplayName = "Small Max",
            Status = CapabilityStatuses.Active,
            ImplementationKind = ImplementationKinds.HttpEndpoint,
            SideEffectLevel = SideEffectLevels.ReadOnly,
            ServiceEndpoint = "http://localhost:9999/test",
            MaxRequestBytes = 100, // very small max
        });

        var invocation = await _service.InvokeAsync(
            smallCapId,
            new string('x', 200), // 200 bytes, exceeds 100
            _projectId,
            null,
            "test-agent");

        Assert.Equal(InvocationStatuses.InvalidRequest, invocation.Status);
        Assert.Equal("request_too_large", invocation.ErrorType);
    }

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
