using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Llm;
using DenCore.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = DenCore.Models.TaskStatus;

namespace DenCore.Service.Tests;

public sealed class ChannelsContractApiTests : IAsyncLifetime
{
    private const string ProjectId = "channels-contract-proj";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private ChannelsContractAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new ChannelsContractAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Channels Contract Project" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SourceSummary_ForTask_ReturnsCompactDisplayShapeAndDeepLink()
    {
        var task = await CreateTaskAsync("Wire Channels contract", TaskStatus.InProgress, priority: 2, assignedTo: "sysadmin");

        var response = await _client.GetAsync($"/api/source-summaries/task/{task.Id}?projectId={ProjectId}");

        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;
        Assert.Equal("task", root.GetProperty("source_kind").GetString());
        Assert.Equal(task.Id.ToString(), root.GetProperty("source_id").GetString());
        Assert.Equal(ProjectId, root.GetProperty("source_project_id").GetString());
        Assert.Equal("Task #" + task.Id + ": Wire Channels contract", root.GetProperty("title").GetString());
        Assert.Contains("in_progress", root.GetProperty("summary").GetString());
        Assert.Equal($"den://project/{ProjectId}/task/{task.Id}", root.GetProperty("deep_link").GetString());
        Assert.Equal("sysadmin", root.GetProperty("actor").GetString());
        Assert.Equal("normal", root.GetProperty("severity").GetString());
        Assert.Equal(2, root.GetProperty("metadata").GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task SourceSummary_ForTaskMessage_ReturnsFirstLineWithoutCopyingFullBody()
    {
        var task = await CreateTaskAsync("Message target");
        var message = await CreateMessageAsync(task.Id, "runner", "Implementation completed.\n\nLong detail should not be the title.");

        var response = await _client.GetAsync($"/api/source-summaries/task_message/{message.Id}?projectId={ProjectId}");

        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;
        Assert.Equal("task_message", root.GetProperty("source_kind").GetString());
        Assert.Equal(message.Id.ToString(), root.GetProperty("source_id").GetString());
        Assert.Equal(ProjectId, root.GetProperty("source_project_id").GetString());
        Assert.Equal($"Message #{message.Id} from runner", root.GetProperty("title").GetString());
        Assert.Equal("Implementation completed.", root.GetProperty("summary").GetString());
        Assert.Equal($"den://project/{ProjectId}/message/{message.Id}", root.GetProperty("deep_link").GetString());
        Assert.Equal(task.Id, root.GetProperty("metadata").GetProperty("task_id").GetInt32());
    }

    [Fact]
    public async Task SourceSummary_RejectsCrossProjectLookup()
    {
        var task = await CreateTaskAsync("Private task");

        var response = await _client.GetAsync($"/api/source-summaries/task/{task.Id}?projectId=other-project");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EventOutbox_UsesMonotonicAgentStreamCursorAndSkipsDebugEntries()
    {
        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var first = await stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "note",
            ProjectId = ProjectId,
            Sender = "sysadmin",
            DeliveryMode = AgentStreamDeliveryMode.Notify,
            Body = "Channels-visible event",
            DedupKey = "channels-contract-visible"
        });
        await stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_work_chunk",
            ProjectId = ProjectId,
            Sender = "worker",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "debug churn"
        });
        var second = await stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "question",
            ProjectId = ProjectId,
            Sender = "runner",
            DeliveryMode = AgentStreamDeliveryMode.Wake,
            Body = "Needs input",
            DedupKey = "channels-contract-input"
        });

        var response = await _client.GetAsync($"/api/events/outbox?after={first.Id}&projectId={ProjectId}&limit=10");

        response.EnsureSuccessStatusCode();
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(second.Id.ToString("D12"), items[0].GetProperty("cursor").GetString());
        Assert.Equal("agent_stream.question", items[0].GetProperty("event_type").GetString());
        Assert.Equal("agent_stream_entry", items[0].GetProperty("source_kind").GetString());
        Assert.Equal(second.Id.ToString(), items[0].GetProperty("source_id").GetString());
        Assert.Equal($"den://project/{ProjectId}/agent-stream/{second.Id}", items[0].GetProperty("deep_link").GetString());
        Assert.Equal("channels-contract-input", items[0].GetProperty("dedupe_key").GetString());
        Assert.Equal((second.Id + 1).ToString("D12"), root.GetProperty("next_cursor").GetString());
    }

    private async Task<ProjectTask> CreateTaskAsync(string title, TaskStatus status = TaskStatus.Planned, int priority = 3,
        string? assignedTo = null)
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = title,
            Status = status,
            Priority = priority,
            AssignedTo = assignedTo
        });
        return task;
    }

    private async Task<Message> CreateMessageAsync(int? taskId, string sender, string content)
    {
        using var scope = _factory.Services.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        return await messages.CreateAsync(new Message
        {
            ProjectId = ProjectId,
            TaskId = taskId,
            Sender = sender,
            Content = content
        });
    }

    private sealed class ChannelsContractAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-core-channels-contract-{Guid.NewGuid()}.db");

        public string DatabasePath => _dbPath;

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

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient, FakeLlmClient>();
                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(DatabaseInitializer.GetConnectionString(_dbPath)));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            DatabaseInitializer.DisposeLeaseAsync(_dbPath).AsTask().GetAwaiter().GetResult();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
            => Task.FromResult("{}");
    }
}
