using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenCore.Data;
using DenCore.Llm;
using DenCore.Models;
using DenCore.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenCore.Service.Tests;

public sealed class MessageWaitApiTests : IAsyncLifetime
{
    private const string ProjectId = "wait-api-test";
    private WaitApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WaitApiFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Wait API Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WaitRoute_ReturnsCompactMessages_WhenUnreadMessagesExist()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            var task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Wait route task" });
            await messages.CreateAsync(new Message
            {
                ProjectId = ProjectId,
                Sender = "coder",
                Content = new string('x', 800),
                TaskId = task.Id
            });
        }

        var waitResponse = await _client.GetAsync($"/api/projects/{ProjectId}/messages/wait?unreadFor=reviewer&timeoutMs=500&limit=5");

        Assert.Equal(HttpStatusCode.OK, waitResponse.StatusCode);
        using var doc = JsonDocument.Parse(await waitResponse.Content.ReadAsStringAsync());
        Assert.Equal("messages", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());

        var message = Assert.Single(doc.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("coder", message.GetProperty("sender").GetString());
        Assert.True(message.GetProperty("task_id").GetInt32() > 0);
        Assert.True(message.GetProperty("content_preview").GetString()!.Length <= 500);
        Assert.False(message.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task WaitRoute_ReturnsTimeoutReceipt_WhenNoUnreadMessagesArrive()
    {
        var waitResponse = await _client.GetAsync($"/api/projects/{ProjectId}/messages/wait?unreadFor=agent&timeoutMs=500&limit=5");

        Assert.Equal(HttpStatusCode.OK, waitResponse.StatusCode);
        using var doc = JsonDocument.Parse(await waitResponse.Content.ReadAsStringAsync());
        Assert.Equal("timeout", doc.RootElement.GetProperty("status").GetString());
        Assert.Empty(doc.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Contains("Stop polling", doc.RootElement.GetProperty("guidance").GetString());
    }

    [Fact]
    public async Task WaitRoute_RequiresUnreadForAgent()
    {
        var waitResponse = await _client.GetAsync($"/api/projects/{ProjectId}/messages/wait?timeoutMs=500");

        Assert.Equal(HttpStatusCode.BadRequest, waitResponse.StatusCode);
        using var doc = JsonDocument.Parse(await waitResponse.Content.ReadAsStringAsync());
        Assert.Contains("unreadFor", doc.RootElement.GetProperty("error").GetString());
    }

    private sealed class WaitApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-core-wait-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenCore:DatabasePath"] = _dbPath,
                    ["DenCore:Llm:Endpoint"] = "",
                    ["DenCore:Llm:Model"] = "test-model"
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
                services.AddSingleton<INotificationChannel, NoOpNotificationChannel>();
            });
        }
    }

    private sealed class NoOpNotificationChannel : INotificationChannel
    {
        public Task SendDispatchNotificationAsync(
            DispatchEntry dispatch,
            string summary,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAgentStatusAsync(
            string projectId,
            string agent,
            string status,
            int? taskId = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
    }
}
