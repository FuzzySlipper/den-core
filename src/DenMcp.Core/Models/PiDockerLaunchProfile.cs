namespace DenMcp.Core.Models;

/// <summary>
/// Server-owned configuration for rendering a Docker Compose based Pi launch profile.
/// This is a launch-profile contract only; process lifecycle is owned by later Pi
/// session host work.
/// </summary>
public sealed class PiDockerLaunchProfileOptions
{
    public string ComposeFile { get; set; } = "/data/services/den-mcp/pi-docker/compose.yaml";
    public string Service { get; set; } = "pi";
    public string DevDir { get; set; } = "/data/dev";
    public bool UsePerSessionWorkspace { get; set; } = true;
    public string WorkspaceRootDir { get; set; } = "/data/services/den-mcp/pi-workspaces";
    public string PiStateRootDir { get; set; } = "/data/services/den-mcp/pi-sessions";
    public string? PiStateSourceDir { get; set; } = "/data/services/pi";
    public string Image { get; set; } = "pi-sandbox:latest";
    public string PiVersion { get; set; } = "0.71.0";
    public string NodeVersion { get; set; } = "22";
    public int SandboxUid { get; set; } = 1000;
    public int SandboxGid { get; set; } = 1000;
    public string? GitConfigPath { get; set; }
    public string? SshDir { get; set; }
    public string? GhConfigDir { get; set; }
    public string CredentialFallbackRootDir { get; set; } = "/data/services/den-mcp/pi-credential-fallbacks";
    public string HostCallbackBindAddress { get; set; } = PiDockerLaunchProfileDefaults.LoopbackAddress;
    public string? HostId { get; set; }
    public string TmuxExecutable { get; set; } = "/usr/bin/tmux";
    public string[] TmuxShellCommand { get; set; } = PiDockerLaunchProfileDefaults.TmuxShellCommand.ToArray();
    public string DockerExecutable { get; set; } = "/usr/bin/docker";
    public string? DockerHost { get; set; }
    public bool ScrubProviderEnvironmentVariables { get; set; } = true;
    public string[] ProviderSecretEnvironmentVariables { get; set; } = PiDockerLaunchProfileDefaults.ProviderSecretEnvironmentVariables.ToArray();
    public string[] RequiredPiStatePaths { get; set; } = ["agent/settings.json"];
}

public sealed class PiDockerLaunchRenderRequest
{
    public required string ProjectId { get; init; }
    public required string SessionId { get; init; }
    public long? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public string? DevDir { get; init; }
    public string? WorkspaceRootDir { get; init; }
    public string? PiStateDir { get; init; }
    public string? PiStateSourceDir { get; init; }
    public string? ComposeFile { get; init; }
    public string? Service { get; init; }
    public string? Image { get; init; }
    public string? PiVersion { get; init; }
    public string? NodeVersion { get; init; }
    public string? GitConfigPath { get; init; }
    public string? SshDir { get; init; }
    public string? GhConfigDir { get; init; }
    public IReadOnlyList<PiDockerCallbackPort> CallbackPorts { get; init; } = [];
    public string? WorkerRole { get; init; }
    public string? WorkerRunId { get; init; }
    public int? PromptPacketMessageId { get; init; }
    public string? StateFileRef { get; init; }
    public string? StartupPrompt { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class PiDockerCallbackPort
{
    public int HostPort { get; init; }
    public int ContainerPort { get; init; }
    public string BindAddress { get; init; } = PiDockerLaunchProfileDefaults.LoopbackAddress;
}

public sealed class PiDockerLaunchProfile
{
    public required string ProfileId { get; init; }
    public required string ProjectId { get; init; }
    public required string SessionId { get; init; }
    public long? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public required string ComposeProjectName { get; init; }
    public required string ComposeFile { get; init; }
    public required string Service { get; init; }
    public required string DevDir { get; init; }
    public string? WorkspaceSourceProjectDir { get; init; }
    public string? WorkspaceBranch { get; init; }
    public required string PiStateDir { get; init; }
    public string? PiStateSourceDir { get; init; }
    public required string Image { get; init; }
    public required string PiVersion { get; init; }
    public required string NodeVersion { get; init; }
    public string? DockerHost { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> ScrubbedEnvironmentVariables { get; init; } = [];
    public IReadOnlyList<PiDockerVolumeMount> VolumeMounts { get; init; } = [];
    public IReadOnlyList<PiDockerCallbackPort> CallbackPorts { get; init; } = [];
    public IReadOnlyList<string> DockerComposeConfigArgs { get; init; } = [];
    public IReadOnlyList<string> DockerComposeBuildArgs { get; init; } = [];
    public IReadOnlyList<string> DockerComposeRunArgs { get; init; } = [];
    public IReadOnlyList<string> CacheVolumeNames { get; init; } = [];
    public IReadOnlyList<string> RequiredHostPaths { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> KnownLimitations { get; init; } = [];
    public string? WorkerRole { get; init; }
    public string? WorkerRunId { get; init; }
    public int? PromptPacketMessageId { get; init; }
    public string? StateFileRef { get; init; }
    public string? StartupPrompt { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class PiDockerVolumeMount
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool ReadOnly { get; init; }
    public required string Purpose { get; init; }
}

public static class PiDockerLaunchProfileDefaults
{
    public const string LoopbackAddress = "127.0.0.1";
    public const string ContainerDevDir = "/home/pi/dev";
    public const string ContainerPiStateDir = "/home/pi/.pi";
    public const string ContainerGitConfigPath = "/home/pi/.gitconfig";
    public const string ContainerSshDir = "/home/pi/.ssh";
    public const string ContainerGhConfigDir = "/home/pi/.config/gh";
    public const string CacheVolume = "pi-cache";
    public const string NpmCacheVolume = "pi-npm-cache";

    /// <summary>Explicit tmux pane shell argv; avoids service-account passwd shells such as nologin.</summary>
    public static readonly IReadOnlyList<string> TmuxShellCommand = ["/bin/sh", "-i"];

    /// <summary>Provider/model credentials intentionally blanked for Den-owned Docker launches.</summary>
    public static readonly IReadOnlyList<string> ProviderSecretEnvironmentVariables = [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "OPENAI_API_KEY",
        "OPENAI_ORG_ID",
        "OPENAI_PROJECT_ID",
        "GEMINI_API_KEY",
        "GOOGLE_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "MISTRAL_API_KEY",
        "GROQ_API_KEY",
        "OPENROUTER_API_KEY",
        "AWS_PROFILE",
        "AWS_REGION",
        "AWS_DEFAULT_REGION",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_API_KEY",
        "COHERE_API_KEY",
        "TOGETHER_API_KEY",
        "XAI_API_KEY",
        "DEEPSEEK_API_KEY",
        "PERPLEXITY_API_KEY",
    ];

    /// <summary>Callback ports preserved from the inspected pi-docker compose service.</summary>
    public static readonly IReadOnlyList<int> CallbackContainerPorts = [1455, 53692, 8085, 51121];
}
