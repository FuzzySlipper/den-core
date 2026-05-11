using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class PiDockerLaunchProfileRendererTests
{
    [Fact]
    public void Options_DefaultsUseDenSrvServiceAccessiblePaths()
    {
        var options = new PiDockerLaunchProfileOptions();

        Assert.Equal("/data/services/den-mcp/pi-docker/compose.yaml", options.ComposeFile);
        Assert.Equal("/data/dev", options.DevDir);
        Assert.True(options.UsePerSessionWorkspace);
        Assert.Equal("/data/services/den-mcp/pi-workspaces", options.WorkspaceRootDir);
        Assert.Equal("/data/services/den-mcp/pi-sessions", options.PiStateRootDir);
        Assert.Equal("/data/services/pi", options.PiStateSourceDir);
        Assert.Equal("/data/services/den-mcp/pi-credential-fallbacks", options.CredentialFallbackRootDir);
        Assert.Equal("/usr/bin/tmux", options.TmuxExecutable);
        Assert.Equal("/usr/bin/docker", options.DockerExecutable);
        Assert.Null(options.DockerHost);
        Assert.All(new[] { options.ComposeFile, options.DevDir, options.PiStateRootDir, options.CredentialFallbackRootDir }, path =>
        {
            Assert.DoesNotContain("~", path, StringComparison.Ordinal);
            Assert.DoesNotContain("/home/patch", path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Render_ReturnsEffectiveComposeSettingsForSession()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/opt/pi-docker/compose.yaml",
            DevDir = "/home/patch/dev",
            UsePerSessionWorkspace = false,
            PiStateRootDir = "/var/lib/den/pi-state",
            Image = "pi-sandbox:test",
            PiVersion = "0.71.0",
            NodeVersion = "22",
            GitConfigPath = "/home/patch/.gitconfig",
            SshDir = "/home/patch/.ssh",
            GhConfigDir = "/home/patch/.config/gh",
            DockerHost = "unix:///run/den-mcp/docker-rt/docker.sock",
        });

        var profile = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
            TaskId = 1188,
            CallbackPorts = [
                new() { HostPort = 21455, ContainerPort = 1455 },
                new() { HostPort = 28085, ContainerPort = 8085 },
            ],
        });

        Assert.Equal("/opt/pi-docker/compose.yaml", profile.ComposeFile);
        Assert.Equal("pi", profile.Service);
        Assert.Equal("/home/patch/dev", profile.Environment["DEV_DIR"]);
        Assert.Equal("/var/lib/den/pi-state/session-a", profile.Environment["PI_STATE_DIR"]);
        Assert.Equal("pi-sandbox:test", profile.Environment["PI_SANDBOX_IMAGE"]);
        Assert.Equal("0.71.0", profile.Environment["PI_VERSION"]);
        Assert.Equal("22", profile.Environment["NODE_VERSION"]);
        Assert.Equal("/home/patch/.gitconfig", profile.Environment["PI_GITCONFIG_PATH"]);
        Assert.Equal("/home/patch/.ssh", profile.Environment["PI_SSH_DIR"]);
        Assert.Equal("/home/patch/.config/gh", profile.Environment["PI_GH_CONFIG_DIR"]);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", profile.DockerHost);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", profile.Environment["DOCKER_HOST"]);

        Assert.Contains(profile.VolumeMounts, m => m.Source == "/home/patch/dev" && m.Target == "/home/pi/dev" && !m.ReadOnly);
        Assert.Contains(profile.VolumeMounts, m => m.Source == "/var/lib/den/pi-state/session-a" && m.Target == "/home/pi/.pi" && !m.ReadOnly);
        Assert.Contains(profile.VolumeMounts, m => m.Purpose == "gitconfig_credentials" && m.ReadOnly);
        Assert.Contains(profile.VolumeMounts, m => m.Purpose == "ssh_credentials" && m.ReadOnly);
        Assert.Contains(profile.VolumeMounts, m => m.Purpose == "gh_credentials" && m.ReadOnly);
        Assert.All(profile.CallbackPorts, p => Assert.Equal("127.0.0.1", p.BindAddress));
        Assert.Contains("--publish", profile.DockerComposeRunArgs);
        Assert.DoesNotContain("--rm", profile.DockerComposeRunArgs);
        Assert.Contains("127.0.0.1:21455:1455", profile.DockerComposeRunArgs);
        Assert.Contains("127.0.0.1:28085:8085", profile.DockerComposeRunArgs);
        Assert.StartsWith("den-pi-den-mcp-session-a-", profile.ComposeProjectName);
        Assert.Equal([
            $"{profile.ComposeProjectName}_pi-cache",
            $"{profile.ComposeProjectName}_pi-npm-cache"
        ], profile.CacheVolumeNames);
    }

    [Fact]
    public void Render_BlanksProviderSecretEnvironmentVariables()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/opt/pi-docker/compose.yaml",
            DevDir = "/home/patch/dev",
            UsePerSessionWorkspace = false,
            PiStateRootDir = "/var/lib/den/pi-state",
            ProviderSecretEnvironmentVariables = ["OPENAI_API_KEY", "AWS_REGION"],
        });

        var profile = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
            CallbackPorts = [new() { HostPort = 21455, ContainerPort = 1455 }],
        });

        Assert.Equal(string.Empty, profile.Environment["OPENAI_API_KEY"]);
        Assert.Equal(string.Empty, profile.Environment["AWS_REGION"]);
        Assert.Equal(["AWS_REGION", "OPENAI_API_KEY"], profile.ScrubbedEnvironmentVariables);
        Assert.Contains(profile.Warnings, value => value.Contains("Provider/model credential environment variables are scrubbed", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_WarnsWhenRequiredPiStateSettingsAreMissing()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/opt/pi-docker/compose.yaml",
            DevDir = "/home/patch/dev",
            UsePerSessionWorkspace = false,
            PiStateRootDir = Path.Combine(Path.GetTempPath(), "den-mcp", "missing-pi-state-root"),
            RequiredPiStatePaths = ["agent/settings.json"],
        });

        var profile = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
            CallbackPorts = [new() { HostPort = 21455, ContainerPort = 1455 }],
        });

        Assert.Contains(profile.Warnings, value =>
            value.Contains("PI_STATE_DIR", StringComparison.Ordinal)
            && value.Contains("agent/settings.json", StringComparison.Ordinal)
            && value.Contains("launch will fail", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_RequiresExplicitCallbackPorts()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/opt/pi-docker/compose.yaml",
            DevDir = "/home/patch/dev",
            UsePerSessionWorkspace = false,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
        }));

        Assert.Contains("callback_ports must be provided per session", ex.Message);
    }

    [Fact]
    public void Render_RejectsNonLoopbackCallbackPorts()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/opt/pi-docker/compose.yaml",
            DevDir = "/home/patch/dev",
            UsePerSessionWorkspace = false,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
            CallbackPorts = [new() { BindAddress = "0.0.0.0", HostPort = 21455, ContainerPort = 1455 }],
        }));

        Assert.Contains("host loopback only", ex.Message);
    }

    [Fact]
    public void Render_DerivesDifferentComposeProjectsAndStateDirsForConcurrentSessions()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/opt/pi-docker/compose.yaml",
            DevDir = "/home/patch/dev",
            UsePerSessionWorkspace = false,
            PiStateRootDir = "/var/lib/den/pi-state",
        });

        var first = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
            CallbackPorts = [new() { HostPort = 21455, ContainerPort = 1455 }],
        });
        var second = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-b",
            CallbackPorts = [new() { HostPort = 21456, ContainerPort = 1455 }],
        });

        Assert.NotEqual(first.ComposeProjectName, second.ComposeProjectName);
        Assert.NotEqual(first.PiStateDir, second.PiStateDir);
        Assert.NotEqual(first.CacheVolumeNames[0], second.CacheVolumeNames[0]);
        Assert.Contains(first.KnownLimitations, value => value.Contains("does not probe or reserve host ports", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_UsesPerSessionWorkspaceByDefault()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            DevDir = "/data/dev",
            WorkspaceRootDir = "/data/services/den-mcp/pi-workspaces",
            PiStateRootDir = "/data/services/den-mcp/pi-sessions",
        });

        var profile = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
            CallbackPorts = [new() { HostPort = 21455, ContainerPort = 1455 }],
        });

        Assert.Equal("/data/services/den-mcp/pi-workspaces/session-a/dev", profile.DevDir);
        Assert.Equal("/data/services/den-mcp/pi-workspaces/session-a/dev", profile.Environment["DEV_DIR"]);
        Assert.Equal("/data/dev/den-mcp", profile.WorkspaceSourceProjectDir);
        Assert.Equal("main", profile.WorkspaceBranch);
        Assert.Contains(profile.VolumeMounts, m => m.Source == "/data/services/den-mcp/pi-workspaces/session-a/dev" && m.Target == "/home/pi/dev" && !m.ReadOnly);
        Assert.Contains(profile.KnownLimitations, value => value.Contains("per-session workspace", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_PassesWorkerEnvironmentAndUsesPrintModeWhenStartupPromptIsPresent()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/opt/pi-docker/compose.yaml",
            DevDir = "/home/patch/dev",
            UsePerSessionWorkspace = false,
            PiStateRootDir = "/var/lib/den/pi-state",
        });

        var profile = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "den-mcp",
            SessionId = "session-a",
            CallbackPorts = [new() { HostPort = 21455, ContainerPort = 1455 }],
            WorkerRole = "coder",
            WorkerRunId = "run-a",
            PromptPacketMessageId = 123,
            StateFileRef = "state.md",
            StartupPrompt = "do the work",
        });

        Assert.Contains("--env", profile.DockerComposeRunArgs);
        Assert.Contains("DEN_WORKER_ROLE", profile.DockerComposeRunArgs);
        Assert.Contains("DEN_WORKER_RUN_ID", profile.DockerComposeRunArgs);
        Assert.Contains("DEN_WORKER_PROMPT_PACKET_MESSAGE_ID", profile.DockerComposeRunArgs);
        Assert.Contains("DEN_WORKER_STATE_FILE_REF", profile.DockerComposeRunArgs);
        Assert.Contains("DEN_WORKER_STARTUP_PROMPT", profile.DockerComposeRunArgs);
        Assert.DoesNotContain(profile.DockerComposeRunArgs, arg => arg.Contains("do the work", StringComparison.Ordinal));
        Assert.Equal(["pi", "/bin/sh", "-lc", "exec pi -p \"$DEN_WORKER_STARTUP_PROMPT\""], profile.DockerComposeRunArgs.TakeLast(4).ToArray());
    }
}
