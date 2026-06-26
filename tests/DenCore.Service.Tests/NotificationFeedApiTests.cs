using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenCore.Data;
using DenCore.Llm;
using DenCore.Models;
using DenCore.Services;
using DenCore.Service.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenCore.Service.Tests;

public class NotificationFeedApiTests : IAsyncLifetime
{
    private sealed class NotificationFeedAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-notif-feed-test-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DenCore:Provider", "Postgres");
            builder.UseSetting("DenCore:ConnectionString", DatabaseInitializer.GetConnectionString(_dbPath));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenCore:DatabasePath"] = _dbPath,
                    ["DenCore:ConnectionString"] = DatabaseInitializer.GetConnectionString(_dbPath),
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
                services.AddSingleton<INotificationChannel>(new NoOpNotificationChannel());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            DatabaseInitializer.DisposeLeaseAsync(_dbPath).AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class NoOpNotificationChannel : INotificationChannel
    {
        public Task SendDispatchNotificationAsync(DispatchEntry dispatch, string summary, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SendAgentStatusAsync(string projectId, string agent, string status, int? taskId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StartListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
    }

    private NotificationFeedAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new NotificationFeedAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = "proj-a", Name = "Project A" });
        await projects.CreateAsync(new Project { Id = "proj-b", Name = "Project B" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> SeedTaskAsync(string projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await tasks.CreateAsync(new ProjectTask { ProjectId = projectId, Title = "Test task" });
        return task.Id;
    }

    private async Task<Message> SeedNotificationAsync(string projectId, string sender, string content,
        int? taskId = null, string? urgency = null, string? metadataType = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        var metadataDict = new Dictionary<string, object>();
        if (urgency is not null) metadataDict["urgency"] = urgency;
        if (metadataType is not null) metadataDict["type"] = metadataType;
        metadataDict["source_sender"] = sender;

        var metadataJson = JsonSerializer.Serialize(metadataDict);
        var metadata = JsonSerializer.Deserialize<JsonElement>(metadataJson);

        var msg = new Message
        {
            ProjectId = projectId,
            Sender = sender,
            Content = content,
            TaskId = taskId,
            Intent = MessageIntent.Notification,
            Metadata = metadata
        };

        return await repo.CreateAsync(msg);
    }

    // ---- Feed listing tests ----

    [Fact]
    public async Task GetNotificationFeed_ReturnsNotifications()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "Notification 1");
        await SeedNotificationAsync("proj-a", "agent-1", "Notification 2");

        var response = await _client.GetAsync("/api/user-notifications");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Equal(2, notifications!.Length);
    }

