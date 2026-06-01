using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DenMcp.Core.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenMcp.Server.Tests;

public sealed class McpToolProfileTests : IAsyncLifetime
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
    public async Task UnqualifiedMcp_ReturnsFullToolList()
    {
        var sessionId = await InitializeMcpSessionAsync();
        var tools = await ListToolsAsync(sessionId);

        Assert.True(tools.Count > 80, $"Expected >80 tools for full list, got {tools.Count}");
        Assert.Contains(tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()), t => t == "create_task");
        Assert.Contains(tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()), t => t == "legacy_get_dispatch");
        Assert.Contains(tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()), t => t == "send_agent_stream_message");
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("runner")]
    [InlineData("admin-current")]
    public async Task NormalProfile_ExcludesLegacyAndDiagnostics(string profile)
    {
        var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");
        var tools = await ListToolsAsync(sessionId);

        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.DoesNotContain("legacy_get_dispatch", names);
        Assert.DoesNotContain("legacy_launch_pi_worker", names);
        Assert.DoesNotContain("send_agent_stream_message", names);
    }

    [Theory]
    [InlineData("planner", 35)]
    [InlineData("runner", 45)]
    [InlineData("admin-current", 79)]
    public async Task Profile_ReturnsExpectedToolCount(string profile, int minExpected)
    {
        var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");
        var tools = await ListToolsAsync(sessionId);

        Assert.True(tools.Count >= minExpected,
            $"Profile '{profile}' expected at least {minExpected} tools, got {tools.Count}");
    }

    [Fact]
    public async Task LegacyFull_IncludesLegacyTools()
    {
        var sessionId = await InitializeMcpSessionAsync(query: "tool_profile=legacy-full");
        var tools = await ListToolsAsync(sessionId);

        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("legacy_get_dispatch", names);
        Assert.Contains("legacy_launch_pi_worker", names);
        Assert.Contains("send_agent_stream_message", names);
    }

    [Fact]
    public async Task HiddenTool_CallIsRejected()
    {
        var sessionId = await InitializeMcpSessionAsync(query: "tool_profile=planner");

        // planner does not include legacy_get_dispatch
        var result = await SendToolCallAsync(sessionId, 99, "legacy_get_dispatch", new { dispatch_id = 1 });
        Assert.Contains("error", result);
        Assert.Contains("\"code\":-32", result); // JSON-RPC method/tool not found error code
    }

    [Fact]
    public async Task SendUserNotification_IsListedAndCallableOnlyForOperatorProfiles()
    {
        var operatorProfiles = new[] { "planner", "runner", "admin-current" };
        foreach (var profile in operatorProfiles)
        {
            var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");
            var tools = await ListToolsAsync(sessionId);
            var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
            Assert.Contains("send_user_notification", names);

            var result = await SendToolCallAsync(sessionId, 200, "send_user_notification", new
            {
                project_id = "den-core",
                sender = profile,
                content = $"test notification from {profile}",
                urgency = "low"
            });
            Assert.DoesNotContain("error", result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sent message", result);
        }

        var workerProfiles = new[]
        {
            "worker-coder",
            "worker-reviewer",
            "worker-validator",
            "worker-drift-checker",
            "worker-packet-auditor"
        };
        foreach (var profile in workerProfiles)
        {
            var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");
            var tools = await ListToolsAsync(sessionId);
            var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
            Assert.DoesNotContain("send_user_notification", names);

            var result = await SendToolCallAsync(sessionId, 201, "send_user_notification", new
            {
                project_id = "den-core",
                sender = profile,
                content = $"blocked worker notification attempt from {profile}"
            });
            Assert.Contains("error", result);
            Assert.Contains("\"code\":-32", result);
        }
    }

    [Fact]
    public async Task UnknownProfile_FailClosed()
    {
        var sessionId = await InitializeMcpSessionAsync(query: "tool_profile=nonexistent-profile");
        var tools = await ListToolsAsync(sessionId);

        // Fail closed: unknown profile should result in empty tool list
        Assert.Empty(tools);
    }

    [Fact]
    public async Task UnknownBundle_FailClosed()
    {
        var sessionId = await InitializeMcpSessionAsync(query: "tool_bundles=core,nonexistent");
        var tools = await ListToolsAsync(sessionId);

        // Fail closed: unknown bundle should result in empty tool list
        Assert.Empty(tools);
    }

    [Fact]
    public async Task HeaderSelector_Works()
    {
        var sessionId = await InitializeMcpSessionAsync(headers: new Dictionary<string, string>
        {
            ["X-Den-MCP-Tool-Profile"] = "planner"
        });
        var tools = await ListToolsAsync(sessionId);

        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.DoesNotContain("legacy_get_dispatch", names);
        Assert.Contains("create_task", names);
    }

    [Fact]
    public async Task BundleSelector_Works()
    {
        var sessionId = await InitializeMcpSessionAsync(query: "tool_bundles=legacy");
        var tools = await ListToolsAsync(sessionId);

        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("legacy_get_dispatch", names);
        Assert.DoesNotContain("create_task", names);
    }

    [Fact]
    public async Task SpecBundleSelectors_WorkAndKeepLegacyOptIn()
    {
        var currentSession = await InitializeMcpSessionAsync(query: "tool_bundles=all-current");
        var currentTools = await ListToolsAsync(currentSession);
        var currentNames = currentTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("create_task", currentNames);
        Assert.Contains("request_review", currentNames);
        Assert.DoesNotContain("legacy_get_dispatch", currentNames);
        Assert.DoesNotContain("send_agent_stream_message", currentNames);

        var legacySession = await InitializeMcpSessionAsync(query: "tool_bundles=all-with-legacy");
        var legacyTools = await ListToolsAsync(legacySession);
        var legacyNames = legacyTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("legacy_get_dispatch", legacyNames);
        Assert.Contains("send_agent_stream_message", legacyNames);
    }

    [Fact]
    public async Task SpecFunctionalBundleSelectors_Work()
    {
        var readSession = await InitializeMcpSessionAsync(query: "tool_bundles=core-read");
        var readTools = await ListToolsAsync(readSession);
        var readNames = readTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("get_task", readNames);
        Assert.Contains("list_documents", readNames);
        Assert.DoesNotContain("create_task", readNames);
        Assert.DoesNotContain("legacy_get_dispatch", readNames);

        var workflowSession = await InitializeMcpSessionAsync(query: "tool_bundles=worker-workflow");
        var workflowTools = await ListToolsAsync(workflowSession);
        var workflowNames = workflowTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("register_worker_run", workflowNames);
        Assert.Contains("prepare_coder_context_packet", workflowNames);
        Assert.DoesNotContain("legacy_launch_pi_worker", workflowNames);
    }

    [Fact]
    public async Task GovernanceAdminBundle_DoesNotIncludeLegacy()
    {
        var sessionId = await InitializeMcpSessionAsync(query: "tool_bundles=governance-admin");
        var tools = await ListToolsAsync(sessionId);
        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();

        Assert.Contains("create_project", names);
        Assert.Contains("add_agent_guidance_entry", names);
        Assert.DoesNotContain("legacy_get_dispatch", names);
        Assert.DoesNotContain("legacy_launch_pi_worker", names);
    }

    [Fact]
    public async Task ProfileAndBundle_Combines()
    {
        var sessionId = await InitializeMcpSessionAsync(query: "tool_profile=planner&tool_bundles=legacy");
        var tools = await ListToolsAsync(sessionId);

        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("create_task", names); // from planner
        Assert.Contains("legacy_get_dispatch", names); // from legacy bundle
    }

    [Theory]
    [InlineData("worker-validator")]
    [InlineData("worker-drift-checker")]
    [InlineData("worker-packet-auditor")]
    public async Task NewWorkerProfiles_AreNonEmpty(string profile)
    {
        var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");
        var tools = await ListToolsAsync(sessionId);
        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();

        Assert.True(tools.Count >= 14,
            $"Profile '{profile}' expected at least 14 tools, got {tools.Count} ({string.Join(", ", names.OrderBy(n => n))})");
        Assert.Contains("get_worker_run_status", names);
        Assert.Contains("post_worker_completion_packet", names);
        Assert.Contains("get_latest_task_packet", names);
        Assert.Contains("render_worker_prompt", names);
    }

    [Theory]
    [InlineData("worker-validator")]
    [InlineData("worker-drift-checker")]
    [InlineData("worker-packet-auditor")]
    public async Task NewWorkerProfiles_ExcludeLegacyAndDiagnostics(string profile)
    {
        var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");
        var tools = await ListToolsAsync(sessionId);
        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();

        Assert.DoesNotContain("legacy_get_dispatch", names);
        Assert.DoesNotContain("legacy_launch_pi_worker", names);
        Assert.DoesNotContain("legacy_launch_validator_worker", names);
        Assert.DoesNotContain("legacy_launch_drift_checker_worker", names);
        Assert.DoesNotContain("legacy_launch_packet_auditor_worker", names);
        Assert.DoesNotContain("send_agent_stream_message", names);
    }

    [Theory]
    [InlineData("worker-validator", "prepare_validator_context_packet")]
    [InlineData("worker-drift-checker", "prepare_drift_checker_context_packet")]
    [InlineData("worker-packet-auditor", "prepare_packet_auditor_context_packet")]
    public async Task NewWorkerProfiles_IncludeRoleSpecificPacket(string profile, string expectedPacketTool)
    {
        var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");
        var tools = await ListToolsAsync(sessionId);
        var names = tools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();

        Assert.Contains(expectedPacketTool, names);
        Assert.Contains("get_latest_task_packet", names);
        Assert.Contains("get_worker_run_status", names);
        Assert.Contains("post_worker_completion_packet", names);
    }

    [Theory]
    [InlineData("worker-validator")]
    [InlineData("worker-drift-checker")]
    [InlineData("worker-packet-auditor")]
    public async Task NewWorkerProfiles_HiddenToolCallIsRejected(string profile)
    {
        var sessionId = await InitializeMcpSessionAsync(query: $"tool_profile={profile}");

        // These profiles do NOT include legacy tools
        var result = await SendToolCallAsync(sessionId, 99, "legacy_get_dispatch", new { dispatch_id = 1 });
        Assert.Contains("error", result);
        Assert.Contains("\"code\":-32", result); // JSON-RPC method/tool not found error code
    }

    [Fact]
    public async Task DiscussionTools_ProfileExposureAndHiddenCallEnforcement()
    {
        var plannerSession = await InitializeMcpSessionAsync(query: "tool_profile=planner");
        var plannerTools = await ListToolsAsync(plannerSession);
        var plannerNames = plannerTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("get_document_discussion", plannerNames);
        Assert.Contains("comment_on_document", plannerNames);
        Assert.Contains("list_discussion_threads", plannerNames);
        Assert.Contains("get_discussion_thread", plannerNames);
        Assert.Contains("create_discussion_comment", plannerNames);
        Assert.DoesNotContain("update_discussion_thread", plannerNames);

        var runnerSession = await InitializeMcpSessionAsync(query: "tool_profile=runner");
        var runnerTools = await ListToolsAsync(runnerSession);
        var runnerNames = runnerTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("comment_on_document", runnerNames);
        Assert.Contains("create_discussion_comment", runnerNames);

        var adminSession = await InitializeMcpSessionAsync(query: "tool_profile=admin-current");
        var adminTools = await ListToolsAsync(adminSession);
        var adminNames = adminTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("update_discussion_thread", adminNames);

        var coderSession = await InitializeMcpSessionAsync(query: "tool_profile=worker-coder");
        var coderTools = await ListToolsAsync(coderSession);
        var coderNames = coderTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.DoesNotContain("get_document_discussion", coderNames);
        Assert.DoesNotContain("list_discussion_threads", coderNames);
        Assert.DoesNotContain("get_discussion_thread", coderNames);
        Assert.DoesNotContain("comment_on_document", coderNames);
        Assert.DoesNotContain("create_discussion_comment", coderNames);
        Assert.DoesNotContain("update_discussion_thread", coderNames);

        var reviewerSession = await InitializeMcpSessionAsync(query: "tool_profile=worker-reviewer");
        var reviewerTools = await ListToolsAsync(reviewerSession);
        var reviewerNames = reviewerTools.Where(t => t is not null).Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("get_document_discussion", reviewerNames);
        Assert.Contains("list_discussion_threads", reviewerNames);
        Assert.Contains("get_discussion_thread", reviewerNames);
        Assert.DoesNotContain("comment_on_document", reviewerNames);
        Assert.DoesNotContain("create_discussion_comment", reviewerNames);
        Assert.DoesNotContain("update_discussion_thread", reviewerNames);

        var hiddenCall = await SendToolCallAsync(coderSession, 101, "get_document_discussion", new
        {
            project_id = "den-core",
            slug = "fake"
        });
        Assert.Contains("error", hiddenCall);
        Assert.Contains("\"code\":-32", hiddenCall);
    }

    private async Task<List<JsonNode?>> ListToolsAsync(string sessionId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { }
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
        var body = await response.Content.ReadAsStringAsync();

        // The response is SSE-formatted: data: {...}
        var lines = body.Split('\n').Where(l => l.StartsWith("data: ")).ToList();
        if (lines.Count == 0)
            return new List<JsonNode?>();

        var json = lines[0]["data: ".Length..];
        var doc = JsonSerializer.Deserialize<JsonObject>(json);
        if (doc is null || doc.TryGetPropertyValue("result", out var resultNode) == false || resultNode is not JsonObject result)
            return new List<JsonNode?>();

        if (result.TryGetPropertyValue("tools", out var toolsNode) && toolsNode is JsonArray arr)
            return arr.ToList();

        return new List<JsonNode?>();
    }

    private async Task<string> InitializeMcpSessionAsync(string? query = null, Dictionary<string, string>? headers = null)
    {
        var url = "/mcp";
        if (!string.IsNullOrEmpty(query))
            url += "?" + query;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
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
        if (headers is not null)
        {
            foreach (var h in headers)
                request.Headers.Add(h.Key, h.Value);
        }

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values));
        var sessionId = Assert.Single(values);

        using var initialized = new HttpRequestMessage(HttpMethod.Post, url)
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
        if (headers is not null)
        {
            foreach (var h in headers)
                if (!initialized.Headers.Contains(h.Key))
                    initialized.Headers.Add(h.Key, h.Value);
        }
        using var initializedResponse = await _client.SendAsync(initialized);
        initializedResponse.EnsureSuccessStatusCode();

        return sessionId;
    }

    private async Task<string> SendToolCallAsync(string sessionId, int id, string toolName, object arguments)
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
