using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Thread = DenCore.Models.Thread;

namespace DenCore.Service.Tests;

public class TrustedPublisherServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"den-mcp-tests-{Environment.UserName}", "trusted-publisher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishWorkerBranch_RejectsWithoutRuntimeWorkspaceEvidenceAndAudits()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: true);
        var service = BuildService(repos);

        var result = await service.PublishWorkerBranchAsync(new PublishWorkerBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RunId = "run-1",
            RequestedBy = "den-mcp-runner",
            ExpectedBranch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseCommit = fixture.Base,
            AllowedPathPrefixes = "src",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("Worker workspace path is missing or unavailable", StringComparison.Ordinal));
        Assert.Empty(result.ChangedFiles);
        Assert.NotNull(result.AuditMessageId);
        Assert.Contains(repos.Messages.Created, m => m.Metadata?.GetProperty("type").GetString() == "trusted_publisher_audit");
    }

    [Fact]
    public async Task PublishReviewedBranch_ValidatesLooksGoodReviewForTrustedOrchestrator()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: false);
        repos.ReviewRound = new ReviewRound
        {
            Id = 7,
            TaskId = 1285,
            RoundNumber = 1,
            RequestedBy = "den-mcp-runner",
            Branch = "task/1285-trusted-publisher",
            BaseBranch = "main",
            BaseCommit = fixture.Base,
            HeadCommit = fixture.Head,
            Verdict = ReviewVerdict.LooksGood,
            VerdictBy = "reviewer",
        };
        var service = BuildService(repos);

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-mcp-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("validated", result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.ValidationDecisions, d => d.Contains("looks_good", StringComparison.Ordinal));
        Assert.NotNull(result.AuditMessageId);
    }

    [Fact]
    public async Task PublishReviewedBranch_AllowsCurrentHermesRunnerDefaults()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: false);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions { AllowFileProtocolRemote = true });

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("validated", result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.ValidationDecisions, d => d.Contains("allowed trusted orchestrator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReviewedBranch_ResolvesBareProjectRootThroughConfiguredDevRoots()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var bareRootName = Path.GetFileName(fixture.RootPath);
        var repos = BuildRepositories(fixture, includeCompletion: false, projectRootPath: bareRootName);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions
        {
            AllowFileProtocolRemote = true,
            AllowedOrchestrators = ["den-channels-runner"],
            ProjectRootSearchPaths = [_root],
        });

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("validated", result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(fixture.RootPath, result.WorkspacePath);
    }

    [Fact]
    public async Task PublishReviewedBranch_AutoCreatesManagedWorkspaceBeforeFallingBackToDevCheckout()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var bareRootName = Path.GetFileName(fixture.RootPath);
        var managedRoot = Path.Combine(_root, "managed-git");
        var legacyRoot = Path.Combine(_root, "legacy-dev");
        await fixture.CloneRemoteMainAsync(legacyRoot, bareRootName);
        var repos = BuildRepositories(fixture, includeCompletion: false, projectRootPath: bareRootName);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions
        {
            AllowFileProtocolRemote = true,
            AllowedOrchestrators = ["den-channels-runner"],
            ProjectRootSearchPaths = [managedRoot, legacyRoot],
        });

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        var managedWorkspace = Path.Combine(managedRoot, bareRootName);
        Assert.Equal("validated", result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(managedWorkspace, result.WorkspacePath);
        Assert.True(Directory.Exists(Path.Combine(managedWorkspace, ".git")));
        Assert.Contains(result.ValidationDecisions, d => d.Contains("managed publisher workspace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReviewedBranch_ManagedWorkspaceFailsClosedWhenReviewedCommitIsNotImportable()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        await fixture.DeleteRemoteTaskBranchAsync(1285);
        var bareRootName = Path.GetFileName(fixture.RootPath);
        var managedRoot = Path.Combine(_root, "managed-git");
        var legacyRoot = Path.Combine(_root, "legacy-dev");
        await fixture.CloneRemoteMainAsync(legacyRoot, bareRootName);
        var repos = BuildRepositories(fixture, includeCompletion: false, projectRootPath: bareRootName);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions
        {
            AllowFileProtocolRemote = true,
            AllowedOrchestrators = ["den-channels-runner"],
            ProjectRootSearchPaths = [managedRoot, legacyRoot],
        });
        var remoteUrl = new Uri(fixture.RemotePath).AbsoluteUri;

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = remoteUrl,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Equal(Path.Combine(managedRoot, bareRootName), result.WorkspacePath);
        Assert.Contains(result.Diagnostics, d => d.Contains("reviewed branch/object", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReviewedBranch_LegacyNonGitDevRootFailsClosedWithDiagnostic()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var bareRootName = Path.GetFileName(fixture.RootPath);
        var managedRoot = Path.Combine(_root, "managed-git");
        var legacyRoot = Path.Combine(_root, "legacy-dev");
        var legacyCandidate = Path.Combine(legacyRoot, bareRootName);
        Directory.CreateDirectory(legacyCandidate);
        await File.WriteAllTextAsync(Path.Combine(legacyCandidate, "not-a-checkout.txt"), "not git\n");
        var repos = BuildRepositories(fixture, includeCompletion: false, projectRootPath: bareRootName);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions
        {
            AllowFileProtocolRemote = true,
            AllowedOrchestrators = ["den-channels-runner"],
            ProjectRootSearchPaths = [managedRoot, legacyRoot],
        });

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Null(result.WorkspacePath);
        Assert.Contains(result.Diagnostics, d => d.Contains("not a git checkout", StringComparison.Ordinal) && d.Contains(legacyCandidate, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReviewedBranch_ImportsReviewedCommitFromAllowedBundleWhenRemoteBranchIsUnavailable()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var artifactRoot = Path.Combine(_root, "reviewed-artifacts");
        var bundlePath = await fixture.CreateReviewedBundleAsync(artifactRoot, "reviewed.bundle", taskId: 1285);
        await fixture.DeleteRemoteTaskBranchAsync(1285);
        var bareRootName = Path.GetFileName(fixture.RootPath);
        var managedRoot = Path.Combine(_root, "managed-git");
        var repos = BuildRepositories(fixture, includeCompletion: false, projectRootPath: bareRootName);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions
        {
            AllowFileProtocolRemote = true,
            AllowedOrchestrators = ["den-channels-runner"],
            ProjectRootSearchPaths = [managedRoot],
            ReviewedArtifactSearchPaths = [artifactRoot],
        });
        var remoteUrl = new Uri(fixture.RemotePath).AbsoluteUri;

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = remoteUrl,
            ReviewedGitBundlePath = bundlePath,
            ValidateOnly = true,
        });

        Assert.Equal("validated", result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(Path.Combine(managedRoot, bareRootName), result.WorkspacePath);
        Assert.Contains(result.ValidationDecisions, d => d.Contains("reviewed git bundle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReviewedBranch_RejectsReviewedBundleOutsideConfiguredArtifactRoots()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var artifactRoot = Path.Combine(_root, "reviewed-artifacts");
        var outsideRoot = Path.Combine(_root, "outside-artifacts");
        var bundlePath = await fixture.CreateReviewedBundleAsync(outsideRoot, "reviewed.bundle", taskId: 1285);
        await fixture.DeleteRemoteTaskBranchAsync(1285);
        var bareRootName = Path.GetFileName(fixture.RootPath);
        var managedRoot = Path.Combine(_root, "managed-git");
        var repos = BuildRepositories(fixture, includeCompletion: false, projectRootPath: bareRootName);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions
        {
            AllowFileProtocolRemote = true,
            AllowedOrchestrators = ["den-channels-runner"],
            ProjectRootSearchPaths = [managedRoot],
            ReviewedArtifactSearchPaths = [artifactRoot],
        });
        var remoteUrl = new Uri(fixture.RemotePath).AbsoluteUri;

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = remoteUrl,
            ReviewedGitBundlePath = bundlePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("reviewed git bundle", StringComparison.Ordinal) && d.Contains("configured artifact roots", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReviewedBranch_RejectsBareProjectRootThatEscapesConfiguredDevRoots()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: false, projectRootPath: "../outside");
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos, new TrustedPublisherOptions
        {
            AllowFileProtocolRemote = true,
            AllowedOrchestrators = ["den-channels-runner"],
            ProjectRootSearchPaths = [_root],
        });

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-channels-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("not a safe bare project root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishWorkerBranch_FailsClosedOnCompletionHeadMismatch()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: true, completionHead: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var service = BuildService(repos);

        var result = await service.PublishWorkerBranchAsync(new PublishWorkerBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RunId = "run-1",
            RequestedBy = "den-mcp-runner",
            ExpectedBranch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseCommit = fixture.Base,
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("Completion packet head mismatch", StringComparison.Ordinal));
        Assert.NotNull(result.AuditMessageId);
    }

    [Fact]
    public async Task PublishWorkerBranch_FailsClosedWhenDiffBaseIsInvalid()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: true);
        var service = BuildService(repos);

        var result = await service.PublishWorkerBranchAsync(new PublishWorkerBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RunId = "run-1",
            RequestedBy = "den-mcp-runner",
            ExpectedBranch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseCommit = "refs/heads/does-not-exist",
            AllowedPathPrefixes = "src",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Empty(result.ChangedFiles);
        Assert.Contains(result.Diagnostics, d => d.Contains("Worker workspace path is missing or unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishWorkerBranch_RejectsInvalidOrNonCanonicalRemoteName()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: true);
        var service = BuildService(repos);

        var result = await service.PublishWorkerBranchAsync(new PublishWorkerBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RunId = "run-1",
            RequestedBy = "den-mcp-runner",
            ExpectedBranch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseCommit = fixture.Base,
            RemoteName = "evil/origin",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("not a safe git remote token", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Contains("not the configured canonical remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishWorkerBranch_RejectsNonCompletedDurableSessionState()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        var repos = BuildRepositories(fixture, includeCompletion: true, assignmentState: WorkerPoolStates.Running);
        var service = BuildService(repos);

        var result = await service.PublishWorkerBranchAsync(new PublishWorkerBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RunId = "run-1",
            RequestedBy = "den-mcp-runner",
            ExpectedBranch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseCommit = fixture.Base,
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("Worker assignment must be completed before publish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReviewedBranch_FastForwardMainRejectsRemoteBaseMismatch()
    {
        var fixture = await GitFixture.CreateAsync(_root, taskId: 1285);
        await fixture.AdvanceRemoteMainAsync();
        var repos = BuildRepositories(fixture, includeCompletion: false);
        repos.ReviewRound = LooksGoodRound(fixture);
        var service = BuildService(repos);

        var result = await service.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = "proj",
            TaskId = 1285,
            RequestedBy = "den-mcp-runner",
            Branch = "task/1285-trusted-publisher",
            ExpectedHeadCommit = fixture.Head,
            ExpectedBaseBranch = "main",
            ReviewRoundId = 7,
            Operation = "fast_forward_main",
            ExpectedRemoteUrl = fixture.RemotePath,
            ValidateOnly = true,
        });

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("not descendant of current remote base", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    private static TestRepositories BuildRepositories(GitFixture fixture, bool includeCompletion, string? completionHead = null, string assignmentState = WorkerPoolStates.Completed, string? projectRootPath = null)
    {
        var messages = new FakeMessageRepository();
        if (includeCompletion)
        {
            messages.Messages.Add(new Message
            {
                Id = 1,
                ProjectId = "proj",
                TaskId = 1285,
                Sender = "worker",
                Content = "completion",
                CreatedAt = DateTime.UtcNow,
                Metadata = JsonSerializer.SerializeToElement(new
                {
                    type = "implementation_packet",
                    completion_packet = true,
                    malformed = false,
                    status = "completed",
                    role = "coder",
                    project_id = "proj",
                    task_id = 1285,
                    run_id = "run-1",
                    branch = "task/1285-trusted-publisher",
                    head_commit = completionHead ?? fixture.Head,
                    base_commit = fixture.Base,
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        }

        var pool = new FakeWorkerPoolRepository().AddAssignment(new WorkerAssignment
        {
            Id = 1,
            WorkerIdentity = "worker-1",
            RunId = "run-1",
            ProjectId = "proj",
            TaskId = 1285,
            Role = "coder",
            AssignedBy = "runner",
            State = assignmentState,
            ProfileIdentity = "den-worker",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        return new TestRepositories
        {
            Projects = new FakeProjectRepository(new Project { Id = "proj", Name = "Proj", RootPath = projectRootPath ?? fixture.RootPath }),
            Pool = pool,
            Messages = messages,
            Findings = new FakeReviewFindingRepository(),
        };
    }

    private static TrustedPublisherService BuildService(TestRepositories repos, TrustedPublisherOptions? options = null) => new(
        repos.Projects,
        repos.Pool,
        repos.Messages,
        repos.Rounds,
        repos.Findings,
        new SystemProcessRunner(),
        options ?? new TrustedPublisherOptions { AllowFileProtocolRemote = true, AllowedOrchestrators = ["den-mcp-runner"], AllowedTargetBranches = ["main"] },
        NullLogger<TrustedPublisherService>.Instance);

    private static ReviewRound LooksGoodRound(GitFixture fixture) => new()
    {
        Id = 7,
        TaskId = 1285,
        RoundNumber = 1,
        RequestedBy = "den-mcp-runner",
        Branch = "task/1285-trusted-publisher",
        BaseBranch = "main",
        BaseCommit = fixture.Base,
        HeadCommit = fixture.Head,
        Verdict = ReviewVerdict.LooksGood,
        VerdictBy = "reviewer",
    };

    private sealed class TestRepositories
    {
        public required FakeProjectRepository Projects { get; init; }
        public required FakeWorkerPoolRepository Pool { get; init; }
        public required FakeMessageRepository Messages { get; init; }
        public FakeReviewRoundRepository Rounds { get; } = new();
        public required FakeReviewFindingRepository Findings { get; init; }
        public ReviewRound? ReviewRound { set => Rounds.Round = value; }
    }

    private sealed record GitFixture(string RootPath, string WorkspacePath, string RemotePath, string Base, string Head)
    {
        public static async Task<GitFixture> CreateAsync(string root, int taskId)
        {
            Directory.CreateDirectory(root);
            var remote = Path.Combine(root, "remote.git");
            var main = Path.Combine(root, "main");
            var worker = Path.Combine(root, "worker");
            await Git(root, "init", "--bare", remote);
            await Git(root, "init", "-b", "main", main);
            await Git(main, "config", "user.email", "test@example.invalid");
            await Git(main, "config", "user.name", "Test User");
            Directory.CreateDirectory(Path.Combine(main, "src"));
            await File.WriteAllTextAsync(Path.Combine(main, "src", "app.txt"), "base\n");
            await Git(main, "add", ".");
            await Git(main, "commit", "-m", "base");
            var baseSha = await GitOut(main, "rev-parse", "HEAD");
            await Git(main, "remote", "add", "origin", remote);
            await Git(main, "push", "-u", "origin", "main");
            await Git(remote, "symbolic-ref", "HEAD", "refs/heads/main");
            await Git(root, "clone", remote, worker);
            await Git(worker, "config", "user.email", "worker@example.invalid");
            await Git(worker, "config", "user.name", "Worker User");
            await Git(worker, "checkout", "-b", $"task/{taskId}-trusted-publisher");
            await File.AppendAllTextAsync(Path.Combine(worker, "src", "app.txt"), "worker\n");
            await Git(worker, "add", ".");
            await Git(worker, "commit", "-m", "worker change");
            var headSha = await GitOut(worker, "rev-parse", "HEAD");
            await Git(worker, "push", "origin", $"HEAD:refs/heads/task/{taskId}-trusted-publisher");
            await Git(main, "fetch", "origin", $"task/{taskId}-trusted-publisher");
            await Git(main, "checkout", "-b", $"task/{taskId}-trusted-publisher", "FETCH_HEAD");
            return new GitFixture(main, worker, remote, baseSha, headSha);
        }

        private static async Task Git(string workdir, params string[] args)
        {
            var all = new[] { "-C", workdir }.Concat(args).ToArray();
            var result = await new SystemProcessRunner().RunAsync("git", all, TimeSpan.FromSeconds(20));
            if (!result.Succeeded) throw new InvalidOperationException($"git {string.Join(' ', all)} failed: {result.Stderr}");
        }

        private static async Task<string> GitOut(string workdir, params string[] args)
        {
            var all = new[] { "-C", workdir }.Concat(args).ToArray();
            var result = await new SystemProcessRunner().RunAsync("git", all, TimeSpan.FromSeconds(20));
            if (!result.Succeeded) throw new InvalidOperationException(result.Stderr);
            return result.Stdout.Trim();
        }

        public async Task AdvanceRemoteMainAsync()
        {
            await Git(RootPath, "checkout", "main");
            await File.AppendAllTextAsync(Path.Combine(RootPath, "src", "app.txt"), "remote-main\n");
            await Git(RootPath, "add", ".");
            await Git(RootPath, "commit", "-m", "advance main");
            await Git(RootPath, "push", "origin", "main");
            await Git(RootPath, "checkout", "task/1285-trusted-publisher");
        }

        public async Task CloneRemoteMainAsync(string parentDirectory, string repoName)
        {
            Directory.CreateDirectory(parentDirectory);
            var target = Path.Combine(parentDirectory, repoName);
            await Git(parentDirectory, "clone", RemotePath, target);
            await Git(target, "checkout", "main");
        }

        public Task DeleteRemoteTaskBranchAsync(int taskId)
            => Git(RootPath, "push", "origin", "--delete", $"task/{taskId}-trusted-publisher");

        public async Task<string> CreateReviewedBundleAsync(string artifactRoot, string fileName, int taskId)
        {
            Directory.CreateDirectory(artifactRoot);
            var bundlePath = Path.Combine(artifactRoot, fileName);
            await Git(WorkspacePath, "bundle", "create", bundlePath, $"task/{taskId}-trusted-publisher");
            return bundlePath;
        }
    }

    private sealed class FakeProjectRepository(Project project) : IProjectRepository
    {
        public Task<Project> CreateAsync(Project project) => Task.FromResult(project);
        public Task<Project?> GetByIdAsync(string id) => Task.FromResult<Project?>(project.Id == id ? project : null);
        public Task<List<Project>> GetAllAsync() => Task.FromResult(new List<Project> { project });
        public Task<List<Project>> ListAsync(string? kind = null, bool includeHidden = false, bool includeArchived = false) => Task.FromResult(new List<Project> { project });
        public Task<ProjectWithStats> GetWithStatsAsync(string id, string? agent = null) => throw new NotSupportedException();
        public Task<Project> UpdateVisibilityAsync(string id, string visibility) => throw new NotSupportedException();
        public Task<Project> UpdateProjectAsync(string id, ProjectUpdateRequest update) =>
            Task.FromResult(project.Id == id ? project : throw new KeyNotFoundException($"Project '{id}' not found"));
        public Task<Dictionary<string, int>> GetDependentRecordCountsAsync(string id) => Task.FromResult(new Dictionary<string, int>());
        public Task DeleteSpaceAsync(string id) => Task.CompletedTask;
    }

    private sealed class FakeMessageRepository : IMessageRepository
    {
        public List<Message> Messages { get; } = [];
        public List<Message> Created { get; } = [];
        public Task<Message> CreateAsync(Message message)
        {
            message.Id = Messages.Count + Created.Count + 1;
            message.CreatedAt = DateTime.UtcNow;
            Created.Add(message);
            return Task.FromResult(message);
        }
        public Task<Message?> GetByIdAsync(int id) => Task.FromResult<Message?>(Messages.Concat(Created).FirstOrDefault(m => m.Id == id));
        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null, DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null)
            => Task.FromResult(Messages.Concat(Created).Where(m => m.ProjectId == projectId && (taskId is null || m.TaskId == taskId)).Take(limit).ToList());
        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null) => throw new NotSupportedException();
        public Task<Thread> GetThreadAsync(int threadId) => throw new NotSupportedException();
        public Task<int> MarkReadAsync(string agent, int[] messageIds) => throw new NotSupportedException();
        public Task<List<NotificationFeedItem>> GetNotificationFeedAsync(string? projectId = null, int? taskId = null, string? sender = null, string? metadataType = null, string? urgency = null, bool? isRead = null, string? readForAgent = null, int limit = 20, int offset = 0)
            => throw new NotSupportedException();
        public Task<int> MarkNotificationsReadAsync(string agent, int[]? notificationIds) => throw new NotSupportedException();
        public Task<int> MarkAllNotificationsReadAsync(string agent, string projectId, int? taskId = null) => throw new NotSupportedException();
    }

    private sealed class FakeReviewRoundRepository : IReviewRoundRepository
    {
        public ReviewRound? Round { get; set; }
        public Task<ReviewRound> CreateAsync(CreateReviewRoundInput input) => throw new NotSupportedException();
        public Task<ReviewRound?> GetByIdAsync(int id) => Task.FromResult(Round?.Id == id ? Round : null);
        public Task<List<ReviewRound>> ListByTaskAsync(int taskId) => Task.FromResult(Round?.TaskId == taskId ? [Round] : new List<ReviewRound>());
        public Task<ReviewRound?> GetLatestByTaskAsync(int taskId) => Task.FromResult(Round?.TaskId == taskId ? Round : null);
        public Task<ReviewRound> SetVerdictAsync(int id, ReviewVerdict verdict, string decidedBy, string? notes = null) => throw new NotSupportedException();
    }

    private sealed class FakeReviewFindingRepository : IReviewFindingRepository
    {
        public List<ReviewFinding> Findings { get; } = [];
        public Task<ReviewFinding> CreateAsync(CreateReviewFindingInput input) => throw new NotSupportedException();
        public Task<List<ReviewFinding>> ListByTaskAsync(int taskId, ReviewFindingStatus[]? statuses = null) => Task.FromResult(Findings.Where(f => f.TaskId == taskId).ToList());
        public Task<List<ReviewFinding>> ListByReviewRoundAsync(int reviewRoundId, ReviewFindingStatus[]? statuses = null) => Task.FromResult(Findings.Where(f => f.ReviewRoundId == reviewRoundId).ToList());
        public Task<ReviewFinding?> GetByIdAsync(int id) => Task.FromResult(Findings.FirstOrDefault(f => f.Id == id));
        public Task<ReviewFinding> RespondAsync(int id, RespondToReviewFindingInput input) => throw new NotSupportedException();
        public Task<ReviewFinding> SetStatusAsync(int id, UpdateReviewFindingStatusInput input) => throw new NotSupportedException();
    }
}
