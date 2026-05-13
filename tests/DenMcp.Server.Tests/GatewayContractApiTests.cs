using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public sealed class GatewayContractApiTests : IAsyncLifetime
{
    private const string ProjectId = "gateway-contract-proj";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private GatewayContractAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new GatewayContractAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Gateway Contract Project" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Readiness_ReturnsGatewayRelevantChecks()
    {
        var response = await _client.GetAsync("/api/gateway/readiness");

        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;
        Assert.Equal("den-core-gateway-contract", root.GetProperty("service").GetString());
        Assert.NotEqual("blocked", root.GetProperty("status").GetString());
        var checks = root.GetProperty("checks");
        Assert.Equal("ready", checks.GetProperty("process").GetProperty("status").GetString());
        Assert.Equal("ready", checks.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("ready", checks.GetProperty("migrations").GetProperty("status").GetString());
        Assert.Equal("ready", checks.GetProperty("gateway_contract").GetProperty("status").GetString());
        Assert.Equal("degraded", checks.GetProperty("service_auth").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Bindings_ReturnsGatewayProjectionForActiveAndDegradedBindingsOnlyByDefault()
    {
        using var scope = _factory.Services.CreateScope();
        var bindings = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        await bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "gateway-active-1",
            ProjectId = ProjectId,
            AgentIdentity = "kate",
            AgentFamily = "hermes",
            Role = "router",
            TransportKind = "discord",
            SessionId = "session-active",
            Status = AgentInstanceBindingStatus.Active,
            Metadata = "{\"home_channel\":\"discord\"}"
        });
        await bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "gateway-inactive-1",
            ProjectId = ProjectId,
            AgentIdentity = "stale",
            AgentFamily = "hermes",
            TransportKind = "discord",
            Status = AgentInstanceBindingStatus.Inactive
        });

        var response = await _client.GetAsync($"/api/gateway/bindings?projectId={ProjectId}");

        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("gateway-active-1", item.GetProperty("instance_id").GetString());
        Assert.Equal(ProjectId, item.GetProperty("project_id").GetString());
        Assert.Equal("kate", item.GetProperty("agent_identity").GetString());
        Assert.Equal("router", item.GetProperty("role").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.Equal("discord", item.GetProperty("metadata").GetProperty("home_channel").GetString());
    }

    [Fact]
    public async Task GatewayEndpoints_RequireConfiguredServiceToken()
    {
        await using var authFactory = new GatewayContractAppFactory(serviceToken: "test-token");
        var initializer = new DatabaseInitializer(authFactory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        using var client = authFactory.CreateClient();

        var unauthorized = await client.GetAsync("/api/gateway/readiness");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        var authorized = await client.GetAsync("/api/gateway/readiness");
        authorized.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SentinelEvents_AreDurablyAppendedAndVisibleThroughOutbox()
    {
        var request = new
        {
            sentinel_id = "gateway-local-sentinel",
            event_type = "outage",
            state = "paused",
            project_id = ProjectId,
            outage_id = "outage-42",
            reason = "channels unavailable",
            cursor = "cursor-42",
            dedupe_key = "gateway-contract-sentinel-outage-42"
        };

        var first = await PostJsonAsync("/api/gateway/sentinel/events", request);
        first.EnsureSuccessStatusCode();
        using var firstPayload = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync());
        var entryId = firstPayload.RootElement.GetProperty("agent_stream_entry_id").GetInt32();
        Assert.Equal("accepted", firstPayload.RootElement.GetProperty("status").GetString());
        Assert.Equal("gateway_sentinel_outage", firstPayload.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(entryId.ToString("D12"), firstPayload.RootElement.GetProperty("outbox_cursor").GetString());

        var duplicate = await PostJsonAsync("/api/gateway/sentinel/events", request);
        duplicate.EnsureSuccessStatusCode();
        using var duplicatePayload = await JsonDocument.ParseAsync(await duplicate.Content.ReadAsStreamAsync());
        Assert.Equal(entryId, duplicatePayload.RootElement.GetProperty("agent_stream_entry_id").GetInt32());

        var outbox = await _client.GetAsync($"/api/events/outbox?after=0&projectId={ProjectId}&limit=20");
        outbox.EnsureSuccessStatusCode();
        using var outboxPayload = await JsonDocument.ParseAsync(await outbox.Content.ReadAsStreamAsync());
        var item = Assert.Single(outboxPayload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("agent_stream.gateway_sentinel_outage", item.GetProperty("event_type").GetString());
        Assert.Equal("agent_stream_entry", item.GetProperty("source_kind").GetString());
        Assert.Equal("gateway-contract-sentinel-outage-42", item.GetProperty("dedupe_key").GetString());
        Assert.Equal("attention", item.GetProperty("severity").GetString());
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOpts);
        return _client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private sealed class GatewayContractAppFactory(string? serviceToken = null) : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-core-gateway-contract-{Guid.NewGuid()}.db");

        public string DatabasePath => _dbPath;

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
                    ["llm-model"] = "fake",
                    ["DenMcp:GatewayContract:ServiceToken"] = serviceToken
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient, FakeLlmClient>();
                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory($"Data Source={_dbPath}"));
                if (serviceToken is not null)
                {
                    services.RemoveAll<DenMcpOptions>();
                    services.AddSingleton(new DenMcpOptions
                    {
                        DatabasePath = _dbPath,
                        GatewayContract = new GatewayContractOptions { ServiceToken = serviceToken }
                    });
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
            => Task.FromResult("{}");
    }
}
