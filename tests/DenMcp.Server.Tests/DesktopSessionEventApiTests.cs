using System.Net.Http.Json;
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

public class DesktopSessionEventApiTests : IAsyncLifetime
{
    private const string ProjectId = "desktop-session-event-api-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private SessionEventAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ProjectTask _task = null!;
    private AgentWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        _factory = new SessionEventAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Desktop Session Event API Test" });

        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        _task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Session event task" });

        var workspaces = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceRepository>();
        _workspace = await workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = "ws-session-event-api",
            ProjectId = ProjectId,
            TaskId = _task.Id,
            Branch = "task/session-event-api",
            WorktreePath = "/tmp/session-event-api",
            BaseBranch = "main"
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostAndGetSessionEvents_RoundTrips()
    {
        // Append a session created event
        var createdResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new
            {
                task_id = _task.Id,
                workspace_id = _workspace.Id,
                source_instance_id = "desktop-a",
                session_id = "pty-10",
                event_type = "created",
                payload = "{\"kind\":\"terminal\",\"backend\":\"direct_pty\"}",
                requested_by = "user",
                reason = "Launched from desktop",
                observed_at = DateTime.UtcNow.AddSeconds(-10)
            }, JsonOpts);
        createdResponse.EnsureSuccessStatusCode();
        Assert.Equal(201, (int)createdResponse.StatusCode);

        using var createdJson = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        Assert.Equal(ProjectId, createdJson.RootElement.GetProperty("project_id").GetString());
        Assert.Equal("desktop-a", createdJson.RootElement.GetProperty("source_instance_id").GetString());
        Assert.Equal("pty-10", createdJson.RootElement.GetProperty("session_id").GetString());
        Assert.Equal("created", createdJson.RootElement.GetProperty("event_type").GetString());
        Assert.NotNull(createdJson.RootElement.GetProperty("payload").GetString());
        Assert.Equal("user", createdJson.RootElement.GetProperty("requested_by").GetString());
        Assert.Contains("desktop", createdJson.RootElement.GetProperty("reason").GetString());
        Assert.True(createdJson.RootElement.GetProperty("id").GetInt64() > 0);

        // Append a status change event for the same session
        var statusResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new
            {
                source_instance_id = "desktop-a",
                session_id = "pty-10",
                event_type = "status_changed",
                payload = "{\"from\":\"starting\",\"to\":\"running\"}",
                observed_at = DateTime.UtcNow
            }, JsonOpts);
        statusResponse.EnsureSuccessStatusCode();

        // Append an event for a different session
        await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new
            {
                source_instance_id = "desktop-a",
                session_id = "pty-11",
                event_type = "created",
                observed_at = DateTime.UtcNow
            }, JsonOpts);

        // List events by session
        var listResponse = await _client.GetAsync(
            $"/api/projects/{ProjectId}/desktop/session-events?sessionId=pty-10");
        listResponse.EnsureSuccessStatusCode();

        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var events = listJson.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("status_changed", events[0].GetProperty("event_type").GetString());
        Assert.Equal("created", events[1].GetProperty("event_type").GetString());

        // List by source instance
        var sourceResponse = await _client.GetAsync(
            $"/api/projects/{ProjectId}/desktop/session-events?sourceInstanceId=desktop-a&limit=10");
        sourceResponse.EnsureSuccessStatusCode();
        using var sourceJson = JsonDocument.Parse(await sourceResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, sourceJson.RootElement.EnumerateArray().Count());
    }

    [Fact]
    public async Task PostSessionEvent_RejectsMissingRequiredFields()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new { }, JsonOpts);
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task FilterByEventTypes()
    {
        // Create events of various types
        await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new { source_instance_id = "desktop-b", session_id = "pty-20", event_type = "created", observed_at = DateTime.UtcNow },
            JsonOpts);
        await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new { source_instance_id = "desktop-b", session_id = "pty-20", event_type = "attached", observed_at = DateTime.UtcNow },
            JsonOpts);
        await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new { source_instance_id = "desktop-b", session_id = "pty-20", event_type = "lease_acquired", observed_at = DateTime.UtcNow },
            JsonOpts);

        var filterResponse = await _client.GetAsync(
            $"/api/projects/{ProjectId}/desktop/session-events?sessionId=pty-20&eventTypes=created,attached");
        filterResponse.EnsureSuccessStatusCode();
        using var filterJson = JsonDocument.Parse(await filterResponse.Content.ReadAsStringAsync());
        var events = filterJson.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.GetProperty("event_type").GetString() == "created");
        Assert.Contains(events, e => e.GetProperty("event_type").GetString() == "attached");
    }

    [Fact]
    public async Task PostAcceptsSplitReconnectEventsAndLegacyReconnect()
    {
        foreach (var eventType in new[] { "reconnect", "reconnect_requested", "reconnected" })
        {
            var response = await _client.PostAsJsonAsync(
                $"/api/projects/{ProjectId}/desktop/session-events",
                new { source_instance_id = "desktop-reconnect", session_id = "pty-reconnect", event_type = eventType, observed_at = DateTime.UtcNow },
                JsonOpts);
            response.EnsureSuccessStatusCode();
        }

        var filterResponse = await _client.GetAsync(
            $"/api/projects/{ProjectId}/desktop/session-events?sessionId=pty-reconnect&eventTypes=reconnect_requested,reconnected");
        filterResponse.EnsureSuccessStatusCode();
        using var filterJson = JsonDocument.Parse(await filterResponse.Content.ReadAsStringAsync());
        var events = filterJson.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.GetProperty("event_type").GetString() == "reconnect_requested");
        Assert.Contains(events, e => e.GetProperty("event_type").GetString() == "reconnected");
    }

    [Fact]
    public async Task PostAndListCrossSourceIsolation()
    {
        await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new { source_instance_id = "desktop-c", session_id = "pty-30", event_type = "created", observed_at = DateTime.UtcNow },
            JsonOpts);
        await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new { source_instance_id = "desktop-d", session_id = "pty-30", event_type = "created", observed_at = DateTime.UtcNow },
            JsonOpts);

        // Listing by source should isolate
        var cResponse = await _client.GetAsync(
            $"/api/projects/{ProjectId}/desktop/session-events?sourceInstanceId=desktop-c");
        cResponse.EnsureSuccessStatusCode();
        using var cJson = JsonDocument.Parse(await cResponse.Content.ReadAsStringAsync());
        Assert.Single(cJson.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task FilterByTask()
    {
        await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/desktop/session-events",
            new { source_instance_id = "desktop-e", session_id = "pty-40", task_id = _task.Id, event_type = "created", observed_at = DateTime.UtcNow },
            JsonOpts);

        var taskResponse = await _client.GetAsync(
            $"/api/projects/{ProjectId}/desktop/session-events?taskId={_task.Id}");
        taskResponse.EnsureSuccessStatusCode();
        using var taskJson = JsonDocument.Parse(await taskResponse.Content.ReadAsStringAsync());
        var events = taskJson.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(_task.Id, e.GetProperty("task_id").GetInt32()));
    }

    private sealed class SessionEventAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-session-event-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = "",
                    ["DenMcp:Llm:Model"] = "test-model"
                });
            });

            builder.ConfigureServices(services =>
            {
                var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private sealed class NoOpLlmClient : ILlmClient
        {
            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
                => Task.FromResult("{}");
        }
    }
}
