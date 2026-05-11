using System.Net;
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

public class AgentWorkspaceApiTests : IAsyncLifetime
{
    private const string ProjectId = "workspace-api-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private WorkspaceAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ProjectTask _task = null!;
    private ProjectTask _otherTask = null!;

    public async Task InitializeAsync()
    {
        _factory = new WorkspaceAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Workspace API Test" });
        await projects.CreateAsync(new Project { Id = "other-workspace-api-test", Name = "Other Workspace API Test" });

        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        _task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Workspace task" });
        _otherTask = await tasks.CreateAsync(new ProjectTask { ProjectId = "other-workspace-api-test", Title = "Other workspace task" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestAgentWorkspaces_CreateListGetAndUpsert()
    {
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/agent-workspaces", new
        {
            id = "ws-api",
            task_id = _task.Id,
            branch = "task/809-agent-workspace-backend",
            worktree_path = "/home/patch/dev/den-mcp",
            base_branch = "main",
            base_commit = "base-sha",
            head_commit = "head-sha",
            state = "active",
            dev_server_url = "http://localhost:5199",
            preview_url = "http://localhost:5199/web",
            cleanup_policy = "keep",
            changed_file_summary = new
            {
                files = new[] { new { path = "src/Foo.cs", status = "modified" } },
                counts = new { modified = 1 }
            }
        }, JsonOpts);
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<AgentWorkspace>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal("ws-api", created!.Id);
        Assert.Equal(AgentWorkspaceState.Active, created.State);
        Assert.Equal("src/Foo.cs", created.ChangedFileSummary!.Value.GetProperty("files")[0].GetProperty("path").GetString());

        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/agent-workspaces?taskId={_task.Id}&state=active");
        listResponse.EnsureSuccessStatusCode();
        var listed = await listResponse.Content.ReadFromJsonAsync<List<AgentWorkspace>>(JsonOpts);
        var only = Assert.Single(listed!);
        Assert.Equal("ws-api", only.Id);

        var getResponse = await _client.GetAsync($"/api/projects/{ProjectId}/agent-workspaces/ws-api");
        getResponse.EnsureSuccessStatusCode();
        var loaded = await getResponse.Content.ReadFromJsonAsync<AgentWorkspace>(JsonOpts);
        Assert.Equal("head-sha", loaded!.HeadCommit);

        var updateResponse = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/agent-workspaces/ws-api", new
        {
            task_id = _task.Id,
            branch = "task/809-agent-workspace-backend",
            worktree_path = "/home/patch/dev/den-mcp",
            base_branch = "main",
            base_commit = "base-sha",
            head_commit = "new-head-sha",
            state = "review",
            cleanup_policy = "delete_worktree"
        }, JsonOpts);
        updateResponse.EnsureSuccessStatusCode();

        var updated = await updateResponse.Content.ReadFromJsonAsync<AgentWorkspace>(JsonOpts);
        Assert.Equal("ws-api", updated!.Id);
        Assert.Equal("new-head-sha", updated.HeadCommit);
        Assert.Equal(AgentWorkspaceState.Review, updated.State);
        Assert.Equal(AgentWorkspaceCleanupPolicy.DeleteWorktree, updated.CleanupPolicy);
    }

    [Fact]
    public async Task RestAgentWorkspaces_RejectsMissingRequiredFieldsAndWrongProjectGet()
    {
        var badResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/agent-workspaces", new
        {
            task_id = _task.Id,
            branch = "task/missing-worktree",
            base_branch = "main"
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/projects/other-project/agent-workspaces/ws-api");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task RestAgentWorkspaces_RejectsTaskFromDifferentProject()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/agent-workspaces", new
        {
            id = "ws-cross-project",
            task_id = _otherTask.Id,
            branch = "task/cross-project",
            worktree_path = "/tmp/cross-project",
            base_branch = "main"
        }, JsonOpts);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("belongs to project", body);
    }

    private sealed class WorkspaceAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-workspace-api-{Guid.NewGuid()}.db");

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
