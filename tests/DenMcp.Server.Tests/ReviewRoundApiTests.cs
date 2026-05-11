using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public class ReviewRoundApiTests : IAsyncLifetime
{
    private ReviewRoundAppFactory _factory = null!;
    private HttpClient _client = null!;
    private const string ProjectId = "proj";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task InitializeAsync()
    {
        _factory = new ReviewRoundAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<ProjectTask> CreateTaskAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Review target" });
    }

    [Fact]
    public async Task CreateReviewRound_ReturnsCreatedRound()
    {
        var task = await CreateTaskAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds", new
        {
            requested_by = "claude-code",
            branch = "task/594-review-rounds-sha-metadata",
            base_branch = "main",
            base_commit = "abc123",
            head_commit = "def456",
            preferred_diff_base_ref = "task/593-parent",
            preferred_diff_base_commit = "bbb222",
            preferred_diff_head_ref = "task/594-review-rounds-sha-metadata",
            preferred_diff_head_commit = "def456",
            alternate_diff_base_ref = "main",
            alternate_diff_base_commit = "abc123",
            alternate_diff_head_ref = "task/594-review-rounds-sha-metadata",
            alternate_diff_head_commit = "def456",
            delta_base_commit = "ccc333",
            inherited_commit_count = 4,
            task_local_commit_count = 1,
            tests_run = new[] { "dotnet test den-mcp.slnx --filter ReviewRoundApiTests" },
            notes = "Ready for review"
        });
        response.EnsureSuccessStatusCode();

        var round = await response.Content.ReadFromJsonAsync<ReviewRound>(JsonOpts);
        Assert.Equal(1, round!.RoundNumber);
        Assert.Equal("def456", round.HeadCommit);
        Assert.Single(round.TestsRun!);
        Assert.Equal("task/593-parent", round.PreferredDiff.BaseRef);
        Assert.NotNull(round.AlternateDiff);
        Assert.Equal("main", round.AlternateDiff!.BaseRef);
        Assert.Equal("ccc333", round.DeltaDiff!.BaseCommit);
        Assert.True(round.IsStackedBranchReview);
        Assert.Equal(4, round.BranchComposition.InheritedCommitCount);
        Assert.Equal(1, round.BranchComposition.TaskLocalCommitCount);
    }

    [Fact]
    public async Task CreateReviewRound_SameHeadAsLastReview_Returns400()
    {
        var task = await CreateTaskAsync();

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds", new
        {
            requested_by = "claude-code",
            branch = "task/594-review-rounds-sha-metadata",
            base_branch = "main",
            base_commit = "abc123",
            head_commit = "def456"
        });

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds", new
        {
            requested_by = "claude-code",
            branch = "task/594-review-rounds-sha-metadata",
            base_branch = "main",
            base_commit = "abc123",
            head_commit = "def456"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetVerdict_UpdatesReviewRound()
    {
        var task = await CreateTaskAsync();
        var create = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds", new
        {
            requested_by = "claude-code",
            branch = "task/594-review-rounds-sha-metadata",
            base_branch = "main",
            base_commit = "abc123",
            head_commit = "def456"
        });
        var round = await create.Content.ReadFromJsonAsync<ReviewRound>(JsonOpts);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds/{round!.Id}/verdict",
            new { verdict = "changes_requested", decided_by = "codex", notes = "Needs another pass" });
        response.EnsureSuccessStatusCode();

        var updated = await response.Content.ReadFromJsonAsync<ReviewRound>(JsonOpts);
        Assert.Equal(ReviewVerdict.ChangesRequested, updated!.Verdict);
        Assert.Equal("codex", updated.VerdictBy);
    }

    [Fact]
    public async Task GetTaskDetail_IncludesReviewRounds()
    {
        var task = await CreateTaskAsync();
        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds", new
        {
            requested_by = "claude-code",
            branch = "task/594-review-rounds-sha-metadata",
            base_branch = "main",
            base_commit = "abc123",
            head_commit = "def456",
            preferred_diff_base_ref = "task/593-parent",
            alternate_diff_base_ref = "main",
            inherited_commit_count = 2,
            task_local_commit_count = 1
        });

        var response = await _client.GetAsync($"/api/projects/{ProjectId}/tasks/{task.Id}");
        response.EnsureSuccessStatusCode();

        var detail = await response.Content.ReadFromJsonAsync<TaskDetail>(JsonOpts);
        Assert.Single(detail!.ReviewRounds);
        Assert.Equal("def456", detail.ReviewRounds[0].HeadCommit);
        Assert.True(detail.ReviewRounds[0].IsStackedBranchReview);
        Assert.Equal(2, detail.ReviewRounds[0].BranchComposition.InheritedCommitCount);
    }

    private sealed class ReviewRoundAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-reviewround-{Guid.NewGuid()}.db");

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
                    ["llm-model"] = "fake"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient, FakeLlmClient>();
                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory($"Data Source={_dbPath}"));
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
