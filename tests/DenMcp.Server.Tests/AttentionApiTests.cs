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

namespace DenMcp.Server.Tests;

public class AttentionApiTests : IAsyncLifetime
{
    private const string ProjectId = "attention-api-test";
    private const string OtherProjectId = "attention-api-other";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private AttentionAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new AttentionAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Attention API Test" });
        await projects.CreateAsync(new Project { Id = OtherProjectId, Name = "Other Attention API Test" });
        await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = "Project blocked task",
            Status = DenMcp.Core.Models.TaskStatus.Blocked
        });
        await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = OtherProjectId,
            Title = "Other blocked task",
            Status = DenMcp.Core.Models.TaskStatus.Blocked
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestAttention_ProjectAndGlobalRoutesFilterDerivedItems()
    {
        var projectResponse = await _client.GetAsync($"/api/projects/{ProjectId}/attention?kind=blocked_task&severity=warning");
        projectResponse.EnsureSuccessStatusCode();
        var projectItems = await projectResponse.Content.ReadFromJsonAsync<List<AttentionItem>>(JsonOpts);
        var projectItem = Assert.Single(projectItems!);
        Assert.Equal(ProjectId, projectItem.ProjectId);
        Assert.Equal("blocked_task", projectItem.Kind);
        Assert.Equal("warning", projectItem.Severity);
        Assert.Equal("Resolve dependencies, update the task with the blocker, or unblock it if the blocker is gone.", projectItem.SuggestedAction);

        var globalResponse = await _client.GetAsync($"/api/attention?projectId={OtherProjectId}&kind=blocked_task");
        globalResponse.EnsureSuccessStatusCode();
        var globalItems = await globalResponse.Content.ReadFromJsonAsync<List<AttentionItem>>(JsonOpts);
        var globalItem = Assert.Single(globalItems!);
        Assert.Equal(OtherProjectId, globalItem.ProjectId);
        Assert.Equal("blocked_task", globalItem.Kind);
    }

    private sealed class AttentionAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-attention-api-{Guid.NewGuid()}.db");

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
                var initializer = new DatabaseInitializer(_dbPath,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
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
                => Task.FromResult(string.Empty);
        }
    }
}
