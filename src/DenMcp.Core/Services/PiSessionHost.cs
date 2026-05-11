using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public sealed class PiSessionLaunchPlan
{
    public required PiSessionRecord Record { get; init; }
    public required PiDockerLaunchProfile LaunchProfile { get; init; }
    public IReadOnlyList<string> LaunchCommand { get; init; } = [];
}

public sealed class PiSessionHostLaunchResult
{
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
}

public sealed class PiSessionHostStatus
{
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public string? OutputTail { get; init; }
    public DateTime? OutputTailCapturedAt { get; init; }
    public bool OutputTailTruncated { get; init; }
    public string? OutputTailSha256 { get; init; }
}

public sealed class PiSessionHostControlResult
{
    public required bool Succeeded { get; init; }
    public required string State { get; init; }
    public string? StateReason { get; init; }
    public DateTime? EndedAt { get; init; }
}

public interface IPiSessionHost
{
    string HostId { get; }
    Task<PiSessionHostLaunchResult> LaunchAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken = default);
    Task<PiSessionHostStatus> GetStatusAsync(PiSessionRecord session, CancellationToken cancellationToken = default);
    Task<PiSessionHostControlResult> TerminateAsync(PiSessionRecord session, CancellationToken cancellationToken = default);
    Task<PiSessionHostControlResult> CleanupAsync(PiSessionRecord session, PiDockerLaunchProfile? profile, CancellationToken cancellationToken = default);
}

public sealed class TmuxDockerPiSessionHost : IPiSessionHost
{
    private const string LabelPrefix = "@den.";
    private const int OutputTailLineCount = 80;
    private const int OutputTailMaxChars = 12000;
    private static readonly TimeSpan ContainerStartupGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LaunchFailureDetectionWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LaunchFailureDetectionPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly PiDockerLaunchProfileOptions _options;
    private readonly IProcessRunner _runner;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(15);

