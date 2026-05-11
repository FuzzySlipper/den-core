using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenMcp.Server.Tests;

public sealed class PiLaunchProfileApiTests : IAsyncLifetime
{
    private PiLaunchProfileAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new PiLaunchProfileAppFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Render_ReturnsEffectivePiDockerProfile()
    {
        var response = await _client.PostAsJsonAsync("/api/projects/den-mcp/pi-launch-profile/render", new
        {
            session_id = "session-a",
            task_id = 1188,
            compose_file = "/opt/pi-docker/compose.yaml",
            dev_dir = "/srv/dev",
            image = "pi-sandbox:test",
            pi_version = "0.71.0",
            node_version = "22",
            callback_ports = new[]
            {
                new { host_port = 21455, container_port = 1455 },
                new { host_port = 28085, container_port = 8085 },
            },
        });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("den-mcp", root.GetProperty("project_id").GetString());
        Assert.Equal("session-a", root.GetProperty("session_id").GetString());
        Assert.Equal("/opt/pi-docker/compose.yaml", root.GetProperty("compose_file").GetString());
        Assert.Equal("/srv/dev", root.GetProperty("dev_dir").GetString());
        Assert.Equal("pi-sandbox:test", root.GetProperty("image").GetString());
        Assert.Equal("0.71.0", root.GetProperty("pi_version").GetString());
        Assert.Equal("22", root.GetProperty("node_version").GetString());
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", root.GetProperty("docker_host").GetString());
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", root.GetProperty("environment").GetProperty("DOCKER_HOST").GetString());
        Assert.Contains("--project-name", root.GetProperty("docker_compose_run_args").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("127.0.0.1:21455:1455", root.GetProperty("docker_compose_run_args").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("127.0.0.1:28085:8085", root.GetProperty("docker_compose_run_args").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("127.0.0.1", root.GetProperty("callback_ports")[0].GetProperty("bind_address").GetString());
        Assert.NotEmpty(root.GetProperty("known_limitations").EnumerateArray());
    }

    [Fact]
    public async Task Render_RejectsMissingCallbackPorts()
    {
        var response = await _client.PostAsJsonAsync("/api/projects/den-mcp/pi-launch-profile/render", new
        {
            session_id = "session-a",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("callback_ports must be provided per session", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Defaults_ConfiguredArraysReplaceDefaultsInsteadOfAppending()
    {
        using var response = await _client.GetAsync("/api/projects/den-mcp/pi-launch-profile/defaults");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(["/bin/sh", "-i"], root.GetProperty("tmux_shell_command").EnumerateArray().Select(v => v.GetString()!).ToArray());
        Assert.Equal(["agent/settings.json"], root.GetProperty("required_pi_state_paths").EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    private sealed class PiLaunchProfileAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-pi-launch-profile-{Guid.NewGuid()}.db");

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
                    ["llm-model"] = "fake",
                    ["DenMcp:PiSessionHost:ComposeFile"] = "/opt/pi-docker/compose.yaml",
                    ["DenMcp:PiSessionHost:DevDir"] = "/srv/dev",
                    ["DenMcp:PiSessionHost:PiStateRootDir"] = "/srv/pi-state",
                    ["DenMcp:PiSessionHost:Image"] = "pi-sandbox:test",
                    ["DenMcp:PiSessionHost:PiVersion"] = "0.71.0",
                    ["DenMcp:PiSessionHost:NodeVersion"] = "22",
                    ["DenMcp:PiSessionHost:GitConfigPath"] = "/home/patch/.gitconfig",
                    ["DenMcp:PiSessionHost:SshDir"] = "/home/patch/.ssh",
                    ["DenMcp:PiSessionHost:GhConfigDir"] = "/home/patch/.config/gh",
                    ["DenMcp:PiSessionHost:DockerHost"] = "unix:///run/den-mcp/docker-rt/docker.sock",
                    ["DenMcp:PiSessionHost:TmuxShellCommand:0"] = "/bin/sh",
                    ["DenMcp:PiSessionHost:TmuxShellCommand:1"] = "-i",
                    ["DenMcp:PiSessionHost:RequiredPiStatePaths:0"] = "agent/settings.json",
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
