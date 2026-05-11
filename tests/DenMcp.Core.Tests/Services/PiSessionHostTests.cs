using System.Text.Json;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class PiSessionHostTests
{
    [Fact]
    public async Task Launch_BlanksProviderSecretsInCommandWithoutSeedingTmuxEnvironment()
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"pi-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(piStateDir, "agent"));
        await File.WriteAllTextAsync(Path.Combine(piStateDir, "agent", "settings.json"), "{}");
        var previousOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "server-secret");
        try
        {
            var runner = new FakeProcessRunner(string.Empty);
            var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
            {
                ProviderSecretEnvironmentVariables = ["OPENAI_API_KEY"],
                RequiredPiStatePaths = ["agent/settings.json"],
            }, runner, delayAsync: NoDelay);
            var record = Session();
            var profile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
                ["OPENAI_API_KEY"] = string.Empty,
            }, ["OPENAI_API_KEY"]);

            var result = await host.LaunchAsync(new PiSessionLaunchPlan
            {
                Record = record,
                LaunchProfile = profile,
                LaunchCommand = ["env", "OPENAI_API_KEY=", "docker", "compose", "run", "pi"],
            });

            Assert.Equal(PiSessionStates.Running, result.State);
            var newSessionArgs = Assert.Single(runner.Calls, args => TmuxCommand(args) == "new-session");
            AssertTmuxSocket(newSessionArgs, "session-a");
            Assert.Equal("/bin/sh -i", newSessionArgs[^1]);
            var newSessionEnvironment = runner.Environments[runner.Calls.IndexOf(newSessionArgs)];
            Assert.Equal("/bin/sh", newSessionEnvironment["SHELL"]);
            Assert.DoesNotContain("-e", newSessionArgs);
            Assert.DoesNotContain(newSessionArgs, value => value.Contains("OPENAI_API_KEY=", StringComparison.Ordinal));
            Assert.DoesNotContain(newSessionArgs, value => value.Contains("server-secret", StringComparison.Ordinal));
            var sendKeysArgs = Assert.Single(runner.Calls, args => TmuxCommand(args) == "send-keys" && args.Contains("-l"));
            Assert.Contains(sendKeysArgs, value => value.Contains("OPENAI_API_KEY=", StringComparison.Ordinal));
            Assert.DoesNotContain(sendKeysArgs, value => value.Contains("server-secret", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiKey);
            if (Directory.Exists(piStateDir))
                Directory.Delete(piStateDir, recursive: true);
        }
    }

    [Fact]
    public async Task Launch_StartsTmuxPaneWithExplicitConfiguredShellCommand()
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"pi-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(piStateDir, "agent"));
        await File.WriteAllTextAsync(Path.Combine(piStateDir, "agent", "settings.json"), "{}");
        try
        {
            var runner = new FakeProcessRunner(string.Empty);
            var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
            {
                TmuxShellCommand = ["/bin/bash", "-i"],
                RequiredPiStatePaths = ["agent/settings.json"],
            }, runner, delayAsync: NoDelay);
            var record = Session();
            var profile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
            }, []);

            var result = await host.LaunchAsync(new PiSessionLaunchPlan
            {
                Record = record,
                LaunchProfile = profile,
                LaunchCommand = ["docker", "compose", "run", "pi"],
            });

            Assert.Equal(PiSessionStates.Running, result.State);
            var newSessionArgs = Assert.Single(runner.Calls, args => TmuxCommand(args) == "new-session");
            AssertTmuxSocket(newSessionArgs, "session-a");
            Assert.Equal("/bin/bash -i", newSessionArgs[^1]);
        }
        finally
        {
            if (Directory.Exists(piStateDir))
                Directory.Delete(piStateDir, recursive: true);
        }
    }

    public static TheoryData<string, string[]?, bool, string> TmuxShellCommandNormalizationCases => new()
    {
        { "default options", null, false, "/bin/sh -i" },
        { "null command", null, true, "/bin/sh -i" },
        { "empty command", [], true, "/bin/sh -i" },
        { "whitespace-only command", ["", "  ", "\t"], true, "/bin/sh -i" },
        { "single-arg command", ["/bin/sh"], true, "/bin/sh" },
    };

    [Theory]
    [MemberData(nameof(TmuxShellCommandNormalizationCases))]
    public async Task Launch_NormalizesTmuxShellCommandFallbackEdges(
        string _,
        string[]? tmuxShellCommand,
        bool configureTmuxShellCommand,
        string expectedShellCommand)
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"pi-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(piStateDir, "agent"));
        await File.WriteAllTextAsync(Path.Combine(piStateDir, "agent", "settings.json"), "{}");
        try
        {
            var runner = new FakeProcessRunner(string.Empty);
            var options = new PiDockerLaunchProfileOptions
            {
                RequiredPiStatePaths = ["agent/settings.json"],
            };
            if (configureTmuxShellCommand)
                options.TmuxShellCommand = tmuxShellCommand!;
            var host = new TmuxDockerPiSessionHost(options, runner, delayAsync: NoDelay);
            var record = Session();
            var profile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
            }, []);

            var result = await host.LaunchAsync(new PiSessionLaunchPlan
            {
                Record = record,
                LaunchProfile = profile,
                LaunchCommand = ["docker", "compose", "run", "pi"],
            });

            Assert.Equal(PiSessionStates.Running, result.State);
            var newSessionArgs = Assert.Single(runner.Calls, args => TmuxCommand(args) == "new-session");
            AssertTmuxSocket(newSessionArgs, "session-a");
            Assert.Equal(expectedShellCommand, newSessionArgs[^1]);
        }
        finally
        {
            if (Directory.Exists(piStateDir))
                Directory.Delete(piStateDir, recursive: true);
        }
    }

    [Fact]
    public async Task Launch_FailsBeforeTmuxWhenRequiredPiStateSettingsAreMissing()
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"missing-pi-state-{Guid.NewGuid():N}");
        var runner = new FakeProcessRunner(string.Empty);
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
        {
            RequiredPiStatePaths = ["agent/settings.json"],
        }, runner);

        var result = await host.LaunchAsync(new PiSessionLaunchPlan
        {
            Record = Session(),
            LaunchProfile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
            }, []),
            LaunchCommand = ["docker", "compose", "run", "pi"],
        });

        Assert.Equal(PiSessionStates.Failed, result.State);
        Assert.Contains("PI_STATE_DIR", result.StateReason);
        Assert.Contains("do not fall back to provider environment secrets", result.StateReason);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Launch_ProvisionsMissingPiStateFromConfiguredSourceBeforeValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "den-mcp", $"pi-provision-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(root, "source");
        var piStateDir = Path.Combine(root, "session-a");
        Directory.CreateDirectory(Path.Combine(sourceDir, "agent"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "agent", "settings.json"), "{\"provider\":\"test\"}");
        try
        {
            var runner = new FakeProcessRunner(string.Empty);
            var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
            {
                RequiredPiStatePaths = ["agent/settings.json"],
            }, runner, delayAsync: NoDelay);

            var result = await host.LaunchAsync(new PiSessionLaunchPlan
            {
                Record = Session(),
                LaunchProfile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PI_STATE_DIR"] = piStateDir,
                }, [], piStateSourceDir: sourceDir),
                LaunchCommand = ["docker", "compose", "run", "pi"],
            });

            Assert.Equal(PiSessionStates.Running, result.State);
            Assert.True(File.Exists(Path.Combine(piStateDir, "agent", "settings.json")));
            Assert.NotEmpty(runner.Calls);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Launch_MarksFailedWhenDockerFailureAppearsDuringLaunchPolling()
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"pi-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(piStateDir, "agent"));
        await File.WriteAllTextAsync(Path.Combine(piStateDir, "agent", "settings.json"), "{}");
        try
        {
            const string output = "permission denied while trying to connect to the Docker API at unix:///var/run/docker.sock";
            var runner = new FakeProcessRunner([string.Empty, output]);
            var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
            {
                RequiredPiStatePaths = ["agent/settings.json"],
            }, runner, delayAsync: NoDelay);
            var profile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
            }, []);

            var result = await host.LaunchAsync(new PiSessionLaunchPlan
            {
                Record = Session(),
                LaunchProfile = profile,
                LaunchCommand = ["docker", "compose", "run", "pi"],
            });

            Assert.Equal(PiSessionStates.Failed, result.State);
            Assert.Contains("Docker launch command failed", result.StateReason);
            Assert.Equal("container-a", result.ContainerName);
            Assert.True(runner.Calls.Count(args => TmuxCommand(args) == "capture-pane") >= 2);
        }
        finally
        {
            if (Directory.Exists(piStateDir))
                Directory.Delete(piStateDir, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_UsesLaunchProfileDockerEnvironment()
    {
        var runner = new FakeProcessRunner(string.Empty);
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
        {
            DockerExecutable = "/usr/bin/docker",
            DockerHost = "unix:///run/den-mcp/docker-rt/fallback.sock",
        }, runner);
        var profile = Profile("/tmp/den-mcp/pi-state", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = "unix:///run/den-mcp/docker-rt/docker.sock",
            ["DEV_DIR"] = "/tmp/den-mcp/dev",
            ["OPENAI_API_KEY"] = string.Empty,
            ["PI_STATE_DIR"] = "/tmp/den-mcp/pi-state",
        }, ["OPENAI_API_KEY"]);

        var result = await host.CleanupAsync(Session(), profile);

        Assert.True(result.Succeeded);
        var cleanupCallIndex = runner.Calls.FindIndex(args => args.Count > 0 && args[0] == "compose" && args.Contains("down"));
        Assert.True(cleanupCallIndex >= 0);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", runner.Environments[cleanupCallIndex]["DOCKER_HOST"]);
        Assert.Equal("/tmp/den-mcp/dev", runner.Environments[cleanupCallIndex]["DEV_DIR"]);
        Assert.Equal(string.Empty, runner.Environments[cleanupCallIndex]["OPENAI_API_KEY"]);
    }

    [Fact]
    public void ExtractContainerName_FallsBackToComposeProjectAndServiceWhenNameFlagIsAbsent()
    {
        var profile = Profile("/tmp/den-mcp/pi-state", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PI_STATE_DIR"] = "/tmp/den-mcp/pi-state",
        }, [], ["compose", "run", "pi"]);

        Assert.Equal("compose-a-pi", PiSessionContainerNames.Extract(profile));
    }

    [Fact]
    public async Task GetStatus_MarksFailedWhenOutputShowsDockerSocketPermissionDenied()
    {
        const string output = "permission denied while trying to connect to the Docker API at unix:///var/run/docker.sock";
        var runner = new FakeProcessRunner(output);
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions(), runner);

        var status = await host.GetStatusAsync(Session());

        Assert.Equal(PiSessionStates.Failed, status.State);
        Assert.Contains("Docker launch command failed", status.StateReason);
        Assert.Equal(output, status.OutputTail);
    }

    [Fact]
    public async Task GetStatus_MarksFailedWhenKnownContainerIsMissingAfterStartupGracePeriod()
    {
        var now = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);
        var profile = Profile("/tmp/den-mcp/pi-state", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = "unix:///run/den-mcp/docker-rt/docker.sock",
            ["PI_STATE_DIR"] = "/tmp/den-mcp/pi-state",
        }, []);
        var runner = new FakeProcessRunner(string.Empty, new ProcessRunResult
        {
            ExitCode = 1,
            Stderr = "Error: No such object: container-a",
        });
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
        {
            DockerExecutable = "/usr/bin/docker",
            DockerHost = "unix:///run/den-mcp/docker-rt/fallback.sock",
        }, runner, () => now);

        var status = await host.GetStatusAsync(Session(
            containerName: "container-a",
            launchProfileJson: JsonSerializer.Serialize(profile, PiSessionJson.Options),
            startedAt: now.AddSeconds(-30)));

        Assert.Equal(PiSessionStates.Failed, status.State);
        Assert.Contains("Expected Docker container 'container-a' was not found", status.StateReason);
        var inspectCallIndex = runner.Calls.FindIndex(args => args.Count > 0 && args[0] == "inspect");
        Assert.True(inspectCallIndex >= 0);
        Assert.Equal("container-a", runner.Calls[inspectCallIndex][^1]);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", runner.Environments[inspectCallIndex]["DOCKER_HOST"]);
    }

    [Fact]
    public async Task GetStatus_RecordsRunningDockerContainerIdWhenContainerIsPresent()
    {
        var runner = new FakeProcessRunner(string.Empty, new ProcessRunResult
        {
            ExitCode = 0,
            Stdout = "container-id-a\trunning\t0\t\n",
        });
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions(), runner);

        var status = await host.GetStatusAsync(Session(containerName: "container-a"));

        Assert.Equal(PiSessionStates.Running, status.State);
        Assert.Equal("container-id-a", status.ContainerId);
        Assert.Equal("container-a", status.ContainerName);
    }

    [Fact]
    public async Task GetStatus_DoesNotMarkExactlyEightyCapturedLinesAsTruncated()
    {
        var runner = new FakeProcessRunner(Lines(80));
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions(), runner, () => new DateTime(2026, 5, 6, 8, 0, 0, DateTimeKind.Utc));

        var status = await host.GetStatusAsync(Session());

        Assert.Equal(PiSessionStates.Running, status.State);
        Assert.False(status.OutputTailTruncated);
        Assert.Equal(80, status.OutputTail!.Split('\n').Length);
        var captureArgs = Assert.Single(runner.Calls, args => TmuxCommand(args) == "capture-pane");
        var startIndex = captureArgs.ToList().IndexOf("-S");
        Assert.Equal("-81", captureArgs[startIndex + 1]);
    }

    [Fact]
    public async Task GetStatus_MarksAndDropsExtraCapturedLineWhenLineLimitExceeded()
    {
        var runner = new FakeProcessRunner(Lines(81));
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions(), runner);

        var status = await host.GetStatusAsync(Session());

        Assert.True(status.OutputTailTruncated);
        var outputLines = status.OutputTail!.Split('\n');
        Assert.Equal(80, outputLines.Length);
        Assert.Equal("line-2", outputLines[0]);
        Assert.Equal("line-81", outputLines[^1]);
    }

    private static PiDockerLaunchProfile Profile(
        string piStateDir,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> scrubbedEnvironmentVariables,
        IReadOnlyList<string>? dockerComposeRunArgs = null,
        string? piStateSourceDir = null) => new()
    {
        ProfileId = "profile-a",
        ProjectId = "den-mcp",
        SessionId = "session-a",
        ComposeProjectName = "compose-a",
        ComposeFile = "/opt/pi-docker/compose.yaml",
        Service = "pi",
        DevDir = "/tmp/den-mcp/dev",
        PiStateDir = piStateDir,
        PiStateSourceDir = piStateSourceDir,
        Image = "pi-sandbox:test",
        PiVersion = "0.71.0",
        NodeVersion = "22",
        Environment = environment,
        ScrubbedEnvironmentVariables = scrubbedEnvironmentVariables,
        DockerHost = environment.TryGetValue("DOCKER_HOST", out var dockerHost) ? dockerHost : null,
        DockerComposeRunArgs = dockerComposeRunArgs ?? ["compose", "run", "--name", "container-a", "pi"],
    };

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static PiSessionRecord Session(string? containerName = null, string? launchProfileJson = null, DateTime? startedAt = null) => new()
    {
        SessionId = "session-a",
        ProjectId = "den-mcp",
        HostId = "host-test",
        TmuxSessionName = "tmux-session-a",
        ContainerName = containerName,
        State = PiSessionStates.Running,
        LaunchProfileKind = "test",
        LaunchProfileJson = launchProfileJson ?? "{}",
        LaunchCommandJson = "[]",
        LaunchCommandDisplay = "test",
        CreatedAt = startedAt ?? DateTime.UtcNow,
        StartedAt = startedAt,
        UpdatedAt = DateTime.UtcNow,
    };

    private static string Lines(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => $"line-{i}"));

    private static string? TmuxCommand(IReadOnlyList<string> args)
    {
        if (args.Count >= 3 && args[0] == "-L")
            return args[2];
        return args.Count > 0 ? args[0] : null;
    }

    private static void AssertTmuxSocket(IReadOnlyList<string> args, string sessionId)
    {
        Assert.True(args.Count >= 3, "tmux args should include -L <socket> before the command");
        Assert.Equal("-L", args[0]);
        Assert.Equal($"den-mcp-{sessionId}", args[1]);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Queue<string> _capturedOutputs;
        private readonly ProcessRunResult? _inspectResult;
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];

        public FakeProcessRunner(string capturedOutput, ProcessRunResult? inspectResult = null)
            : this([capturedOutput], inspectResult)
        {
        }

        public FakeProcessRunner(IReadOnlyList<string> capturedOutputs, ProcessRunResult? inspectResult = null)
        {
            _capturedOutputs = capturedOutputs.Count == 0
                ? new Queue<string>([string.Empty])
                : new Queue<string>(capturedOutputs);
            _inspectResult = inspectResult;
        }

        public Task<ProcessRunResult> RunAsync(
            string executable,
            IReadOnlyList<string> args,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(args.ToArray());
            Environments.Add(environment is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(environment, StringComparer.Ordinal));
            var command = TmuxCommand(args);
            if (command == "list-sessions")
            {
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
                    Stdout = "tmux-session-a\t1760000000\t1760000010\n",
                });
            }

            if (command == "capture-pane")
            {
                var capturedOutput = _capturedOutputs.Count > 1 ? _capturedOutputs.Dequeue() : _capturedOutputs.Peek();
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
                    Stdout = capturedOutput,
                });
            }

            if (args.Count > 0 && args[0] == "compose" && args.Contains("down"))
            {
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
                });
            }

            if (args.Count > 0 && args[0] == "inspect")
            {
                return Task.FromResult(_inspectResult ?? new ProcessRunResult
                {
                    ExitCode = 1,
                    Stderr = "unexpected inspect command",
                });
            }

            if (command is "new-session" or "set-option" or "send-keys")
            {
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
                });
            }

            return Task.FromResult(new ProcessRunResult
            {
                ExitCode = 1,
                Stderr = "unexpected command",
            });
        }
    }
}
