using System.Security.Cryptography;
using System.Text;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IPiDockerLaunchProfileRenderer
{
    PiDockerLaunchProfile Render(PiDockerLaunchRenderRequest request);
}

/// <summary>
/// Renders the effective Docker Compose inputs for a Den-owned Pi launch without
/// starting processes. The lifecycle API can consume this contract later without
/// duplicating Docker/Compose policy.
/// </summary>
public sealed class PiDockerLaunchProfileRenderer(PiDockerLaunchProfileOptions options) : IPiDockerLaunchProfileRenderer
{
    public PiDockerLaunchProfile Render(PiDockerLaunchRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectId = RequireIdentifier(request.ProjectId, "project_id");
        var sessionId = RequireIdentifier(request.SessionId, "session_id");
        var composeFile = ResolveConfiguredPath(request.ComposeFile, options.ComposeFile, "compose_file");
        var service = RequireIdentifier(request.Service ?? options.Service, "service");
        var sourceDevDir = ResolveConfiguredPath(request.DevDir, options.DevDir, "dev_dir");
        var workspaceRoot = ResolveOptionalPath(request.WorkspaceRootDir ?? options.WorkspaceRootDir);
        var usePerSessionWorkspace = options.UsePerSessionWorkspace
            && request.DevDir is null
            && workspaceRoot is not null;
        var devDir = usePerSessionWorkspace
            ? Path.Combine(workspaceRoot!, sessionId, "dev")
            : sourceDevDir;
        var workspaceSourceProjectDir = usePerSessionWorkspace
            ? Path.Combine(sourceDevDir, projectId)
            : null;
        var piStateDir = ResolveStateDir(request.PiStateDir, sessionId);
        var piStateSourceDir = ResolveOptionalPath(request.PiStateSourceDir ?? options.PiStateSourceDir);
        var image = RequireNonEmpty(request.Image ?? options.Image, "image");
        var piVersion = RequireNonEmpty(request.PiVersion ?? options.PiVersion, "pi_version");
        var nodeVersion = RequireNonEmpty(request.NodeVersion ?? options.NodeVersion, "node_version");
        var dockerHost = NormalizeDockerHost(options.DockerHost);
        var callbackBindAddress = RequireLoopbackBindAddress(options.HostCallbackBindAddress);
        var callbackPorts = ValidateCallbackPorts(request.CallbackPorts, callbackBindAddress);
        var startupPrompt = NormalizeStartupPrompt(request.StartupPrompt);
        var composeProjectName = BuildComposeProjectName(projectId, sessionId);
        var profileId = $"den-pi-docker:{composeProjectName}";
        var scrubbedEnvironmentVariables = options.ScrubProviderEnvironmentVariables
            ? NormalizeEnvironmentVariableNames(options.ProviderSecretEnvironmentVariables, nameof(options.ProviderSecretEnvironmentVariables))
            : [];

        var gitConfigPath = ResolveOptionalPath(request.GitConfigPath ?? options.GitConfigPath);
        var sshDir = ResolveOptionalPath(request.SshDir ?? options.SshDir);
        var ghConfigDir = ResolveOptionalPath(request.GhConfigDir ?? options.GhConfigDir);
        var credentialFallbackRoot = ResolveConfiguredPath(null, options.CredentialFallbackRootDir, "credential_fallback_root_dir");
        gitConfigPath ??= Path.Combine(credentialFallbackRoot, "gitconfig");
        sshDir ??= Path.Combine(credentialFallbackRoot, "ssh");
        ghConfigDir ??= Path.Combine(credentialFallbackRoot, "gh");

        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["DEV_DIR"] = devDir,
            ["PI_STATE_DIR"] = piStateDir,
            ["PI_SANDBOX_IMAGE"] = image,
            ["PI_VERSION"] = piVersion,
            ["NODE_VERSION"] = nodeVersion,
            ["PI_SANDBOX_UID"] = options.SandboxUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PI_SANDBOX_GID"] = options.SandboxGid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PI_GITCONFIG_PATH"] = gitConfigPath,
            ["PI_SSH_DIR"] = sshDir,
            ["PI_GH_CONFIG_DIR"] = ghConfigDir,
            ["DEN_WORKER_PROJECT_ID"] = projectId,
            ["DEN_WORKER_SESSION_ID"] = sessionId,
        };
        if (request.TaskId is not null)
            environment["DEN_WORKER_TASK_ID"] = request.TaskId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.WorkerRole))
            environment["DEN_WORKER_ROLE"] = request.WorkerRole.Trim();
        if (!string.IsNullOrWhiteSpace(request.WorkerRunId))
            environment["DEN_WORKER_RUN_ID"] = request.WorkerRunId.Trim();
        if (request.PromptPacketMessageId is not null)
            environment["DEN_WORKER_PROMPT_PACKET_MESSAGE_ID"] = request.PromptPacketMessageId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.StateFileRef))
            environment["DEN_WORKER_STATE_FILE_REF"] = request.StateFileRef.Trim();
        if (startupPrompt is not null)
            environment["DEN_WORKER_STARTUP_PROMPT"] = startupPrompt;
        if (request.TimeoutSeconds is not null)
            environment["DEN_WORKER_TIMEOUT_SECONDS"] = request.TimeoutSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (dockerHost is not null)
            environment["DOCKER_HOST"] = dockerHost;

        foreach (var name in scrubbedEnvironmentVariables)
        {
            if (environment.ContainsKey(name))
                throw new InvalidOperationException($"provider secret environment variable '{name}' conflicts with a required Pi launch environment variable.");
            environment[name] = string.Empty;
        }

        var volumeMounts = new List<PiDockerVolumeMount>
        {
            new() { Source = devDir, Target = PiDockerLaunchProfileDefaults.ContainerDevDir, ReadOnly = false, Purpose = "broad_dev_dir" },
            new() { Source = piStateDir, Target = PiDockerLaunchProfileDefaults.ContainerPiStateDir, ReadOnly = false, Purpose = "pi_state" },
            new() { Source = $"{composeProjectName}_{PiDockerLaunchProfileDefaults.CacheVolume}", Target = "/home/pi/.cache", ReadOnly = false, Purpose = "cache_volume" },
            new() { Source = $"{composeProjectName}_{PiDockerLaunchProfileDefaults.NpmCacheVolume}", Target = "/home/pi/.npm", ReadOnly = false, Purpose = "npm_cache_volume" },
            new() { Source = gitConfigPath, Target = PiDockerLaunchProfileDefaults.ContainerGitConfigPath, ReadOnly = true, Purpose = "gitconfig_credentials" },
            new() { Source = sshDir, Target = PiDockerLaunchProfileDefaults.ContainerSshDir, ReadOnly = true, Purpose = "ssh_credentials" },
            new() { Source = ghConfigDir, Target = PiDockerLaunchProfileDefaults.ContainerGhConfigDir, ReadOnly = true, Purpose = "gh_credentials" },
        };

        var composePrefix = new List<string>
        {
            "compose",
            "--project-name", composeProjectName,
            "-f", composeFile,
        };

        var configArgs = composePrefix.Concat(["config"]).ToList();
        var buildArgs = composePrefix.Concat(["build", service]).ToList();
        var runArgs = composePrefix.Concat(["run", "--name", $"{composeProjectName}-{service}"]).ToList();
        foreach (var port in callbackPorts)
        {
            runArgs.Add("--publish");
            runArgs.Add($"{port.BindAddress}:{port.HostPort}:{port.ContainerPort}");
        }

        foreach (var pair in environment.Where(pair => pair.Key.StartsWith("DEN_WORKER_", StringComparison.Ordinal)))
        {
            runArgs.Add("--env");
            runArgs.Add(pair.Key);
        }

        runArgs.Add(service);
        if (startupPrompt is not null)
        {
            runArgs.Add("/bin/sh");
            runArgs.Add("-lc");
            runArgs.Add("exec pi -p \"$DEN_WORKER_STARTUP_PROMPT\"");
        }

        var warnings = new List<string>();
        if (request.PiStateDir is null)
        {
            warnings.Add("PI_STATE_DIR is derived per session from PiStateRootDir; configure/populate that session state before launch or override PiStateDir to reuse an existing Pi auth/config namespace.");
        }

        if (scrubbedEnvironmentVariables.Count > 0)
            warnings.Add($"Provider/model credential environment variables are scrubbed to empty for Docker Compose interpolation: {string.Join(", ", scrubbedEnvironmentVariables)}. Pi credentials must come from the mounted PI_STATE_DIR.");
        else
            warnings.Add("Provider/model credential environment variable scrubbing is disabled; Den-owned Pi sessions may inherit server process model credentials.");

        if (dockerHost is not null)
            warnings.Add($"Docker CLI calls for launch and cleanup use explicit DOCKER_HOST '{dockerHost}'.");
        else
            warnings.Add("DOCKER_HOST is not configured; Docker CLI calls will use the Docker client's default daemon socket.");

        AddPiStateWarnings(warnings, piStateDir, NormalizeRequiredPiStatePaths(options.RequiredPiStatePaths));

        if (request.GitConfigPath is null && options.GitConfigPath is null)
            warnings.Add("PI_GITCONFIG_PATH is using the configured empty fallback; no host git config is exposed unless configured.");
        if (request.SshDir is null && options.SshDir is null)
            warnings.Add("PI_SSH_DIR is using the configured empty fallback; no host SSH directory is exposed unless configured.");
        if (request.GhConfigDir is null && options.GhConfigDir is null)
            warnings.Add("PI_GH_CONFIG_DIR is using the configured empty fallback; no host gh config is exposed unless configured.");

        return new PiDockerLaunchProfile
        {
            ProfileId = profileId,
            ProjectId = projectId,
            SessionId = sessionId,
            TaskId = request.TaskId,
            WorkspaceId = NullIfWhiteSpace(request.WorkspaceId),
            Title = NullIfWhiteSpace(request.Title),
            ComposeProjectName = composeProjectName,
            ComposeFile = composeFile,
            Service = service,
            DevDir = devDir,
            WorkspaceSourceProjectDir = workspaceSourceProjectDir,
            WorkspaceBranch = usePerSessionWorkspace ? "main" : null,
            PiStateDir = piStateDir,
            PiStateSourceDir = piStateSourceDir,
            Image = image,
            PiVersion = piVersion,
            NodeVersion = nodeVersion,
            DockerHost = dockerHost,
            Environment = environment,
            ScrubbedEnvironmentVariables = scrubbedEnvironmentVariables,
            VolumeMounts = volumeMounts,
            CallbackPorts = callbackPorts,
            DockerComposeConfigArgs = configArgs,
            DockerComposeBuildArgs = buildArgs,
            DockerComposeRunArgs = runArgs,
            CacheVolumeNames = [$"{composeProjectName}_{PiDockerLaunchProfileDefaults.CacheVolume}", $"{composeProjectName}_{PiDockerLaunchProfileDefaults.NpmCacheVolume}"],
            RequiredHostPaths = [devDir, piStateDir, gitConfigPath, sshDir, ghConfigDir],
            Warnings = warnings,
            KnownLimitations = [
                "session_id must be unique for each live launch; reusing a session_id intentionally reuses Compose names and PI_STATE_DIR.",
                "The renderer validates loopback binding and duplicate ports in a single profile, but it does not probe or reserve host ports; the lifecycle API must allocate unique host callback ports before launch.",
                usePerSessionWorkspace
                    ? "DEV_DIR is a per-session workspace provisioned from the source project before launch; worker writes do not modify the shared source checkout directly."
                    : "DEV_DIR uses the configured broad dev directory directly; host permissions must allow the container user to write if worker code changes are expected."
            ],
            WorkerRole = NullIfWhiteSpace(request.WorkerRole),
            WorkerRunId = NullIfWhiteSpace(request.WorkerRunId),
            PromptPacketMessageId = request.PromptPacketMessageId,
            StateFileRef = NullIfWhiteSpace(request.StateFileRef),
            StartupPrompt = startupPrompt,
            TimeoutSeconds = request.TimeoutSeconds,
        };
    }


    private static string? NormalizeStartupPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > 4000)
            throw new InvalidOperationException("startup_prompt must be a bounded packet-reference prompt (max 4000 characters); do not embed large task context in launch environment.");
        return normalized;
    }

    private static IReadOnlyList<PiDockerCallbackPort> ValidateCallbackPorts(IReadOnlyList<PiDockerCallbackPort>? callbackPorts, string callbackBindAddress)
    {
        if (callbackPorts is null || callbackPorts.Count == 0)
        {
            throw new InvalidOperationException("callback_ports must be provided per session so concurrent launches do not silently reuse static host ports.");
        }

        var hostPorts = new HashSet<int>();
        var normalized = new List<PiDockerCallbackPort>();
        foreach (var port in callbackPorts)
        {
            ValidatePort(port.HostPort, "callback_ports.host_port");
            ValidatePort(port.ContainerPort, "callback_ports.container_port");
            if (!string.Equals(port.BindAddress, callbackBindAddress, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"callback_ports.bind_address must be '{callbackBindAddress}' (host loopback only).");
            }

            if (!hostPorts.Add(port.HostPort))
            {
                throw new InvalidOperationException($"callback_ports contains duplicate host port {port.HostPort}.");
            }

            normalized.Add(new PiDockerCallbackPort
            {
                HostPort = port.HostPort,
                ContainerPort = port.ContainerPort,
                BindAddress = port.BindAddress,
            });
        }

        return normalized;
    }

    private static void ValidatePort(int port, string field)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{field} must be between 1 and 65535.");
        }
    }

    private static IReadOnlyList<string> NormalizeEnvironmentVariableNames(IEnumerable<string>? values, string field)
    {
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in values ?? [])
        {
            var value = RequireNonEmpty(raw, field).ToUpperInvariant();
            if (!IsEnvironmentVariableName(value))
                throw new InvalidOperationException($"{field} contains invalid environment variable name '{raw}'.");
            normalized.Add(value);
        }
        return normalized.ToList();
    }

    private static bool IsEnvironmentVariableName(string value) =>
        value.Length > 0
        && (value[0] is >= 'A' and <= 'Z' or '_')
        && value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

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

    private static void AddPiStateWarnings(List<string> warnings, string piStateDir, IReadOnlyList<string> requiredPiStatePaths)
    {
        if (requiredPiStatePaths.Count == 0)
        {
            warnings.Add("No required Pi state files are configured; launch cannot preflight whether mounted Pi settings/auth state exists.");
            return;
        }

        if (!Directory.Exists(piStateDir))
        {
            warnings.Add($"PI_STATE_DIR '{piStateDir}' does not exist; launch will fail unless the mounted Pi settings/auth state is created first (required: {string.Join(", ", requiredPiStatePaths)}).");
            return;
        }

        var missing = requiredPiStatePaths
            .Where(path => !File.Exists(Path.Combine(piStateDir, path)) && !Directory.Exists(Path.Combine(piStateDir, path)))
            .ToList();
        if (missing.Count > 0)
            warnings.Add($"PI_STATE_DIR '{piStateDir}' is missing required Pi settings/auth state path(s): {string.Join(", ", missing)}. Den-owned Pi sessions do not fall back to provider environment secrets.");
    }

    private static string? NormalizeDockerHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var dockerHost = value.Trim();
        if (dockerHost.Any(char.IsControl) || dockerHost.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("docker_host must not contain whitespace or control characters.");
        return dockerHost;
    }

    private static string RequireLoopbackBindAddress(string? value)
    {
        var bindAddress = RequireNonEmpty(value, "host_callback_bind_address");
        if (!string.Equals(bindAddress, PiDockerLaunchProfileDefaults.LoopbackAddress, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"host_callback_bind_address must be '{PiDockerLaunchProfileDefaults.LoopbackAddress}' (host loopback only).");
        }

        return bindAddress;
    }

    private string ResolveStateDir(string? overridePath, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ExpandHome(overridePath.Trim());
        }

        var root = ResolveConfiguredPath(null, options.PiStateRootDir, "pi_state_root_dir");
        return Path.Combine(root, SafeSlug(sessionId));
    }

    private static string? ResolveOptionalPath(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ExpandHome(value.Trim());
    }

    private static string ResolveConfiguredPath(string? overrideValue, string configuredValue, string field)
    {
        var value = !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue : configuredValue;
        return ExpandHome(RequireNonEmpty(value, field));
    }

    private static string RequireIdentifier(string? value, string field)
    {
        var required = RequireNonEmpty(value, field);
        if (required.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{field} must not contain whitespace.");
        }

        return required;
    }

    private static string RequireNonEmpty(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{field} is required.");
        }

        return value.Trim();
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return path;
    }

    private static string BuildComposeProjectName(string projectId, string sessionId)
    {
        var slug = SafeSlug($"den-pi-{projectId}-{sessionId}");
        var suffix = ShortHash($"{projectId}:{sessionId}");
        var maxPrefixLength = 50 - suffix.Length - 1;
        if (slug.Length > maxPrefixLength)
        {
            slug = slug[..maxPrefixLength].Trim('-');
        }

        return $"{slug}-{suffix}";
    }

    private static string SafeSlug(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (c is '-' or '_' or '.')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(slug) ? "den-pi-session" : slug;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
