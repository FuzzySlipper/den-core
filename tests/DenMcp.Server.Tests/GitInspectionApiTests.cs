using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Server.Tests;

public sealed class GitInspectionApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly string _projectId = $"git-api-test-{Guid.NewGuid():N}";
    private readonly string _otherProjectId = $"git-api-other-{Guid.NewGuid():N}";
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"den-git-api-repo-{Guid.NewGuid():N}");
    private readonly string _nonGitRoot = Path.Combine(Path.GetTempPath(), $"den-git-api-nongit-{Guid.NewGuid():N}");
    private readonly string _missingWorkspaceRoot = Path.Combine(Path.GetTempPath(), $"den-git-api-missing-{Guid.NewGuid():N}");
    private readonly string _dirtyWorkspaceId = $"ws-dirty-{Guid.NewGuid():N}";
    private readonly string _missingWorkspaceId = $"ws-missing-{Guid.NewGuid():N}";
    private readonly string _nonGitWorkspaceId = $"ws-nongit-{Guid.NewGuid():N}";
    private GitAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        CreateGitRepo(_repoRoot);
        _factory = new GitAppFactory();
        _client = _factory.CreateClient();

        Directory.CreateDirectory(_nonGitRoot);

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project
        {
            Id = _projectId,
            Name = "Git API Test",
            RootPath = _repoRoot
        });
        await projects.CreateAsync(new Project
        {
            Id = _otherProjectId,
            Name = "Other Git API Test"
        });

        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        _task = await tasks.CreateAsync(new ProjectTask { ProjectId = _projectId, Title = "Workspace git task" });

        var workspaces = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceRepository>();
        await workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = _dirtyWorkspaceId,
            ProjectId = _projectId,
            TaskId = _task.Id,
            Branch = CurrentBranch(_repoRoot) + "-stale",
            WorktreePath = _repoRoot,
            BaseBranch = "main",
            BaseCommit = "base-sha",
            HeadCommit = "deadbeef"
        });
        await workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = _missingWorkspaceId,
            ProjectId = _projectId,
            TaskId = _task.Id,
            Branch = "task/missing",
            WorktreePath = _missingWorkspaceRoot,
            BaseBranch = "main"
        });
        await workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = _nonGitWorkspaceId,
            ProjectId = _projectId,
            TaskId = _task.Id,
            Branch = "task/nongit",
            WorktreePath = _nonGitRoot,
            BaseBranch = "main"
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
        if (Directory.Exists(_nonGitRoot))
            Directory.Delete(_nonGitRoot, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Status_ReturnsStructuredBranchAndDirtyFileState()
    {
        await File.AppendAllTextAsync(Path.Combine(_repoRoot, "tracked.txt"), "modified\n");
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, "scratch.md"), "scratch\n");

        var response = await _client.GetAsync($"/api/projects/{_projectId}/git/status");
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<GitStatusResponse>(JsonOpts);

        Assert.NotNull(status);
        Assert.True(status!.IsGitRepository);
        Assert.NotNull(status.HeadSha);
        Assert.True(status.DirtyCounts.Total >= 2);
        Assert.Contains(status.Files, file => file.Path == "tracked.txt" && file.Category == "modified");
        Assert.Contains(status.Files, file => file.Path == "scratch.md" && file.IsUntracked);
    }

    [Fact]
    public async Task Diff_ReturnsBoundedDiffAndRejectsEscapingPaths()
    {
        await File.AppendAllTextAsync(Path.Combine(_repoRoot, "tracked.txt"), string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line {i}")));

        var diffResponse = await _client.GetAsync($"/api/projects/{_projectId}/git/diff?path=tracked.txt&maxBytes=1024");
        diffResponse.EnsureSuccessStatusCode();
        var diff = await diffResponse.Content.ReadFromJsonAsync<GitDiffResponse>(JsonOpts);
        Assert.NotNull(diff);
        Assert.True(diff!.Diff.Contains("tracked.txt", StringComparison.Ordinal));
        Assert.True(diff.MaxBytes == 1024);

        var badResponse = await _client.GetAsync($"/api/projects/{_projectId}/git/diff?path=../outside.txt");
        badResponse.EnsureSuccessStatusCode();
        var bad = await badResponse.Content.ReadFromJsonAsync<GitDiffResponse>(JsonOpts);
        Assert.NotNull(bad);
        Assert.Contains(bad!.Errors, error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_ReturnsNotFoundForMissingProject()
    {
        var response = await _client.GetAsync("/api/projects/missing-project/git/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WorkspaceGitStatusFilesAndDiff_ReturnWorkspaceMetadataDirtyStateAndAlignmentWarnings()
    {
        await File.AppendAllTextAsync(Path.Combine(_repoRoot, "tracked.txt"), "workspace edit\n");
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, "workspace-note.md"), "note\n");

        var statusResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-workspaces/{_dirtyWorkspaceId}/git/status");
        statusResponse.EnsureSuccessStatusCode();
        var status = await statusResponse.Content.ReadFromJsonAsync<GitStatusResponse>(JsonOpts);
        Assert.NotNull(status);
        Assert.Equal(_dirtyWorkspaceId, status!.WorkspaceId);
        Assert.Equal(_task.Id, status.TaskId);
        Assert.Equal("base-sha", status.WorkspaceBaseCommit);
        Assert.Equal("deadbeef", status.WorkspaceHeadCommit);
        Assert.True(status.IsGitRepository);
        Assert.Contains(status.Files, file => file.Path == "tracked.txt" && file.Category == "modified");
        Assert.Contains(status.Files, file => file.Path == "workspace-note.md" && file.IsUntracked);
        Assert.Contains(status.Warnings, warning => warning.Contains("branch metadata", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(status.Warnings, warning => warning.Contains("head metadata", StringComparison.OrdinalIgnoreCase));

        var filesResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-workspaces/{_dirtyWorkspaceId}/git/files");
        filesResponse.EnsureSuccessStatusCode();
        var files = await filesResponse.Content.ReadFromJsonAsync<GitFilesResponse>(JsonOpts);
        Assert.NotNull(files);
        Assert.Equal(_dirtyWorkspaceId, files!.WorkspaceId);
        Assert.Contains(files.Files, file => file.Path == "workspace-note.md" && file.IsUntracked);
        Assert.Contains(files.Warnings, warning => warning.Contains("head metadata", StringComparison.OrdinalIgnoreCase));

        var diffResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-workspaces/{_dirtyWorkspaceId}/git/diff?path=tracked.txt&maxBytes=4096");
        diffResponse.EnsureSuccessStatusCode();
        var diff = await diffResponse.Content.ReadFromJsonAsync<GitDiffResponse>(JsonOpts);
        Assert.NotNull(diff);
        Assert.Equal(_dirtyWorkspaceId, diff!.WorkspaceId);
        Assert.Equal(_task.Id, diff.TaskId);
        Assert.Contains("workspace edit", diff.Diff);
        Assert.Contains(diff.Warnings, warning => warning.Contains("head metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WorkspaceGitStatus_RejectsWrongProjectAndReportsMissingWorkspacePath()
    {
        var wrongProject = await _client.GetAsync($"/api/projects/{_otherProjectId}/agent-workspaces/{_dirtyWorkspaceId}/git/status");
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);

        var missingPathResponse = await _client.GetAsync($"/api/projects/{_projectId}/agent-workspaces/{_missingWorkspaceId}/git/status");
        missingPathResponse.EnsureSuccessStatusCode();
        var missingPath = await missingPathResponse.Content.ReadFromJsonAsync<GitStatusResponse>(JsonOpts);
        Assert.NotNull(missingPath);
        Assert.Equal(_missingWorkspaceId, missingPath!.WorkspaceId);
        Assert.Contains(missingPath.Errors, error => error.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WorkspaceGitStatus_ReturnsStructuredErrorForNonGitWorkspace()
    {
        var response = await _client.GetAsync($"/api/projects/{_projectId}/agent-workspaces/{_nonGitWorkspaceId}/git/status");
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<GitStatusResponse>(JsonOpts);

        Assert.NotNull(status);
        Assert.Equal(_nonGitWorkspaceId, status!.WorkspaceId);
        Assert.Equal(_task.Id, status.TaskId);
        Assert.False(status.IsGitRepository);
        Assert.Contains(status.Errors, error => error.Contains("git status failed", StringComparison.OrdinalIgnoreCase));
    }

    private static void CreateGitRepo(string root)
    {
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "initial\n");
        RunGit(root, "add", "tracked.txt");
        RunGit(root, "commit", "-m", "initial");
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var (_, stderr, exitCode) = RunGitCapture(cwd, args);
        if (exitCode != 0)
            throw new InvalidOperationException(stderr);
    }

    private static string CurrentBranch(string cwd) => RunGitCapture(cwd, ["rev-parse", "--abbrev-ref", "HEAD"]).Stdout.Trim();

    private static (string Stdout, string Stderr, int ExitCode) RunGitCapture(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit(10_000);
        return (process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd(), process.ExitCode);
    }

    private sealed class GitAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-git-api-{Guid.NewGuid()}.db");

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
