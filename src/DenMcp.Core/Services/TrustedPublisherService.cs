using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

public sealed class TrustedPublisherOptions
{
    public string[] AllowedOrchestrators { get; set; } = ["den-mcp-runner"];
    public string[] AllowedTargetBranches { get; set; } = ["main"];
    public string CanonicalRemoteName { get; set; } = "origin";
    public string? CanonicalRemoteUrl { get; set; }
    public bool AllowFileProtocolRemote { get; set; }
    public bool RequireReviewTestsForMerge { get; set; }
}

public sealed class PublishWorkerBranchRequest
{
    public required string ProjectId { get; init; }
    public required int TaskId { get; init; }
    public required string RunId { get; init; }
    public required string RequestedBy { get; init; }
    public required string ExpectedBranch { get; init; }
    public required string ExpectedHeadCommit { get; init; }
    public string Role { get; init; } = "coder";
    public string? ExpectedBaseCommit { get; init; }
    public string? AllowedPathPrefixes { get; init; }
    public string? RemoteName { get; init; }
    public string? ExpectedRemoteUrl { get; init; }
    public bool ValidateOnly { get; init; }
}

public sealed class PublishReviewedBranchRequest
{
    public required string ProjectId { get; init; }
    public required int TaskId { get; init; }
    public required string RequestedBy { get; init; }
    public required string Branch { get; init; }
    public required string ExpectedHeadCommit { get; init; }
    public required string ExpectedBaseBranch { get; init; }
    public required int ReviewRoundId { get; init; }
    public string Operation { get; init; } = "push_branch";
    public string? RemoteName { get; init; }
    public string? ExpectedRemoteUrl { get; init; }
    public bool ValidateOnly { get; init; }
}

public sealed class TrustedPublisherResult
{
    public required string Status { get; set; }
    public required string Mode { get; init; }
    public required string Summary { get; set; }
    public List<string> Diagnostics { get; init; } = [];
    public List<string> ValidationDecisions { get; init; } = [];
    public List<string> ChangedFiles { get; init; } = [];
    public string? ProjectId { get; init; }
    public int? TaskId { get; init; }
    public string? RequestedBy { get; init; }
    public string? Branch { get; init; }
    public string? BaseBranch { get; init; }
    public string? HeadCommit { get; init; }
    public string? RemoteName { get; init; }
    public string? RemoteUrl { get; init; }
    public string? WorkspacePath { get; init; }
    public string? Operation { get; init; }
    public int? ReviewRoundId { get; init; }
    public int? AuditMessageId { get; set; }
    public bool ValidateOnly { get; init; }
}

public interface ITrustedPublisherService
{
    Task<TrustedPublisherResult> PublishWorkerBranchAsync(PublishWorkerBranchRequest request, CancellationToken cancellationToken = default);
    Task<TrustedPublisherResult> PublishReviewedBranchAsync(PublishReviewedBranchRequest request, CancellationToken cancellationToken = default);
}

