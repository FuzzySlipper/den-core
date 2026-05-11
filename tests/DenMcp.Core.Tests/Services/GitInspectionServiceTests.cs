using System.Diagnostics;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class GitInspectionServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"den-git-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void ParsePorcelainV2_ReturnsBranchMetadataAndFileStates()
    {
        const string output = """
            # branch.oid abcdef1234567890
            # branch.head feature/test
            # branch.upstream origin/main
            # branch.ab +2 -1
            1 M. N... 100644 100644 100644 old new file.txt
            1 .D N... 100644 100644 100644 old new deleted.txt
            2 R. N... 100644 100644 100644 old new R100 renamed.txt	old.txt
            ? scratch.md
            """;

        var status = GitStatusParser.ParsePorcelainV2("proj", "/repo", output);

        Assert.True(status.IsGitRepository);
        Assert.Equal("feature/test", status.Branch);
        Assert.Equal("abcdef1234567890", status.HeadSha);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.Equal(4, status.DirtyCounts.Total);
        Assert.Equal(2, status.DirtyCounts.Staged);
        Assert.Equal(1, status.DirtyCounts.Unstaged);
        Assert.Equal(1, status.DirtyCounts.Untracked);
        Assert.Contains(status.Files, file => file.Path == "scratch.md" && file.IsUntracked && file.Category == "untracked");
        Assert.Contains(status.Files, file => file.Path == "renamed.txt" && file.OldPath == "old.txt" && file.Category == "renamed");
    }

    [Fact]
    public async Task GetDiffAsync_RejectsPathsThatEscapeRoot()
    {
        var service = new GitInspectionService();
        Directory.CreateDirectory(_tempRoot);

        var response = await service.GetDiffAsync("proj", _tempRoot, "../outside.txt");

        Assert.Contains(response.Errors, error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetStatusAsync_ReportsMissingGitRepoAsStructuredError()
    {
        var service = new GitInspectionService();
        Directory.CreateDirectory(_tempRoot);

        var response = await service.GetStatusAsync("proj", _tempRoot);

        Assert.False(response.IsGitRepository);
        Assert.Contains(response.Errors, error => error.Contains("git status failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetDiffAsync_BoundsLargeDiffOutput()
    {
        var repo = CreateGitRepo();
        var file = Path.Combine(repo, "tracked.txt");
        await File.AppendAllTextAsync(file, string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"line {i}")));

        var service = new GitInspectionService();
        var response = await service.GetDiffAsync("proj", repo, "tracked.txt", maxBytes: 1024);

        Assert.Empty(response.Errors);
        Assert.True(response.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(response.Diff) <= 1024);
    }

    private string CreateGitRepo()
    {
        Directory.CreateDirectory(_tempRoot);
        RunGit(_tempRoot, "init");
        RunGit(_tempRoot, "config", "user.email", "test@example.com");
        RunGit(_tempRoot, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(_tempRoot, "tracked.txt"), "initial\n");
        RunGit(_tempRoot, "add", "tracked.txt");
        RunGit(_tempRoot, "commit", "-m", "initial");
        return _tempRoot;
    }

    private static void RunGit(string cwd, params string[] args)
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }
}
