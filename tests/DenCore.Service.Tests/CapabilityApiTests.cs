using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Models;
using DenCore.Service.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenCore.Service.Tests;

public sealed class CapabilityApiTests : IAsyncLifetime
{
    private readonly string _projectId = $"cap-api-test-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private CapabilityAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new CapabilityAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        if (await projects.GetByIdAsync(_projectId) is null)
            await projects.CreateAsync(new Project { Id = _projectId, Name = "Capability API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task SeedDefinitionAsync(string id, string status = CapabilityStatuses.Active,
        string implKind = ImplementationKinds.RegistryOnly, string sideEffectLevel = SideEffectLevels.ReadOnly,
        string? serviceEndpoint = null, string? ownerProjectId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICapabilityRepository>();
        await repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = id,
            DisplayName = $"Cap {id}",
            Description = $"Test cap {id}",
            Status = status,
            ImplementationKind = implKind,
            SideEffectLevel = sideEffectLevel,
            ServiceEndpoint = serviceEndpoint,
            OwnerProjectId = ownerProjectId,
        });
    }

    // ── Definition CRUD ─────────────────────────────────────────────────

    [Fact]
    public async Task GetCapabilities_ReturnsList()
    {
        await SeedDefinitionAsync("api-list-1");
        await SeedDefinitionAsync("api-list-2");

        var response = await _client.GetAsync("/api/capabilities");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.True(caps.Count >= 2);
    }

    [Fact]
    public async Task GetCapabilities_FiltersByStatus()
    {
        await SeedDefinitionAsync("api-status-active", CapabilityStatuses.Active);
        await SeedDefinitionAsync("api-status-disabled", CapabilityStatuses.Disabled);

        var response = await _client.GetAsync("/api/capabilities?status=disabled");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.All(caps, c => Assert.Equal("disabled", c.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task GetCapabilities_FiltersBySideEffectLevel()
    {
        await SeedDefinitionAsync("api-se-1", sideEffectLevel: SideEffectLevels.ReadOnly);
        await SeedDefinitionAsync("api-se-2", sideEffectLevel: SideEffectLevels.ExternalWrite);

        var response = await _client.GetAsync($"/api/capabilities?sideEffectLevel={SideEffectLevels.ExternalWrite}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.All(caps, c => Assert.Equal("external_write", c.GetProperty("side_effect_level").GetString()));
    }

    [Fact]
    public async Task GetCapabilities_FiltersByOwnerProject()
    {
        await SeedDefinitionAsync("api-owner-1", ownerProjectId: _projectId);

        var response = await $"/api/capabilities?ownerProjectId={_projectId}".MockGet(_client);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.NotEmpty(caps);
    }

    [Fact]
    public async Task GetCapability_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/capabilities/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCapability_ReturnsDefinition()
    {
        await SeedDefinitionAsync("api-get-me");

        var response = await _client.GetAsync("/api/capabilities/api-get-me");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("api-get-me", doc.RootElement.GetProperty("capability_id").GetString());
    }

    [Fact]
    public async Task PostCapability_CreatesDefinition()
    {
        var id = $"api-post-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync("/api/capabilities", new
        {
            capability_id = id,
            display_name = "Post Created",
            description = "Created via POST",
            status = "active",
            implementation_kind = "http_endpoint",
            side_effect_level = "read_only",
            service_endpoint = "http://localhost:9999/test",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("capability_id").GetString());
    }

    [Fact]
    public async Task PostCapability_MissingId_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/capabilities", new
        {
            display_name = "No ID",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutCapability_UpsertsByPath()
    {
        var id = $"api-put-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/capabilities/{id}", new
        {
            display_name = "Put Created",
            status = "active",
            implementation_kind = "http_endpoint",
            side_effect_level = "read_only",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("capability_id").GetString());
    }

    // ── Invocation ──────────────────────────────────────────────────────

    [Fact]
    public async Task InvokeCapability_NonExistent_ReturnsInvalidRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/capabilities/nonexistent/invoke", new
        {
            caller_project_id = _projectId,
            caller_agent = "test-agent",
            request_json = "{}",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvokeCapability_Disabled_ReturnsDisabled()
    {
        await SeedDefinitionAsync("api-disabled-invoke", CapabilityStatuses.Disabled);

        var response = await _client.PostAsJsonAsync("/api/capabilities/api-disabled-invoke/invoke", new
        {
            caller_project_id = _projectId,
            caller_agent = "test-agent",
            request_json = "{}",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("disabled", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvokeCapability_NonReadOnly_ReturnsFailed()
    {
        await SeedDefinitionAsync("api-destructive-invoke",
            implKind: ImplementationKinds.HttpEndpoint,
            sideEffectLevel: SideEffectLevels.ExternalWrite);

        var response = await _client.PostAsJsonAsync("/api/capabilities/api-destructive-invoke/invoke", new
        {
            caller_project_id = _projectId,
            caller_agent = "test-agent",
            request_json = "{}",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("non_read_only_rejected", doc.RootElement.GetProperty("error_type").GetString());
    }

    [Fact]
    public async Task InvokeCapability_NoEndpoint_ReturnsFailed()
    {
        await SeedDefinitionAsync("api-no-endpoint",
            implKind: ImplementationKinds.HttpEndpoint,
            sideEffectLevel: SideEffectLevels.ReadOnly);

        var response = await _client.PostAsJsonAsync("/api/capabilities/api-no-endpoint/invoke", new
        {
            caller_project_id = _projectId,
            caller_agent = "test-agent",
            request_json = "{}",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        var error = doc.RootElement.GetProperty("error_message").GetString();
        Assert.Contains("no service_endpoint", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Invocation Audit ────────────────────────────────────────────────

    [Fact]
    public async Task GetInvocations_ListsByCapability()
    {
        await SeedDefinitionAsync("audit-list");

        var invokeResp = await _client.PostAsJsonAsync("/api/capabilities/audit-list/invoke", new
        {
            caller_project_id = _projectId,
            caller_agent = "test-agent",
            request_json = "{}",
        });
        invokeResp.EnsureSuccessStatusCode();

        var listResp = await _client.GetAsync("/api/capabilities/invocations?capabilityId=audit-list");
        listResp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var invocations = doc.RootElement.GetProperty("invocations").EnumerateArray().ToList();
        Assert.NotEmpty(invocations);
    }

    [Fact]
    public async Task GetInvocations_RequiresNoFilter_ReturnsAll()
    {
        var response = await _client.GetAsync("/api/capabilities/invocations");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("invocations", out _));
    }

    [Fact]
    public async Task GetInvocationByInvocationId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/capabilities/invocations/nonexistent-invocation");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetInvocationByInvocationId_ReturnsByPublicId()
    {
        await SeedDefinitionAsync("api-inv-pub");
        var invokeResp = await _client.PostAsJsonAsync("/api/capabilities/api-inv-pub/invoke", new
        {
            caller_project_id = _projectId,
            caller_agent = "test-agent",
            request_json = "{}",
        });
        invokeResp.EnsureSuccessStatusCode();
        using var invokeDoc = JsonDocument.Parse(await invokeResp.Content.ReadAsStringAsync());
        var invocationId = invokeDoc.RootElement.GetProperty("invocation_id").GetString();
        Assert.NotNull(invocationId);
        Assert.StartsWith("capinv_", invocationId);

        var getResp = await _client.GetAsync($"/api/capabilities/invocations/{invocationId}");
        getResp.EnsureSuccessStatusCode();
    }

    // ── AppFactory ──────────────────────────────────────────────────────

    private sealed class CapabilityAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-capability-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["DenCore:ConnectionString"] = DatabaseInitializer.GetConnectionString(_dbPath),
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            DatabaseInitializer.DisposeLeaseAsync(_dbPath).AsTask().GetAwaiter().GetResult();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}

/// <summary>
/// Extension to make mocking GET requests with query params easier.
/// </summary>
internal static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> MockGet(this string url, HttpClient client)
    {
        return client.GetAsync(url);
    }
}
