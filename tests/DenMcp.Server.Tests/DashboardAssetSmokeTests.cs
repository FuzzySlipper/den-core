using System.Text.RegularExpressions;
using DenMcp.Core.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenMcp.Server.Tests;

public class DashboardAssetSmokeTests : IAsyncLifetime
{
    private DashboardAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new DashboardAppFactory();
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
    public async Task DashboardBundle_IncludesAgentStreamSurface()
    {
        var html = await _client.GetStringAsync("/");
        var match = Regex.Match(html, @"src=""(?<path>/assets/index-[^""]+\.js)");

        Assert.True(match.Success, "Expected dashboard HTML to reference the built JavaScript asset.");

        var bundle = await _client.GetStringAsync(match.Groups["path"].Value);
        Assert.Contains("Agent Stream", bundle, StringComparison.Ordinal);
        Assert.Contains("No agent stream entries.", bundle, StringComparison.Ordinal);
        Assert.Contains("Open dispatch #", bundle, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DashboardAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-dashboard-smoke-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = "",
                    ["DenMcp:Llm:Model"] = "test-model"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private sealed class NoOpLlmClient : ILlmClient
        {
            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
                => Task.FromResult(string.Empty);
        }
    }
}
