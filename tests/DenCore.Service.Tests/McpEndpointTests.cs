using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenCore.Service.Tests;

public sealed class McpEndpointTests : IAsyncLifetime
{
    private McpAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new McpAppFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostMcp_CreateTaskReturnsTasksCutoverTombstone()
    {
        var sessionId = await InitializeMcpSessionAsync();
        var projectId = $"mcp-native-tags-{Guid.NewGuid():N}";

        var createProject = await SendMcpRequestAsync(sessionId, 2, "create_project", new
        {
            id = projectId,
            name = "MCP native tags test"
        });
        Assert.DoesNotContain("\"isError\":true", createProject);

        var createTask = await SendMcpRequestAsync(sessionId, 3, "create_task", new
        {
            project_id = projectId,
            title = "Native array tags task",
            tags = new[] { "desktop", "electron" },
            verbose = true
        });

        Assert.DoesNotContain("Cannot get the value of a token type 'StartArray'", createTask);
        Assert.Contains("\"isError\":true", createTask);
        Assert.Contains("create_task has moved from den-core to den-services/tasks", createTask);
    }

    [Fact]
    public async Task PostMcp_Initialize_ReturnsStreamableHttpResponse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"den-core-test","version":"0.0.1"}}}
                """,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("Mcp-Session-Id"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"protocolVersion\"", body);
        Assert.Contains("\"tools\"", body);
    }

    private async Task<string> InitializeMcpSessionAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"den-core-test","version":"0.0.1"}}}
                """,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values));
        var sessionId = Assert.Single(values);

        using var initialized = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","method":"notifications/initialized"}
                """,
                Encoding.UTF8,
                "application/json")
        };
        initialized.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        initialized.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        initialized.Headers.Add("Mcp-Session-Id", sessionId);
        using var initializedResponse = await _client.SendAsync(initialized);
        initializedResponse.EnsureSuccessStatusCode();

        return sessionId;
    }

    private async Task<string> SendMcpRequestAsync(string sessionId, int id, string toolName, object arguments)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("Mcp-Session-Id", sessionId);

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private sealed class McpAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-core-mcp-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DenCore:Provider", "Postgres");
            builder.UseSetting("DenCore:ConnectionString", DatabaseInitializer.GetConnectionString(_dbPath));
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
