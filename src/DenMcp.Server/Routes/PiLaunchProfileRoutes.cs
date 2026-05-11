using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class PiLaunchProfileRoutes
{
    public static void MapPiLaunchProfileRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/pi-launch-profile");

        group.MapGet("/defaults", (DenMcpOptions options) => Results.Ok(options.PiSessionHost));

        group.MapPost("/render", (
            IPiDockerLaunchProfileRenderer renderer,
            string projectId,
            RenderPiDockerLaunchProfileRequest req) =>
        {
            try
            {
                var profile = renderer.Render(new PiDockerLaunchRenderRequest
                {
                    ProjectId = projectId,
                    SessionId = req.SessionId,
                    TaskId = req.TaskId,
                    WorkspaceId = req.WorkspaceId,
                    Title = req.Title,
                    DevDir = req.DevDir,
                    PiStateDir = req.PiStateDir,
                    ComposeFile = req.ComposeFile,
                    Service = req.Service,
                    Image = req.Image,
                    PiVersion = req.PiVersion,
                    NodeVersion = req.NodeVersion,
                    GitConfigPath = req.GitConfigPath,
                    SshDir = req.SshDir,
                    GhConfigDir = req.GhConfigDir,
                    CallbackPorts = req.CallbackPorts,
                });

                return Results.Ok(profile);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public sealed class RenderPiDockerLaunchProfileRequest
{
    public required string SessionId { get; init; }
    public long? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public string? DevDir { get; init; }
    public string? PiStateDir { get; init; }
    public string? ComposeFile { get; init; }
    public string? Service { get; init; }
    public string? Image { get; init; }
    public string? PiVersion { get; init; }
    public string? NodeVersion { get; init; }
    public string? GitConfigPath { get; init; }
    public string? SshDir { get; init; }
    public string? GhConfigDir { get; init; }
    public IReadOnlyList<PiDockerCallbackPort> CallbackPorts { get; init; } = [];
}
