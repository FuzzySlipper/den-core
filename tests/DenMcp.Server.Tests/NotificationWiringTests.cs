using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenMcp.Server.Tests;

public class NotificationWiringTests : IAsyncLifetime
{
    private WiringAppFactory _factory = null!;
    private HttpClient _client = null!;
    private const string ProjectId = "notify-test";

    public async Task InitializeAsync()
    {
        _factory = new WiringAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Notification Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task EnableLegacyDispatchRoutingAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        await docs.UpsertAsync(new Document
        {
            ProjectId = ProjectId,
            Slug = "dispatch-routing",
            Title = "Legacy Dispatch Routing",
            Content = """
            {
              "roles": {},
              "triggers": [
                {
                  "event": "message_received",
                  "has_recipient": true,
                  "dispatch_to": "{recipient}"
                }
              ],
              "defaults": {
                "legacy_dispatch_enabled": true,
                "expiry_minutes": 1440
              }
            }
            """,
            DocType = DocType.Convention
        });
    }

    private async Task<int> SeedTaskAsync(IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Notify task" });
        return task.Id;
    }

    [Fact]
    public async Task RestMessageCreate_DoesNotSendDispatchNotificationByDefault()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Review feedback for notification routing",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });

        response.EnsureSuccessStatusCode();

        Assert.Empty(_factory.RecordingChannel.DispatchNotifications);
    }

    [Fact]
    public async Task RestMessageCreate_WithLegacyDispatchRouting_SendsDispatchNotification()
    {
        await EnableLegacyDispatchRoutingAsync(_factory.Services);

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Review feedback for notification routing",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });

        response.EnsureSuccessStatusCode();

        var notification = Assert.Single(_factory.RecordingChannel.DispatchNotifications);
        Assert.Equal(ProjectId, notification.ProjectId);
        Assert.Equal("claude-code", notification.TargetAgent);
        Assert.Contains("review feedback", notification.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestAgentLifecycle_SendsStatusNotifications()
    {
        var checkInResponse = await _client.PostAsJsonAsync("/api/agents/checkin", new
        {
            agent = "claude-code",
            project_id = ProjectId,
            session_id = "session-1"
        });
        checkInResponse.EnsureSuccessStatusCode();

        var checkOutResponse = await _client.PostAsJsonAsync("/api/agents/checkout", new
        {
            agent = "claude-code",
            project_id = ProjectId,
            session_id = "session-1"
        });
        checkOutResponse.EnsureSuccessStatusCode();

        Assert.Collection(
            _factory.RecordingChannel.AgentStatuses,
            item =>
            {
                Assert.Equal(ProjectId, item.ProjectId);
                Assert.Equal("claude-code", item.Agent);
                Assert.Equal("checked_in", item.Status);
            },
            item =>
            {
                Assert.Equal(ProjectId, item.ProjectId);
                Assert.Equal("claude-code", item.Agent);
                Assert.Equal("checked_out", item.Status);
            });
    }

    [Fact]
    public async Task RestTaskUpdate_SendsTaskStatusNotification()
    {
        var taskId = await SeedTaskAsync();

        var response = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}", new
        {
            agent = "claude-code",
            status = "review"
        });
        response.EnsureSuccessStatusCode();

        var statusUpdate = Assert.Single(_factory.RecordingChannel.AgentStatuses,
            item => item.TaskId == taskId && item.Status == "review");
        Assert.Equal(ProjectId, statusUpdate.ProjectId);
        Assert.Equal("claude-code", statusUpdate.Agent);
    }

    [Fact]
    public async Task PrimaryWritePaths_SucceedWhenNotificationsFail()
    {
        using var factory = new WiringAppFactory(useFailingNotifications: true);
        using var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            await projects.CreateAsync(new Project { Id = ProjectId, Name = "Notification Test" });
        }

        var messageResponse = await client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Should persist",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });
        Assert.Equal(HttpStatusCode.Created, messageResponse.StatusCode);

        var taskId = await SeedTaskAsync(factory.Services);
        var taskResponse = await client.PutAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}", new
        {
            agent = "claude-code",
            status = "review"
        });
        Assert.Equal(HttpStatusCode.OK, taskResponse.StatusCode);

        var checkInResponse = await client.PostAsJsonAsync("/api/agents/checkin", new
        {
            agent = "claude-code",
            project_id = ProjectId,
            session_id = "session-2"
        });
        Assert.Equal(HttpStatusCode.OK, checkInResponse.StatusCode);
    }

    private sealed class WiringAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-notify-test-{Guid.NewGuid()}.db");
        private readonly bool _useFailingNotifications;

        public WiringAppFactory(bool useFailingNotifications = false)
        {
            _useFailingNotifications = useFailingNotifications;
        }

        public RecordingNotificationChannel RecordingChannel { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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
                var initializer = new DatabaseInitializer(_dbPath,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());

                services.RemoveAll<INotificationChannel>();
                if (_useFailingNotifications)
                    services.AddSingleton<INotificationChannel>(new FailingNotificationChannel());
                else
                    services.AddSingleton<INotificationChannel>(RecordingChannel);
            });
        }
    }

    private sealed class RecordingNotificationChannel : INotificationChannel
    {
        public ConcurrentQueue<DispatchNotificationRecord> DispatchNotifications { get; } = new();
        public ConcurrentQueue<AgentStatusRecord> AgentStatuses { get; } = new();

        public Task SendDispatchNotificationAsync(
            DispatchEntry dispatch,
            string summary,
            CancellationToken cancellationToken = default)
        {
            DispatchNotifications.Enqueue(new DispatchNotificationRecord(dispatch.ProjectId, dispatch.TargetAgent, summary));
            return Task.CompletedTask;
        }

        public Task SendAgentStatusAsync(
            string projectId,
            string agent,
            string status,
            int? taskId = null,
            CancellationToken cancellationToken = default)
        {
            AgentStatuses.Enqueue(new AgentStatusRecord(projectId, agent, status, taskId));
            return Task.CompletedTask;
        }

        public Task StartListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingNotificationChannel : INotificationChannel
    {
        public Task SendDispatchNotificationAsync(
            DispatchEntry dispatch,
            string summary,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated notification failure");

        public Task SendAgentStatusAsync(
            string projectId,
            string agent,
            string status,
            int? taskId = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated notification failure");

        public Task StartListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record DispatchNotificationRecord(string ProjectId, string TargetAgent, string Summary);
    private sealed record AgentStatusRecord(string ProjectId, string Agent, string Status, int? TaskId);

    private sealed class NoOpLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
    }
}