public sealed class TrustedPublisherService : ITrustedPublisherService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex SafeSha = new("^[0-9a-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SafeBranch = new("^task/[0-9]+[A-Za-z0-9._/-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeRemoteName = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    private readonly IProjectRepository _projects;
    private readonly IPiSessionService _piSessions;
    private readonly IMessageRepository _messages;
    private readonly IReviewRoundRepository _reviewRounds;
    private readonly IReviewFindingRepository _reviewFindings;
    private readonly IProcessRunner _processes;
    private readonly TrustedPublisherOptions _options;
    private readonly ILogger<TrustedPublisherService> _logger;

    public TrustedPublisherService(
        IProjectRepository projects,
        IPiSessionService piSessions,
        IMessageRepository messages,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        IProcessRunner processes,
        TrustedPublisherOptions options,
        ILogger<TrustedPublisherService> logger)
    {
        _projects = projects;
        _piSessions = piSessions;
        _messages = messages;
        _reviewRounds = reviewRounds;
        _reviewFindings = reviewFindings;
        _processes = processes;
        _options = options;
        _logger = logger;
    }

    public async Task<TrustedPublisherResult> PublishWorkerBranchAsync(PublishWorkerBranchRequest request, CancellationToken cancellationToken = default)
    {
        var decisions = new List<string>();
        var diagnostics = ValidateCommonRequest(request.ProjectId, request.TaskId, request.RequestedBy, request.ExpectedBranch, request.ExpectedHeadCommit);
        ValidateRemoteName(request.RemoteName, diagnostics);
        if (!SafeTaskBranch(request.ExpectedBranch, request.TaskId))
            diagnostics.Add($"Branch '{request.ExpectedBranch}' is not a safe task-scoped branch for task {request.TaskId}.");

        var project = await _projects.GetByIdAsync(request.ProjectId).ConfigureAwait(false);
        var rootPath = ProjectRoot(project, diagnostics);
        var run = await FindRunAsync(request.ProjectId, request.RunId, request.TaskId, cancellationToken).ConfigureAwait(false);
        if (run is null)
            diagnostics.Add($"Worker run/session '{request.RunId}' was not found for project '{request.ProjectId}'.");
        else
        {
            if (run.Session.TaskId != request.TaskId)
                diagnostics.Add($"Worker task mismatch: expected {request.TaskId}, found {run.Session.TaskId?.ToString() ?? "none"}.");
            if (!string.Equals(run.Session.State, PiSessionStates.Completed, StringComparison.Ordinal))
                diagnostics.Add($"Worker session must be durable terminal/completed before publish; found '{run.Session.State}'.");
            else
                decisions.Add("worker session state is completed in durable Den state");
            var role = NormalizeRole(request.Role);
            var durableRole = NormalizeRole(run.Session.ToolProfile ?? run.LaunchProfile?.WorkerRole);
            if (durableRole is not null && role != durableRole)
                diagnostics.Add($"Worker role mismatch: expected '{role}', found '{durableRole}'.");
            decisions.Add("resolved worker run/session from Den state");
        }

        var completion = await FindLatestCompletionAsync(request.ProjectId, request.TaskId, request.RunId, request.Role).ConfigureAwait(false);
        if (completion is null)
        {
            diagnostics.Add("No matching structured worker completion packet was found.");
        }
        else
        {
            VerifyCompletion(completion, request.RunId, request.TaskId, request.Role, request.ExpectedBranch, request.ExpectedHeadCommit, diagnostics, decisions);
        }

        var workspace = run?.LaunchProfile?.WorkspaceSourceProjectDir ?? run?.LaunchProfile?.DevDir;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            diagnostics.Add($"Worker workspace path is missing or unavailable: {workspace ?? "<missing>"}.");

        string? canonicalRemote = null;
        string? remoteUrl = null;
        List<string> changedFiles = [];
        if (diagnostics.Count == 0 && rootPath is not null && workspace is not null)
        {
            var remoteName = RemoteName(request.RemoteName);
            canonicalRemote = await ResolveCanonicalRemoteAsync(rootPath, request.ExpectedRemoteUrl, cancellationToken).ConfigureAwait(false);
            remoteUrl = await GitTrimAsync(workspace, ["remote", "get-url", remoteName], cancellationToken).ConfigureAwait(false);
            VerifyRemote(remoteUrl, canonicalRemote, diagnostics, decisions);
            var workspaceHead = await GitTrimAsync(workspace, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
            if (!ShaEquals(workspaceHead, request.ExpectedHeadCommit))
                diagnostics.Add($"Workspace HEAD mismatch: expected {request.ExpectedHeadCommit}, found {workspaceHead ?? "<missing>"}.");
            else
                decisions.Add("workspace HEAD matched expected head");

            var baseRef = request.ExpectedBaseCommit ?? MetadataString(completion, "base_commit") ?? $"{remoteName}/main";
            changedFiles = await GitLinesAsync(workspace, ["diff", "--name-only", $"{baseRef}...{request.ExpectedHeadCommit}"], diagnostics, "changed-file scope diff", cancellationToken).ConfigureAwait(false);
            var allowedPrefixes = ParseCsv(request.AllowedPathPrefixes);
            var outside = OutsideAllowedPrefixes(changedFiles, allowedPrefixes).ToList();
            if (outside.Count > 0)
                diagnostics.Add($"Changed files outside allowed scope: {string.Join(", ", outside)}.");
            else if (allowedPrefixes.Count > 0)
                decisions.Add("changed files were within allowed path prefixes");
        }

        var result = new TrustedPublisherResult
        {
            Status = diagnostics.Count == 0 ? "validated" : "rejected",
            Mode = "publish_worker_branch",
            Summary = diagnostics.Count == 0 ? $"validated worker branch {request.ExpectedBranch} at {request.ExpectedHeadCommit}" : "worker branch publish rejected",
            Diagnostics = diagnostics,
            ValidationDecisions = decisions,
            ChangedFiles = changedFiles,
            ProjectId = request.ProjectId,
            TaskId = request.TaskId,
            RequestedBy = request.RequestedBy,
            Branch = request.ExpectedBranch,
            HeadCommit = request.ExpectedHeadCommit,
            RemoteName = RemoteName(request.RemoteName),
            RemoteUrl = RedactRemote(remoteUrl ?? canonicalRemote),
            WorkspacePath = workspace,
            Operation = "push_branch",
            ValidateOnly = request.ValidateOnly,
        };

        if (diagnostics.Count == 0 && !request.ValidateOnly && rootPath is not null && workspace is not null)
        {
            var objectAvailable = await EnsureCommitAvailableInProjectRootAsync(rootPath, workspace, request.ExpectedHeadCommit, diagnostics, decisions, cancellationToken).ConfigureAwait(false);
            var push = objectAvailable
                ? await RunGitAsync(rootPath, ["push", RemoteName(request.RemoteName), $"{request.ExpectedHeadCommit}:refs/heads/{request.ExpectedBranch}"], cancellationToken).ConfigureAwait(false)
                : new ProcessRunResult { ExitCode = -1, Stderr = "expected commit was not available in project root" };
            if (!push.Succeeded)
            {
                result.Status = "failed";
                result.Summary = "worker branch publish failed during git push";
                result.Diagnostics.Add($"git push failed: {SafeGitError(push)}");
            }
            else
            {
                result.Status = "published";
                result.Summary = $"published {request.ExpectedBranch} at {request.ExpectedHeadCommit}";
                result.ValidationDecisions.Add("git push completed using server-side publisher context");
            }
        }

        result.AuditMessageId = await AuditAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<TrustedPublisherResult> PublishReviewedBranchAsync(PublishReviewedBranchRequest request, CancellationToken cancellationToken = default)
    {
        var decisions = new List<string>();
        var diagnostics = ValidateCommonRequest(request.ProjectId, request.TaskId, request.RequestedBy, request.Branch, request.ExpectedHeadCommit);
        ValidateRemoteName(request.RemoteName, diagnostics);
        var operation = NormalizeOperation(request.Operation);
        if (operation is null)
            diagnostics.Add($"Unsupported trusted publisher operation '{request.Operation}'.");
        if (!_options.AllowedOrchestrators.Contains(request.RequestedBy, StringComparer.Ordinal))
            diagnostics.Add($"Requester '{request.RequestedBy}' is not an allowed trusted orchestrator.");
        else
            decisions.Add("requester is an allowed trusted orchestrator");
        if (!_options.AllowedTargetBranches.Contains(request.ExpectedBaseBranch, StringComparer.Ordinal))
            diagnostics.Add($"Target/base branch '{request.ExpectedBaseBranch}' is not allowed.");
        if (!SafeTaskBranch(request.Branch, request.TaskId))
            diagnostics.Add($"Branch '{request.Branch}' is not a safe task-scoped branch for task {request.TaskId}.");

        var project = await _projects.GetByIdAsync(request.ProjectId).ConfigureAwait(false);
        var rootPath = ProjectRoot(project, diagnostics);
        var round = await _reviewRounds.GetByIdAsync(request.ReviewRoundId).ConfigureAwait(false);
        if (round is null)
        {
            diagnostics.Add($"Review round {request.ReviewRoundId} was not found.");
        }
        else
        {
            if (round.TaskId != request.TaskId)
                diagnostics.Add($"Review round task mismatch: expected {request.TaskId}, found {round.TaskId}.");
            if (!string.Equals(round.Branch, request.Branch, StringComparison.Ordinal))
                diagnostics.Add($"Review branch mismatch: expected '{request.Branch}', found '{round.Branch}'.");
            if (!ShaEquals(round.HeadCommit, request.ExpectedHeadCommit))
                diagnostics.Add($"Reviewed head mismatch: expected {request.ExpectedHeadCommit}, found {round.HeadCommit}.");
            if (!string.Equals(round.BaseBranch, request.ExpectedBaseBranch, StringComparison.Ordinal))
                diagnostics.Add($"Review base branch mismatch: expected '{request.ExpectedBaseBranch}', found '{round.BaseBranch}'.");
            if (round.Verdict != ReviewVerdict.LooksGood)
                diagnostics.Add($"Review verdict must be looks_good; found {round.Verdict?.ToString() ?? "none"}.");
            else
                decisions.Add("review round has looks_good verdict");
            if (_options.RequireReviewTestsForMerge && (round.TestsRun is null || round.TestsRun.Count == 0))
                diagnostics.Add("Review policy requires tests/evidence on the review round.");
        }

        var findings = await _reviewFindings.ListByReviewRoundAsync(request.ReviewRoundId).ConfigureAwait(false);
        var unresolvedBlocking = findings.Where(f => f.Category == ReviewFindingCategory.BlockingBug && f.Status != ReviewFindingStatus.VerifiedFixed && f.Status != ReviewFindingStatus.Superseded && f.Status != ReviewFindingStatus.SplitToFollowUp).ToList();
        if (unresolvedBlocking.Count > 0)
            diagnostics.Add($"Unresolved blocking findings remain: {string.Join(", ", unresolvedBlocking.Select(f => f.Id))}.");
        else
            decisions.Add("no unresolved blocking findings remain");

        string? remoteUrl = null;
        if (diagnostics.Count == 0 && rootPath is not null)
        {
            var remoteName = RemoteName(request.RemoteName);
            var canonicalRemote = await ResolveCanonicalRemoteAsync(rootPath, request.ExpectedRemoteUrl, cancellationToken).ConfigureAwait(false);
            remoteUrl = await GitTrimAsync(rootPath, ["remote", "get-url", remoteName], cancellationToken).ConfigureAwait(false);
            VerifyRemote(remoteUrl, canonicalRemote, diagnostics, decisions);
            var fetch = await RunGitAsync(rootPath, ["fetch", "--no-tags", remoteName, $"+refs/heads/{request.ExpectedBaseBranch}:refs/remotes/{remoteName}/{request.ExpectedBaseBranch}"], cancellationToken).ConfigureAwait(false);
            if (!fetch.Succeeded)
            {
                diagnostics.Add($"git fetch of remote/base state failed: {SafeGitError(fetch)}");
            }
            var branchHead = await GitTrimAsync(rootPath, ["rev-parse", request.Branch], cancellationToken).ConfigureAwait(false);
            if (!ShaEquals(branchHead, request.ExpectedHeadCommit))
                diagnostics.Add($"Local branch head mismatch: expected {request.ExpectedHeadCommit}, found {branchHead ?? "<missing>"}.");
            else
                decisions.Add("local branch head matches reviewed head");

            if (fetch.Succeeded && operation == "fast_forward_main" && round is not null)
                await VerifyFastForwardRemoteStateAsync(rootPath, remoteName, request.ExpectedBaseBranch, request.ExpectedHeadCommit, round.BaseCommit, diagnostics, decisions, cancellationToken).ConfigureAwait(false);
        }

        var result = new TrustedPublisherResult
        {
            Status = diagnostics.Count == 0 ? "validated" : "rejected",
            Mode = "publish_reviewed_branch",
            Summary = diagnostics.Count == 0 ? $"validated {operation} for {request.Branch} at {request.ExpectedHeadCommit}" : "reviewed branch publish rejected",
            Diagnostics = diagnostics,
            ValidationDecisions = decisions,
            ProjectId = request.ProjectId,
            TaskId = request.TaskId,
            RequestedBy = request.RequestedBy,
            Branch = request.Branch,
            BaseBranch = request.ExpectedBaseBranch,
            HeadCommit = request.ExpectedHeadCommit,
            RemoteName = RemoteName(request.RemoteName),
            RemoteUrl = RedactRemote(remoteUrl ?? request.ExpectedRemoteUrl ?? _options.CanonicalRemoteUrl),
            WorkspacePath = rootPath,
            Operation = operation,
            ReviewRoundId = request.ReviewRoundId,
            ValidateOnly = request.ValidateOnly,
        };

        if (diagnostics.Count == 0 && !request.ValidateOnly && rootPath is not null)
        {
            ProcessRunResult push = operation == "fast_forward_main"
                ? await RunGitAsync(rootPath, ["push", RemoteName(request.RemoteName), $"{request.ExpectedHeadCommit}:refs/heads/{request.ExpectedBaseBranch}"], cancellationToken).ConfigureAwait(false)
                : await RunGitAsync(rootPath, ["push", RemoteName(request.RemoteName), $"{request.ExpectedHeadCommit}:refs/heads/{request.Branch}"], cancellationToken).ConfigureAwait(false);
            if (!push.Succeeded)
            {
                result.Status = "failed";
                result.Summary = "trusted reviewed branch operation failed during git push";
                result.Diagnostics.Add($"git push failed: {SafeGitError(push)}");
            }
            else
            {
                result.Status = "published";
                result.Summary = operation == "fast_forward_main"
                    ? $"fast-forwarded {request.ExpectedBaseBranch} to reviewed head {request.ExpectedHeadCommit}"
                    : $"published reviewed branch {request.Branch} at {request.ExpectedHeadCommit}";
                result.ValidationDecisions.Add("git push completed using server-side publisher context");
            }
        }

        result.AuditMessageId = await AuditAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<int> AuditAsync(TrustedPublisherResult result, CancellationToken cancellationToken)
    {
        var content = BuildAuditContent(result);
        var metadata = JsonSerializer.SerializeToElement(new
        {
            type = "trusted_publisher_audit",
            schema = "den_trusted_publisher_audit",
            schema_version = 1,
            mode = result.Mode,
            status = result.Status,
            requested_by = result.RequestedBy,
            branch = result.Branch,
            base_branch = result.BaseBranch,
            head_commit = result.HeadCommit,
            remote_name = result.RemoteName,
            remote_url = result.RemoteUrl,
            operation = result.Operation,
            review_round_id = result.ReviewRoundId,
            validate_only = result.ValidateOnly,
            diagnostics = result.Diagnostics,
            validation_decisions = result.ValidationDecisions,
            changed_files = result.ChangedFiles,
        }, JsonOptions);
        var message = await _messages.CreateAsync(new Message
        {
            ProjectId = result.ProjectId!,
            TaskId = result.TaskId,
            Sender = "trusted-publisher",
            Intent = result.Status is "published" or "validated" ? MessageIntent.StatusUpdate : MessageIntent.TaskBlocked,
            Content = content,
            Metadata = metadata,
        }).ConfigureAwait(false);
        return message.Id;
    }

    private static string BuildAuditContent(TrustedPublisherResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Trusted publisher audit");
        sb.AppendLine();
        sb.AppendLine($"- Mode: `{result.Mode}`");
        sb.AppendLine($"- Status: `{result.Status}`");
        sb.AppendLine($"- Requested by: `{result.RequestedBy}`");
        sb.AppendLine($"- Branch: `{result.Branch}`");
        if (!string.IsNullOrWhiteSpace(result.BaseBranch)) sb.AppendLine($"- Base branch: `{result.BaseBranch}`");
        sb.AppendLine($"- Head commit: `{result.HeadCommit}`");
        sb.AppendLine($"- Operation: `{result.Operation}`");
        sb.AppendLine($"- Remote: `{result.RemoteName}` `{result.RemoteUrl ?? "not resolved"}`");
        if (result.ReviewRoundId is not null) sb.AppendLine($"- Review round: `#{result.ReviewRoundId}`");
        sb.AppendLine($"- Validate only: `{result.ValidateOnly}`");
        sb.AppendLine();
        sb.AppendLine("## Validation decisions");
        foreach (var decision in result.ValidationDecisions.DefaultIfEmpty("none")) sb.AppendLine($"- {decision}");
        if (result.Diagnostics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Diagnostics");
            foreach (var diagnostic in result.Diagnostics) sb.AppendLine($"- {diagnostic}");
        }
        if (result.ChangedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Changed files");
            foreach (var file in result.ChangedFiles) sb.AppendLine($"- `{file}`");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<PiSessionDetail?> FindRunAsync(string projectId, string runOrSessionId, int taskId, CancellationToken cancellationToken)
    {
        var bySession = await _piSessions.GetAsync(projectId, runOrSessionId, cancellationToken).ConfigureAwait(false);
        if (bySession is not null) return bySession;
        var sessions = await _piSessions.ListAsync(new PiSessionListOptions { ProjectId = projectId, TaskId = taskId, Limit = 200 }, cancellationToken).ConfigureAwait(false);
        var match = sessions.FirstOrDefault(s => string.Equals(s.RunId, runOrSessionId, StringComparison.Ordinal));
        return match is null ? null : await _piSessions.GetAsync(projectId, match.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Message?> FindLatestCompletionAsync(string projectId, int taskId, string runId, string role)
    {
        var normalizedRole = NormalizeRole(role);
        var candidates = await _messages.GetMessagesAsync(projectId, taskId, limit: 100).ConfigureAwait(false);
        return candidates.FirstOrDefault(m =>
            MetadataBool(m, "completion_packet")
            && !MetadataBool(m, "malformed")
            && string.Equals(MetadataString(m, "status"), "completed", StringComparison.Ordinal)
            && (string.Equals(MetadataString(m, "run_id"), runId, StringComparison.Ordinal) || string.Equals(MetadataString(m, "session_id"), runId, StringComparison.Ordinal))
            && string.Equals(NormalizeRole(MetadataString(m, "role")), normalizedRole, StringComparison.Ordinal));
    }

    private static void VerifyCompletion(Message completion, string runId, int taskId, string role, string expectedBranch, string expectedHead, List<string> diagnostics, List<string> decisions)
    {
        if (completion.TaskId != taskId) diagnostics.Add($"Completion packet task mismatch: expected {taskId}, found {completion.TaskId?.ToString() ?? "none"}.");
        if (!string.Equals(NormalizeRole(MetadataString(completion, "role")), NormalizeRole(role), StringComparison.Ordinal)) diagnostics.Add("Completion packet role mismatch.");
        if (!string.Equals(MetadataString(completion, "branch"), expectedBranch, StringComparison.Ordinal)) diagnostics.Add($"Completion packet branch mismatch: expected '{expectedBranch}', found '{MetadataString(completion, "branch") ?? "<missing>"}'.");
        if (!ShaEquals(MetadataString(completion, "head_commit"), expectedHead)) diagnostics.Add($"Completion packet head mismatch: expected {expectedHead}, found {MetadataString(completion, "head_commit") ?? "<missing>"}.");
        if (diagnostics.Count == 0) decisions.Add("completion packet matched run/session/task/role/branch/head");
    }

    private async Task<string?> ResolveCanonicalRemoteAsync(string rootPath, string? expectedRemoteUrl, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(expectedRemoteUrl)) return NormalizeRemote(expectedRemoteUrl);
        if (!string.IsNullOrWhiteSpace(_options.CanonicalRemoteUrl)) return NormalizeRemote(_options.CanonicalRemoteUrl);
        return NormalizeRemote(await GitTrimAsync(rootPath, ["remote", "get-url", _options.CanonicalRemoteName], cancellationToken).ConfigureAwait(false));
    }

    private void VerifyRemote(string? observedRemote, string? expectedRemote, List<string> diagnostics, List<string> decisions)
    {
        var observed = NormalizeRemote(observedRemote);
        var expected = NormalizeRemote(expectedRemote);
        if (string.IsNullOrWhiteSpace(observed)) diagnostics.Add("Could not resolve target remote URL.");
        if (string.IsNullOrWhiteSpace(expected)) diagnostics.Add("Could not resolve canonical remote URL.");
        if (!string.IsNullOrWhiteSpace(observed) && IsFileRemote(observed) && !_options.AllowFileProtocolRemote) diagnostics.Add("File protocol remotes are not allowed by trusted publisher policy.");
        if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(observed, expected, StringComparison.Ordinal)) diagnostics.Add("Target remote does not match canonical project remote.");
        if (diagnostics.Count == 0) decisions.Add("target remote matched canonical remote");
    }

    private async Task<string?> GitTrimAsync(string rootPath, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(rootPath, args, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return null;
        return result.Stdout.Trim();
    }

    private async Task<List<string>> GitLinesAsync(string rootPath, IReadOnlyList<string> args, List<string> diagnostics, string operationName, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(rootPath, args, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            diagnostics.Add($"git {operationName} failed: {SafeGitError(result)}");
            return [];
        }
        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private async Task<bool> EnsureCommitAvailableInProjectRootAsync(string rootPath, string workerWorkspace, string expectedHead, List<string> diagnostics, List<string> decisions, CancellationToken cancellationToken)
    {
        if (await CommitExistsAsync(rootPath, expectedHead, cancellationToken).ConfigureAwait(false))
        {
            decisions.Add("expected head commit is available in canonical project root");
            return true;
        }

        var fetch = await RunGitAsync(rootPath, ["fetch", "--no-tags", workerWorkspace, expectedHead], cancellationToken).ConfigureAwait(false);
        if (!fetch.Succeeded)
        {
            diagnostics.Add($"Could not fetch expected head into canonical project root: {SafeGitError(fetch)}");
            return false;
        }

        if (!await CommitExistsAsync(rootPath, expectedHead, cancellationToken).ConfigureAwait(false))
        {
            diagnostics.Add("Expected head was still unavailable in canonical project root after fetch.");
            return false;
        }

        decisions.Add("fetched expected head commit into canonical project root before push");
        return true;
    }

    private async Task VerifyFastForwardRemoteStateAsync(string rootPath, string remoteName, string baseBranch, string expectedHead, string? reviewBaseCommit, List<string> diagnostics, List<string> decisions, CancellationToken cancellationToken)
    {
        var remoteBaseRef = $"refs/remotes/{remoteName}/{baseBranch}";
        var remoteBaseCommit = await GitTrimAsync(rootPath, ["rev-parse", remoteBaseRef], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(remoteBaseCommit))
        {
            diagnostics.Add($"Could not resolve remote base ref '{remoteBaseRef}'.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(reviewBaseCommit))
        {
            if (ShaEquals(remoteBaseCommit, reviewBaseCommit))
            {
                decisions.Add("remote base matches reviewed base commit");
            }
            else if (await IsAncestorAsync(rootPath, reviewBaseCommit, remoteBaseCommit, cancellationToken).ConfigureAwait(false))
            {
                decisions.Add("remote base has advanced from reviewed base but remains descendant of it");
            }
            else
            {
                diagnostics.Add($"Remote base {remoteBaseCommit} does not match or descend from reviewed base {reviewBaseCommit}.");
            }
        }

        if (await IsAncestorAsync(rootPath, remoteBaseCommit, expectedHead, cancellationToken).ConfigureAwait(false))
            decisions.Add("expected head is descendant of current remote base");
        else
            diagnostics.Add($"Expected head {expectedHead} is not descendant of current remote base {remoteBaseCommit}; refusing fast-forward push.");
    }

    private async Task<bool> CommitExistsAsync(string rootPath, string sha, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(rootPath, ["cat-file", "-e", $"{sha}^{{commit}}"], cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task<bool> IsAncestorAsync(string rootPath, string ancestor, string descendant, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(rootPath, ["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private Task<ProcessRunResult> RunGitAsync(string rootPath, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var fullArgs = new List<string> { "-C", rootPath };
        fullArgs.AddRange(args);
        _logger.LogDebug("Running trusted publisher git command: git {Args}", string.Join(' ', fullArgs.Select(a => a.Contains(' ') ? "..." : a)));
        return _processes.RunAsync("git", fullArgs, GitTimeout, cancellationToken);
    }

    private static List<string> ValidateCommonRequest(string projectId, int taskId, string requestedBy, string branch, string headCommit)
    {
        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(projectId)) diagnostics.Add("project_id is required.");
        if (taskId <= 0) diagnostics.Add("task_id must be positive.");
        if (string.IsNullOrWhiteSpace(requestedBy)) diagnostics.Add("requested_by is required.");
        if (!SafeBranch.IsMatch(branch ?? string.Empty)) diagnostics.Add($"Branch '{branch}' is not a supported safe task branch.");
        if (!SafeSha.IsMatch(headCommit ?? string.Empty)) diagnostics.Add("expected_head_commit must be a full 40-character SHA.");
        return diagnostics;
    }

    private static string? ProjectRoot(Project? project, List<string> diagnostics)
    {
        if (project is null)
        {
            diagnostics.Add("Project was not found.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(project.RootPath) || !Directory.Exists(project.RootPath))
        {
            diagnostics.Add($"Project root path is missing or unavailable: {project.RootPath ?? "<missing>"}.");
            return null;
        }
        return project.RootPath;
    }

    private static bool SafeTaskBranch(string branch, int taskId) => !string.IsNullOrWhiteSpace(branch) && SafeBranch.IsMatch(branch) && branch.StartsWith($"task/{taskId}-", StringComparison.Ordinal);
    private static bool ShaEquals(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string? NormalizeRole(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant().Replace('-', '_');
    private static string RemoteName(string? remoteName) => string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
    private void ValidateRemoteName(string? remoteName, List<string> diagnostics)
    {
        var resolved = RemoteName(remoteName);
        if (!SafeRemoteName.IsMatch(resolved))
            diagnostics.Add($"Remote name '{resolved}' is not a safe git remote token.");
        if (!string.Equals(resolved, _options.CanonicalRemoteName, StringComparison.Ordinal))
            diagnostics.Add($"Remote name '{resolved}' is not the configured canonical remote '{_options.CanonicalRemoteName}'.");
    }
    private static string? NormalizeOperation(string? value) => value?.Trim() switch { "push_branch" => "push_branch", "fast_forward_main" => "fast_forward_main", _ => null };
    private static List<string> ParseCsv(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Replace('\\', '/').Trim('/')).Where(p => p.Length > 0).ToList();
    private static IEnumerable<string> OutsideAllowedPrefixes(IEnumerable<string> files, IReadOnlyList<string> prefixes) => prefixes.Count == 0 ? [] : files.Where(f => !prefixes.Any(p => f.Equals(p, StringComparison.Ordinal) || f.StartsWith(p + "/", StringComparison.Ordinal)));
    private static string SafeGitError(ProcessRunResult result) => (string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr).Trim().ReplaceLineEndings(" ");
    private static bool IsFileRemote(string remote) => remote.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || remote.StartsWith("/", StringComparison.Ordinal);

    private static string? NormalizeRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        var value = remote.Trim();
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.TrimEnd('/');
    }

    private static string? RedactRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return remote;
        var value = remote.Trim();
        var at = value.IndexOf('@');
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        return scheme >= 0 && at > scheme ? value[..(scheme + 3)] + "***@" + value[(at + 1)..] : value;
    }

    private static string? MetadataString(Message? message, string key)
    {
        if (message?.Metadata is null || !message.Metadata.Value.TryGetProperty(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToString() : null;
    }

    private static bool MetadataBool(Message message, string key)
    {
        if (message.Metadata is null || !message.Metadata.Value.TryGetProperty(key, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }
}
