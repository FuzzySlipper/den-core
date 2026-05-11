using System.Reflection;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class PiSessionServiceLaunchCommandTests
{
    [Fact]
    public void BuildLaunchCommand_IncludesComposeInterpolationEnvironment()
    {
        var profile = new PiDockerLaunchProfile
        {
            ProfileId = "profile-a",
            ProjectId = "den-mcp",
            SessionId = "session-a",
            ComposeProjectName = "compose-a",
            ComposeFile = "/data/services/den-mcp/pi-docker/compose.yaml",
            Service = "pi",
            DevDir = "/data/dev",
            PiStateDir = "/data/services/den-mcp/pi-sessions/session-a",
            Image = "pi-sandbox:latest",
            PiVersion = "0.71.0",
            NodeVersion = "22",
            DockerHost = "unix:///run/den-mcp/docker-rt/docker.sock",
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DEV_DIR"] = "/data/dev",
                ["PI_STATE_DIR"] = "/data/services/den-mcp/pi-sessions/session-a",
                ["PI_SANDBOX_IMAGE"] = "pi-sandbox:latest",
                ["OPENAI_API_KEY"] = "should-be-blanked",
            },
            ScrubbedEnvironmentVariables = ["OPENAI_API_KEY"],
            DockerComposeRunArgs = ["compose", "--project-name", "compose-a", "-f", "/data/services/den-mcp/pi-docker/compose.yaml", "run", "pi"],
        };

        var method = typeof(PiSessionService).GetMethod("BuildLaunchCommand", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var command = Assert.IsType<List<string>>(method.Invoke(null, ["/usr/bin/docker", profile]));

        Assert.Equal("env", command[0]);
        Assert.Contains("DEV_DIR=/data/dev", command);
        Assert.Contains("PI_STATE_DIR=/data/services/den-mcp/pi-sessions/session-a", command);
        Assert.Contains("PI_SANDBOX_IMAGE=pi-sandbox:latest", command);
        Assert.Contains("DOCKER_HOST=unix:///run/den-mcp/docker-rt/docker.sock", command);
        Assert.Contains("OPENAI_API_KEY=", command);
        Assert.DoesNotContain("OPENAI_API_KEY=should-be-blanked", command);
        Assert.Contains("/usr/bin/docker", command);
    }
}