    public TmuxDockerPiSessionHost(
        PiDockerLaunchProfileOptions options,
        IProcessRunner runner,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _options = options;
        _runner = runner;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public string HostId => string.IsNullOrWhiteSpace(_options.HostId)
        ? Environment.MachineName
        : _options.HostId.Trim();

    public async Task<PiSessionHostLaunchResult> LaunchAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var workspaceError = await ProvisionWorkspaceAsync(plan.LaunchProfile, cancellationToken).ConfigureAwait(false);
        if (workspaceError is not null)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = workspaceError,
            };
        }

        var provisioningError = ProvisionPiState(plan.LaunchProfile);
        if (provisioningError is not null)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = provisioningError,
            };
        }

        var piStateValidationError = ValidatePiState(plan.LaunchProfile);
        if (piStateValidationError is not null)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = piStateValidationError,
            };
        }

        var newSessionArgs = new List<string> { "new-session", "-d", "-s", plan.Record.TmuxSessionName };
        if (!string.IsNullOrWhiteSpace(plan.LaunchProfile.DevDir))
        {
            newSessionArgs.Add("-c");
            newSessionArgs.Add(plan.LaunchProfile.DevDir);
        }
        // Keep tmux session creation minimal and deterministic. The Docker launch command
        // is rendered as an explicit `env KEY=VALUE ... docker compose ...` shell line below,
        // so duplicating launch-profile environment through `tmux new-session -e` is redundant
        // and can make the tmux server fragile when provider/config environment grows large.
        newSessionArgs.Add(RenderShellCommand(NormalizeTmuxShellCommand(_options.TmuxShellCommand)));

        var create = await RunTmuxAsync(plan.Record, newSessionArgs, cancellationToken).ConfigureAwait(false);
        if (!create.Succeeded)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = TrimError(create.Stderr)
            };
        }

        await SetMetadataAsync(plan, cancellationToken).ConfigureAwait(false);

        var commandLine = RenderShellCommand(plan.LaunchCommand);
        var send = await RunTmuxAsync(plan.Record, ["send-keys", "-t", plan.Record.TmuxSessionName, "-l", "--", commandLine], cancellationToken).ConfigureAwait(false);
        if (!send.Succeeded)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = TrimError(send.Stderr)
            };
        }

        var enter = await RunTmuxAsync(plan.Record, ["send-keys", "-t", plan.Record.TmuxSessionName, "Enter"], cancellationToken).ConfigureAwait(false);
        if (!enter.Succeeded)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = TrimError(enter.Stderr)
            };
        }

        var outputFailure = await DetectLaunchOutputFailureAsync(plan.Record, cancellationToken).ConfigureAwait(false);
        if (outputFailure is not null)
        {
            return new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Failed,
                StateReason = outputFailure,
                ContainerName = PiSessionContainerNames.Extract(plan.LaunchProfile),
            };
        }

        var now = _utcNow();
        return new PiSessionHostLaunchResult
        {
            State = PiSessionStates.Running,
            ContainerName = PiSessionContainerNames.Extract(plan.LaunchProfile),
            StartedAt = now,
            LastActivityAt = now,
        };
    }

    public async Task<PiSessionHostStatus> GetStatusAsync(PiSessionRecord session, CancellationToken cancellationToken = default)
    {
        var list = await RunTmuxAsync(session, [
            "list-sessions",
            "-F",
            "#{session_name}\t#{session_created}\t#{session_activity}"
        ], cancellationToken).ConfigureAwait(false);

        if (!list.Succeeded)
        {
            return new PiSessionHostStatus
            {
                State = PiSessionStates.Stale,
                StateReason = "tmux session list failed: " + TrimError(list.Stderr),
            };
        }

        foreach (var line in list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !string.Equals(parts[0], session.TmuxSessionName, StringComparison.Ordinal))
                continue;

            var output = await CaptureOutputTailAsync(session, cancellationToken).ConfigureAwait(false);
            var observedLastActivity = FromUnixSeconds(parts[2]) ?? session.LastActivityAt;
            var container = await InspectContainerAsync(session, output?.Tail, cancellationToken).ConfigureAwait(false);
            return new PiSessionHostStatus
            {
                State = container?.State ?? PiSessionStates.Running,
                StateReason = container?.StateReason,
                LastActivityAt = observedLastActivity,
                ContainerId = container?.ContainerId ?? session.ContainerId,
                ContainerName = container?.ContainerName ?? session.ContainerName,
                OutputTail = output?.Tail,
                OutputTailCapturedAt = output?.CapturedAt,
                OutputTailTruncated = output?.Truncated ?? false,
                OutputTailSha256 = output?.TailSha256,
            };
        }

        return new PiSessionHostStatus
        {
            State = PiSessionStates.Stale,
            StateReason = "tmux session was not found on the recorded host.",
        };
    }

    public async Task<PiSessionHostControlResult> TerminateAsync(PiSessionRecord session, CancellationToken cancellationToken = default)
    {
        var result = await RunTmuxAsync(session, ["kill-session", "-t", session.TmuxSessionName], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !TrimError(result.Stderr).Contains("can't find", StringComparison.OrdinalIgnoreCase))
        {
            return new PiSessionHostControlResult
            {
                Succeeded = false,
                State = PiSessionStates.Stale,
                StateReason = TrimError(result.Stderr),
            };
        }

        return new PiSessionHostControlResult
        {
            Succeeded = true,
            State = PiSessionStates.Completed,
            EndedAt = _utcNow(),
            StateReason = result.Succeeded ? "terminated by request" : "tmux session was already absent during termination",
        };
    }

    public async Task<PiSessionHostControlResult> CleanupAsync(PiSessionRecord session, PiDockerLaunchProfile? profile, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            return new PiSessionHostControlResult
            {
                Succeeded = false,
                State = session.State,
                StateReason = "launch profile was unavailable; docker compose cleanup was not attempted.",
            };
        }

        var args = new List<string>
        {
            "compose",
            "--project-name", profile.ComposeProjectName,
            "-f", profile.ComposeFile,
            "down",
            "--remove-orphans"
        };
        var result = await _runner.RunAsync(
            _options.DockerExecutable,
            args,
            TimeSpan.FromSeconds(60),
            cancellationToken,
            BuildDockerProcessEnvironment(profile)).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new PiSessionHostControlResult
            {
                Succeeded = false,
                State = session.State,
                StateReason = TrimError(result.Stderr),
            };
        }

        return new PiSessionHostControlResult
        {
            Succeeded = true,
            State = session.State,
            StateReason = "docker compose cleanup completed",
        };
    }

    private async Task SetMetadataAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["project_id"] = plan.Record.ProjectId,
            ["task_id"] = plan.Record.TaskId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["workspace_id"] = plan.Record.WorkspaceId,
            ["run_id"] = plan.Record.RunId,
            ["session_id"] = plan.Record.SessionId,
            ["host_id"] = HostId,
            ["title"] = plan.Record.Title,
            ["profile_id"] = plan.LaunchProfile.ProfileId,
        };

        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            await RunTmuxAsync(plan.Record, ["set-option", "-t", plan.Record.TmuxSessionName, LabelPrefix + pair.Key, pair.Value], cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyDictionary<string, string> BuildDockerProcessEnvironment(PiDockerLaunchProfile profile)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in profile.Environment)
            environment[pair.Key] = pair.Value;
        if (!environment.ContainsKey("DOCKER_HOST"))
        {
            var dockerHost = !string.IsNullOrWhiteSpace(profile.DockerHost)
                ? profile.DockerHost
                : _options.DockerHost;
            if (!string.IsNullOrWhiteSpace(dockerHost))
                environment["DOCKER_HOST"] = dockerHost.Trim();
        }
        return environment;
    }

    private async Task<string?> DetectLaunchOutputFailureAsync(PiSessionRecord session, CancellationToken cancellationToken)
    {
        var elapsed = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await CaptureOutputTailAsync(session, cancellationToken).ConfigureAwait(false);
            if (TryDetectDockerLaunchFailure(output?.Tail) is { } outputFailure)
                return outputFailure;

            if (elapsed >= LaunchFailureDetectionWindow)
                return null;

            var delay = LaunchFailureDetectionWindow - elapsed;
            if (delay > LaunchFailureDetectionPollInterval)
                delay = LaunchFailureDetectionPollInterval;
            await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
            elapsed += delay;
        }
    }

    private async Task<PiSessionOutputTail?> CaptureOutputTailAsync(PiSessionRecord session, CancellationToken cancellationToken)
    {
        var capture = await RunTmuxAsync(session, [
            "capture-pane",
            "-p",
            "-t", session.TmuxSessionName,
            "-S", $"-{OutputTailLineCount + 1}"
        ], cancellationToken).ConfigureAwait(false);
        if (!capture.Succeeded)
            return null;

        var normalized = capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', '\r');
        var lineTruncated = false;
        if (normalized.Length > 0)
        {
            var lines = normalized.Split('\n');
            if (lines.Length > OutputTailLineCount)
            {
                lineTruncated = true;
                normalized = string.Join("\n", lines.Skip(lines.Length - OutputTailLineCount));
            }
        }

        var charTruncated = normalized.Length > OutputTailMaxChars;
        if (charTruncated)
            normalized = normalized[^OutputTailMaxChars..];

        return new PiSessionOutputTail(
            normalized,
            _utcNow(),
            lineTruncated || charTruncated,
            ComputeSha256(normalized));
    }

    private async Task<PiSessionContainerObservation?> InspectContainerAsync(PiSessionRecord session, string? outputTail, CancellationToken cancellationToken)
    {
        if (TryDetectDockerLaunchFailure(outputTail) is { } outputFailure)
        {
            return new PiSessionContainerObservation(
                PiSessionStates.Failed,
                outputFailure,
                session.ContainerId,
                session.ContainerName);
        }

        if (string.IsNullOrWhiteSpace(session.ContainerName))
            return null;

        var result = await _runner.RunAsync(
            _options.DockerExecutable,
            ["inspect", "--format", "{{.Id}}\t{{.State.Status}}\t{{.State.ExitCode}}\t{{.State.Error}}", session.ContainerName],
            _commandTimeout,
            cancellationToken,
            BuildDockerProcessEnvironment(session)).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var error = TrimError(string.Join("\n", new[] { result.Stderr, result.Stdout }.Where(value => !string.IsNullOrWhiteSpace(value))));
            if (IsContainerMissingError(error) && IsWithinContainerStartupGracePeriod(session))
                return null;

            var reason = IsContainerMissingError(error)
                ? $"Expected Docker container '{session.ContainerName}' was not found after launch."
                : $"Docker container status check failed for '{session.ContainerName}': {error}";
            return new PiSessionContainerObservation(
                PiSessionStates.Failed,
                TrimError(reason),
                session.ContainerId,
                session.ContainerName);
        }

        return ParseDockerInspectOutput(session, result.Stdout);
    }

    private PiSessionContainerObservation? ParseDockerInspectOutput(PiSessionRecord session, string stdout)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
            return new PiSessionContainerObservation(PiSessionStates.Failed, $"Docker container status check for '{session.ContainerName}' returned no status output.", session.ContainerId, session.ContainerName);

        var parts = line.Split('\t');
        var containerId = parts.ElementAtOrDefault(0);
        var status = parts.ElementAtOrDefault(1);
        var exitCodeText = parts.ElementAtOrDefault(2);
        var stateError = parts.ElementAtOrDefault(3);
        if (string.IsNullOrWhiteSpace(status))
            return new PiSessionContainerObservation(PiSessionStates.Failed, $"Docker container status check for '{session.ContainerName}' returned an empty status.", NormalizeText(containerId) ?? session.ContainerId, session.ContainerName);

        if (status.Equals("running", StringComparison.OrdinalIgnoreCase)
            || status.Equals("created", StringComparison.OrdinalIgnoreCase)
            || status.Equals("restarting", StringComparison.OrdinalIgnoreCase)
            || status.Equals("paused", StringComparison.OrdinalIgnoreCase))
        {
            return new PiSessionContainerObservation(PiSessionStates.Running, null, NormalizeText(containerId) ?? session.ContainerId, session.ContainerName);
        }

        var exitCode = int.TryParse(exitCodeText, out var parsedExitCode) ? parsedExitCode : (int?)null;
        var state = status.Equals("exited", StringComparison.OrdinalIgnoreCase) && exitCode == 0
            ? PiSessionStates.Completed
            : PiSessionStates.Failed;
        var reason = state == PiSessionStates.Completed
            ? $"Docker container '{session.ContainerName}' exited with code 0."
            : $"Docker container '{session.ContainerName}' is {status}{(exitCode is null ? string.Empty : $" with exit code {exitCode}")}{(string.IsNullOrWhiteSpace(stateError) ? string.Empty : $": {stateError}")}.";
        return new PiSessionContainerObservation(state, TrimError(reason), NormalizeText(containerId) ?? session.ContainerId, session.ContainerName);
    }

    private IReadOnlyDictionary<string, string> BuildDockerProcessEnvironment(PiSessionRecord session)
    {
        var profile = DeserializeProfile(session);
        if (profile is not null)
            return BuildDockerProcessEnvironment(profile);

        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_options.DockerHost))
            environment["DOCKER_HOST"] = _options.DockerHost.Trim();
        return environment;
    }

    private static PiDockerLaunchProfile? DeserializeProfile(PiSessionRecord session)
    {
        try
        {
            return JsonSerializer.Deserialize<PiDockerLaunchProfile>(session.LaunchProfileJson, PiSessionJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool IsWithinContainerStartupGracePeriod(PiSessionRecord session)
    {
        var launchedAt = session.StartedAt ?? session.CreatedAt;
        if (launchedAt == default)
            return false;
        return _utcNow() - launchedAt.ToUniversalTime() < ContainerStartupGracePeriod;
    }

    private static bool IsContainerMissingError(string error) =>
        error.Contains("No such object", StringComparison.OrdinalIgnoreCase)
        || error.Contains("No such container", StringComparison.OrdinalIgnoreCase);

    private static string? TryDetectDockerLaunchFailure(string? outputTail)
    {
        if (string.IsNullOrWhiteSpace(outputTail))
            return null;

        var failureLine = outputTail
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => ContainsAny(line,
                "permission denied while trying to connect to the docker api",
                "permission denied while trying to connect to the docker daemon socket",
                "cannot connect to the docker daemon",
                "is the docker daemon running?"));
        return failureLine is null ? null : TrimError($"Docker launch command failed: {failureLine}");
    }

    private Task<ProcessRunResult> RunTmuxAsync(PiSessionRecord session, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var tmuxArgs = new List<string>(args.Count + 2)
        {
            "-L",
            TmuxSocketName(session),
        };
        tmuxArgs.AddRange(args);
        return _runner.RunAsync(_options.TmuxExecutable, tmuxArgs, _commandTimeout, cancellationToken, BuildTmuxProcessEnvironment());
    }

    private static IReadOnlyDictionary<string, string> BuildTmuxProcessEnvironment() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // den-mcp/docker-rt are service accounts with /usr/sbin/nologin shells.
            // tmux derives its default shell from SHELL/passwd and may use it to
            // start pane commands; force a real shell for deterministic detached sessions.
            ["SHELL"] = "/bin/sh",
        };

    private static string TmuxSocketName(PiSessionRecord session)
    {
        var source = string.IsNullOrWhiteSpace(session.SessionId)
            ? session.TmuxSessionName
            : session.SessionId;
        var builder = new StringBuilder("den-mcp-");
        foreach (var ch in source)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }
        return builder.ToString();
    }

    private static IReadOnlyList<string> NormalizeTmuxShellCommand(IEnumerable<string>? values)
    {
        var normalized = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray() ?? [];
        return normalized.Length > 0
            ? normalized
            : PiDockerLaunchProfileDefaults.TmuxShellCommand;
    }

    private async Task<string?> ProvisionWorkspaceAsync(PiDockerLaunchProfile profile, CancellationToken cancellationToken)
    {
        var sourceDir = NormalizeText(profile.WorkspaceSourceProjectDir);
        if (sourceDir is null)
            return null;

        try
        {
            if (!Directory.Exists(sourceDir))
                return $"Workspace source project dir '{sourceDir}' does not exist.";

            var targetProjectDir = Path.Combine(profile.DevDir, profile.ProjectId);
            if (Directory.Exists(Path.Combine(targetProjectDir, ".git")))
            {
                NormalizeWorkspacePermissions(profile.DevDir);
                return null;
            }

            Directory.CreateDirectory(profile.DevDir);
            if (Directory.Exists(targetProjectDir))
                Directory.Delete(targetProjectDir, recursive: true);

            var branch = NormalizeText(profile.WorkspaceBranch) ?? "main";
            var bundlePath = Path.Combine(profile.DevDir, $".den-source-{profile.SessionId}.bundle");
            if (File.Exists(bundlePath))
                File.Delete(bundlePath);

            var safeEnvironment = BuildGitSafeDirectoryEnvironment(sourceDir);
            var bundle = await _runner.RunAsync(
                "git",
                ["-C", sourceDir, "bundle", "create", bundlePath, $"refs/heads/{branch}"],
                TimeSpan.FromSeconds(120),
                cancellationToken,
                safeEnvironment).ConfigureAwait(false);
            if (!bundle.Succeeded)
                return $"Workspace provisioning failed while bundling '{sourceDir}' branch '{branch}': {TrimError(bundle.Stderr)}";

            var clone = await _runner.RunAsync(
                "git",
                ["clone", "-b", branch, bundlePath, targetProjectDir],
                TimeSpan.FromSeconds(120),
                cancellationToken).ConfigureAwait(false);
            if (!clone.Succeeded)
                return $"Workspace provisioning failed while cloning bundle for '{sourceDir}': {TrimError(clone.Stderr)}";

            var remoteUrl = await _runner.RunAsync(
                "git",
                ["-C", sourceDir, "remote", "get-url", "origin"],
                TimeSpan.FromSeconds(15),
                cancellationToken,
                safeEnvironment).ConfigureAwait(false);
            if (remoteUrl.Succeeded && NormalizeText(remoteUrl.Stdout) is { } origin)
            {
                await _runner.RunAsync(
                    "git",
                    ["-C", targetProjectDir, "remote", "set-url", "origin", origin],
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);
            }

            File.Delete(bundlePath);
            NormalizeWorkspacePermissions(profile.DevDir);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"Workspace for DEV_DIR '{profile.DevDir}' could not be provisioned from '{sourceDir}': {ex.Message}";
        }
    }

    private static IReadOnlyDictionary<string, string> BuildGitSafeDirectoryEnvironment(string sourceDir)
    {
        var gitDir = Path.Combine(sourceDir, ".git");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "safe.directory",
            ["GIT_CONFIG_VALUE_0"] = sourceDir,
            ["GIT_CONFIG_KEY_1"] = "safe.directory",
            ["GIT_CONFIG_VALUE_1"] = gitDir,
        };
    }

    private static void NormalizeWorkspacePermissions(string devDir)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(devDir))
            return;

        SetDirectoryMode(devDir);
        foreach (var directory in Directory.EnumerateDirectories(devDir, "*", SearchOption.AllDirectories))
            SetDirectoryMode(directory);
        foreach (var file in Directory.EnumerateFiles(devDir, "*", SearchOption.AllDirectories))
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
            var current = File.GetUnixFileMode(file);
            if ((current & UnixFileMode.UserExecute) != 0)
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(file, mode);
        }
    }

    private string? ProvisionPiState(PiDockerLaunchProfile profile)
    {
        try
        {
            Directory.CreateDirectory(profile.PiStateDir);

            var requiredPaths = NormalizeRequiredPiStatePaths(_options.RequiredPiStatePaths);
            var missingRequired = requiredPaths
                .Where(path => !File.Exists(Path.Combine(profile.PiStateDir, path)) && !Directory.Exists(Path.Combine(profile.PiStateDir, path)))
                .ToList();
            if (missingRequired.Count > 0)
            {
                var sourceDir = NormalizeText(profile.PiStateSourceDir);
                if (sourceDir is not null && Directory.Exists(sourceDir) && !SameDirectory(sourceDir, profile.PiStateDir))
                {
                    CopyDirectoryContents(sourceDir, profile.PiStateDir);
                }
            }

            NormalizePiStatePermissions(profile.PiStateDir);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"PI_STATE_DIR '{profile.PiStateDir}' could not be provisioned from configured source '{profile.PiStateSourceDir ?? "<none>"}': {ex.Message}";
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string targetDir)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Copy(file, target);
        }
    }

    private static void NormalizePiStatePermissions(string piStateDir)
    {
        if (!OperatingSystem.IsLinux())
            return;

        SetDirectoryMode(piStateDir);
        foreach (var directory in Directory.EnumerateDirectories(piStateDir, "*", SearchOption.AllDirectories))
            SetDirectoryMode(directory);
        foreach (var file in Directory.EnumerateFiles(piStateDir, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
    }

    [SupportedOSPlatform("linux")]
    private static void SetDirectoryMode(string directory) =>
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute |
            UnixFileMode.SetGroup);

    private static bool SameDirectory(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);

    private string? ValidatePiState(PiDockerLaunchProfile profile)
    {
        var requiredPaths = NormalizeRequiredPiStatePaths(_options.RequiredPiStatePaths);
        if (requiredPaths.Count == 0)
            return null;

        if (!Directory.Exists(profile.PiStateDir))
        {
            return $"PI_STATE_DIR '{profile.PiStateDir}' does not exist or is not mounted; Den-owned Pi sessions require Pi settings/auth state in PI_STATE_DIR and do not fall back to provider environment secrets (required: {string.Join(", ", requiredPaths)}).";
        }

        var missing = requiredPaths
            .Where(path => !File.Exists(Path.Combine(profile.PiStateDir, path)) && !Directory.Exists(Path.Combine(profile.PiStateDir, path)))
            .ToList();
        if (missing.Count > 0)
        {
            return $"PI_STATE_DIR '{profile.PiStateDir}' is missing required Pi settings/auth state path(s): {string.Join(", ", missing)}. Den-owned Pi sessions do not fall back to provider environment secrets.";
        }

        return null;
    }

    private static IReadOnlyList<string> NormalizeRequiredPiStatePaths(IEnumerable<string>? values)
    {
        var normalized = new List<string>();
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim().Replace('\\', '/') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (Path.IsPathRooted(value) || value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
                throw new InvalidOperationException($"required_pi_state_paths must contain relative paths under PI_STATE_DIR; invalid value '{raw}'.");
            normalized.Add(value);
        }
        return normalized;
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static DateTime? FromUnixSeconds(string value) =>
        long.TryParse(value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TrimError(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "unknown host error" : value.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[..2000];
    }

    /// <summary>
    /// Renders a typed argv vector as a shell line only for tmux's interactive pane.
    /// The recorded command remains the argv vector and all host process invocations
    /// use <see cref="IProcessRunner"/> argument lists.
    /// </summary>
    internal static string RenderShellCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new InvalidOperationException("Launch command must not be empty.");
        return string.Join(" ", args.Select(PosixQuote));
    }

    private static string PosixQuote(string value)
    {
        if (value.Length == 0)
            return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or ','))
            return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

internal sealed record PiSessionOutputTail(string Tail, DateTime CapturedAt, bool Truncated, string TailSha256);

internal sealed record PiSessionContainerObservation(string State, string? StateReason, string? ContainerId, string? ContainerName);

internal static class PiSessionContainerNames
{
    public static string? Extract(PiDockerLaunchProfile profile)
    {
        for (var i = 0; i < profile.DockerComposeRunArgs.Count - 1; i++)
        {
            if (profile.DockerComposeRunArgs[i] == "--name")
                return profile.DockerComposeRunArgs[i + 1];
        }
        return $"{profile.ComposeProjectName}-{profile.Service}";
    }
}

public static class PiSessionNaming
{
    public static string NewSessionId() => $"pi-{Guid.NewGuid():N}";

    public static string CreateTmuxSessionName(string projectId, string sessionId)
    {
        var slug = SafeSlug($"den-pi-{projectId}-{sessionId}");
        var suffix = ShortHash($"{projectId}:{sessionId}");
        var maxPrefixLength = 60 - suffix.Length - 1;
        if (slug.Length > maxPrefixLength)
            slug = slug[..maxPrefixLength].Trim('-', '.', '_');
        return $"{slug}-{suffix}";
    }

    private static string SafeSlug(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (c is '-' or '_' or '.') builder.Append(c);
            else builder.Append('-');
        }
        var slug = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(slug) ? "den-pi-session" : slug;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