    [Fact]
    public async Task GetNotificationFeed_NewestFirst()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "First");
        await SeedNotificationAsync("proj-a", "agent-1", "Second");

        var response = await _client.GetAsync("/api/user-notifications");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Equal(2, notifications!.Length);
        // Newest first
        Assert.Equal("Second", notifications![0].GetProperty("content").GetString());
        Assert.Equal("First", notifications[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNotificationFeed_CrossProject()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "In A");
        await SeedNotificationAsync("proj-b", "agent-2", "In B");

        var response = await _client.GetAsync("/api/user-notifications");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Equal(2, notifications!.Length);
    }

    [Fact]
    public async Task GetNotificationFeed_FilterByProject()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "In A");
        await SeedNotificationAsync("proj-b", "agent-2", "In B");

        var response = await _client.GetAsync("/api/user-notifications?projectId=proj-a");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);
        Assert.Equal("In A", notifications![0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNotificationFeed_ProjectScopedEndpoint()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "In A");
        await SeedNotificationAsync("proj-b", "agent-2", "In B");

        var response = await _client.GetAsync("/api/projects/proj-a/user-notifications");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);
        Assert.Equal("In A", notifications![0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNotificationFeed_FilterByTask()
    {
        var taskId = await SeedTaskAsync("proj-a");
        await SeedNotificationAsync("proj-a", "agent-1", "With task", taskId: taskId);
        await SeedNotificationAsync("proj-a", "agent-1", "Without task");

        var response = await _client.GetAsync($"/api/user-notifications?taskId={taskId}");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);
        Assert.Equal("With task", notifications![0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNotificationFeed_FilterBySender()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "From 1");
        await SeedNotificationAsync("proj-a", "agent-2", "From 2");

        var response = await _client.GetAsync("/api/user-notifications?sender=agent-1");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);
        Assert.Equal("From 1", notifications![0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNotificationFeed_FilterByMetadataType()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "Work complete", metadataType: "agent_work_complete");
        await SeedNotificationAsync("proj-a", "agent-1", "Other notification");

        var response = await _client.GetAsync("/api/user-notifications?metadataType=agent_work_complete");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);
        Assert.Equal("Work complete", notifications![0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNotificationFeed_FilterByUrgency()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "High urgency", urgency: "high");
        await SeedNotificationAsync("proj-a", "agent-1", "Normal urgency", urgency: "normal");

        var response = await _client.GetAsync("/api/user-notifications?urgency=high");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);
        Assert.Equal("High urgency", notifications![0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNotificationFeed_ExcludesNonNotificationMessages()
    {
        // Seed a notification via the tool
        await SeedNotificationAsync("proj-a", "agent-1", "Is a notification");

        // Seed a regular message directly
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            await repo.CreateAsync(new Message
            {
                ProjectId = "proj-a",
                Sender = "agent-1",
                Content = "Not a notification",
                Intent = MessageIntent.General
            });
        }

        var response = await _client.GetAsync("/api/user-notifications");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);
        Assert.Equal("Is a notification", notifications![0].GetProperty("content").GetString());
    }

    // ---- Mark-read tests ----

    [Fact]
    public async Task MarkNotificationsRead_MarksReadForAgent()
    {
        var n1 = await SeedNotificationAsync("proj-a", "agent-1", "To read");

        // Verify unread initially
        var unreadResp = await _client.GetAsync("/api/user-notifications?isRead=false&readFor=patch");
        unreadResp.EnsureSuccessStatusCode();
        var unread = await unreadResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(unread!);

        // Mark read
        var markResp = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch",
            notification_ids = new[] { n1.Id }
        });
        markResp.EnsureSuccessStatusCode();

        var markResult = await markResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, markResult.GetProperty("marked").GetInt32());

        // Verify now read
        var readResp = await _client.GetAsync("/api/user-notifications?isRead=true&readFor=patch");
        readResp.EnsureSuccessStatusCode();
        var read = await readResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(read!);

        // Verify unread is empty
        var unreadResp2 = await _client.GetAsync("/api/user-notifications?isRead=false&readFor=patch");
        unreadResp2.EnsureSuccessStatusCode();
        var unread2 = await unreadResp2.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Empty(unread2!);
    }

    [Fact]
    public async Task MarkNotificationsRead_OnlyAffectsNotificationMessages()
    {
        // Create a regular message
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            await repo.CreateAsync(new Message
            {
                ProjectId = "proj-a",
                Sender = "agent-1",
                Content = "Regular message",
                Intent = MessageIntent.General
            });
        }

        // Try to mark it as a notification read — should not mark it
        var markResp = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch",
            notification_ids = new[] { 1 }
        });
        markResp.EnsureSuccessStatusCode();

        var markResult = await markResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, markResult.GetProperty("marked").GetInt32());
    }

    // ---- Seed/readback agent_work_complete test ----

    [Fact]
    public async Task AgentWorkCompleteNotification_SeedAndReadback()
    {
        var taskId = await SeedTaskAsync("proj-a");

        // Seed an agent_work_complete notification
        var notification = await SeedNotificationAsync(
            "proj-a", "den-mcp-runner",
            "Runner completed assigned queue for den-core",
            taskId: taskId,
            urgency: "normal",
            metadataType: "agent_work_complete");

        Assert.Equal(MessageIntent.Notification, notification.Intent);

        // Read it back through the feed API filtered by type
        var response = await _client.GetAsync("/api/user-notifications?metadataType=agent_work_complete&projectId=proj-a");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(notifications);
        Assert.Single(notifications!);

        var item = notifications![0];
        Assert.Equal("den-mcp-runner", item.GetProperty("sender").GetString());
        Assert.Equal("Runner completed assigned queue for den-core", item.GetProperty("content").GetString());
        Assert.Equal("proj-a", item.GetProperty("project_id").GetString());
        Assert.Equal(taskId, item.GetProperty("task_id").GetInt32());
        Assert.Equal("normal", item.GetProperty("urgency").GetString());
        Assert.False(item.GetProperty("is_read").GetBoolean());

        // Verify metadata contains the type
        var metadata = item.GetProperty("metadata");
        Assert.Equal("agent_work_complete", metadata.GetProperty("type").GetString());
    }

    // ---- Pagination test ----

    [Fact]
    public async Task GetNotificationFeed_Pagination()
    {
        for (int i = 0; i < 5; i++)
            await SeedNotificationAsync("proj-a", "agent-1", $"Notification {i}");

        // First page
        var page1 = await _client.GetAsync("/api/user-notifications?limit=2&offset=0");
        page1.EnsureSuccessStatusCode();
        var items1 = await page1.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(2, items1!.Length);

        // Second page
        var page2 = await _client.GetAsync("/api/user-notifications?limit=2&offset=2");
        page2.EnsureSuccessStatusCode();
        var items2 = await page2.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(2, items2!.Length);

        // Third page
        var page3 = await _client.GetAsync("/api/user-notifications?limit=2&offset=4");
        page3.EnsureSuccessStatusCode();
        var items3 = await page3.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(items3!);

        // Verify ordering: page1[0] is newest, page3[0] is oldest
        Assert.Equal("Notification 4", items1![0].GetProperty("content").GetString());
        Assert.Equal("Notification 0", items3![0].GetProperty("content").GetString());
    }

    // ---- isRead filter requires readFor ----

    [Fact]
    public async Task GetNotificationFeed_IsReadWithoutReadFor_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/user-notifications?isRead=true");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Dual-mode mark-read validation tests (#868, #869, #870) ----

    [Fact]
    public async Task MarkRead_NullIdsNoMarkAll_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Must provide either", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MarkRead_EmptyIdsNoMarkAll_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch",
            notification_ids = Array.Empty<int>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Must provide either", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MarkRead_BothModes_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch",
            notification_ids = new[] { 1 },
            mark_all = true,
            scope = new { project_id = "proj-a" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Cannot specify both", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MarkAll_NoProjectId_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch",
            mark_all = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("scope.project_id is required", body.GetProperty("error").GetString());
    }

    // ---- Scoped mark-all tests (#867) ----

    [Fact]
    public async Task MarkAll_ProjectScope_MarksAllProjectNotifications()
    {
        // Seed 3 in proj-a, 1 in proj-b
        var a1 = await SeedNotificationAsync("proj-a", "agent-1", "A-1");
        var a2 = await SeedNotificationAsync("proj-a", "agent-1", "A-2");
        var a3 = await SeedNotificationAsync("proj-a", "agent-1", "A-3");
        await SeedNotificationAsync("proj-b", "agent-2", "B-1");

        var markResp = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch",
            mark_all = true,
            scope = new { project_id = "proj-a" }
        });
        markResp.EnsureSuccessStatusCode();
        var markResult = await markResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, markResult.GetProperty("marked").GetInt32());

        // Verify proj-a notifications are read for patch
        var readResp = await _client.GetAsync("/api/user-notifications?projectId=proj-a&isRead=true&readFor=patch");
        readResp.EnsureSuccessStatusCode();
        var read = await readResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(3, read!.Length);

        // Verify proj-b notification is still unread for patch
        var unreadResp = await _client.GetAsync("/api/user-notifications?projectId=proj-b&isRead=false&readFor=patch");
        unreadResp.EnsureSuccessStatusCode();
        var unread = await unreadResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(unread!);
    }

    [Fact]
    public async Task MarkAll_TaskScope_MarksOnlyTaskNotifications()
    {
        var taskId = await SeedTaskAsync("proj-a");

        await SeedNotificationAsync("proj-a", "agent-1", "On task", taskId: taskId);
        await SeedNotificationAsync("proj-a", "agent-1", "On task 2", taskId: taskId);
        await SeedNotificationAsync("proj-a", "agent-1", "No task");

        var markResp = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "patch",
            mark_all = true,
            scope = new { project_id = "proj-a", task_id = taskId }
        });
        markResp.EnsureSuccessStatusCode();
        var markResult = await markResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, markResult.GetProperty("marked").GetInt32());

        // Verify task-scoped notifications are read
        var readResp = await _client.GetAsync($"/api/user-notifications?projectId=proj-a&taskId={taskId}&isRead=true&readFor=patch");
        readResp.EnsureSuccessStatusCode();
        var read = await readResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(2, read!.Length);

        // Verify non-task notification is still unread
        var unreadResp = await _client.GetAsync("/api/user-notifications?projectId=proj-a&isRead=false&readFor=patch");
        unreadResp.EnsureSuccessStatusCode();
        var unread = await unreadResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(unread!);
        Assert.Equal("No task", unread![0].GetProperty("content").GetString());
    }

    // ---- Cross-agent identity isolation (#869) ----

    [Fact]
    public async Task MarkRead_CrossAgentIsolation()
    {
        var n1 = await SeedNotificationAsync("proj-a", "agent-1", "Isolation test");

        // Mark read for agent-x
        var markResp = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "agent-x",
            notification_ids = new[] { n1.Id }
        });
        markResp.EnsureSuccessStatusCode();

        // Verify read for agent-x
        var readX = await _client.GetAsync("/api/user-notifications?isRead=true&readFor=agent-x");
        readX.EnsureSuccessStatusCode();
        var readXItems = await readX.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(readXItems!);

        // Verify still unread for agent-y
        var unreadY = await _client.GetAsync("/api/user-notifications?isRead=false&readFor=agent-y");
        unreadY.EnsureSuccessStatusCode();
        var unreadYItems = await unreadY.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(unreadYItems!);
    }

    [Fact]
    public async Task Feed_WithoutReadFor_ReturnsIsReadFalse()
    {
        await SeedNotificationAsync("proj-a", "agent-1", "Unread by default");

        var response = await _client.GetAsync("/api/user-notifications");
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(items!);
        Assert.False(items![0].GetProperty("is_read").GetBoolean());
    }

    [Fact]
    public async Task Feed_WithReadFor_ReflectsAgentState()
    {
        var n1 = await SeedNotificationAsync("proj-a", "agent-1", "State test");

        // Mark read for agent-1 (the reader, not sender)
        var markResp = await _client.PostAsJsonAsync("/api/user-notifications/mark-read", new
        {
            agent = "reader-agent",
            notification_ids = new[] { n1.Id }
        });
        markResp.EnsureSuccessStatusCode();

        // Verify read for reader-agent
        var readResp = await _client.GetAsync("/api/user-notifications?readFor=reader-agent");
        readResp.EnsureSuccessStatusCode();
        var readItems = await readResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(readItems!);
        Assert.True(readItems![0].GetProperty("is_read").GetBoolean());

        // Verify unread for other-agent
        var unreadResp = await _client.GetAsync("/api/user-notifications?readFor=other-agent");
        unreadResp.EnsureSuccessStatusCode();
        var unreadItems = await unreadResp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(unreadItems!);
        Assert.False(unreadItems![0].GetProperty("is_read").GetBoolean());
    }

    // ---- Feed item shape test ----

    [Fact]
    public async Task GetNotificationFeed_ItemContainsExpectedFields()
    {
        var taskId = await SeedTaskAsync("proj-a");
        await SeedNotificationAsync("proj-a", "agent-1", "Test notification",
            taskId: taskId, urgency: "high", metadataType: "agent_work_complete");

        var response = await _client.GetAsync("/api/user-notifications");
        response.EnsureSuccessStatusCode();

        var notifications = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Single(notifications!);

        var item = notifications![0];
        Assert.True(item.TryGetProperty("id", out _));
        Assert.True(item.TryGetProperty("project_id", out _));
        Assert.True(item.TryGetProperty("task_id", out _));
        Assert.True(item.TryGetProperty("sender", out _));
        Assert.True(item.TryGetProperty("content", out _));
        Assert.True(item.TryGetProperty("metadata", out _));
        Assert.True(item.TryGetProperty("urgency", out _));
        Assert.True(item.TryGetProperty("is_read", out _));
        Assert.True(item.TryGetProperty("created_at", out _));
    }
}
