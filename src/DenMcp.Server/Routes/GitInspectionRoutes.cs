using DenMcp.Core.Data;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class GitInspectionRoutes
{
    public static void MapGitInspectionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/git");

        group.MapGet("/status", async (IProjectRepository projects, IGitInspectionService git, string projectId, CancellationToken cancellationToken) =>
        {
            var project = await projects.GetByIdAsync(projectId);
            if (project is null)
                return Results.NotFound(new { error = $"Project '{projectId}' not found" });

            var status = await git.GetStatusAsync(projectId, project.RootPath, cancellationToken);
            return Results.Ok(status);
        });

        group.MapGet("/files", async (
            IProjectRepository projects,
            IGitInspectionService git,
            string projectId,
            string? baseRef,
            string? headRef,
            bool? includeUntracked,
            CancellationToken cancellationToken) =>
        {
            var project = await projects.GetByIdAsync(projectId);
            if (project is null)
                return Results.NotFound(new { error = $"Project '{projectId}' not found" });

            var files = await git.GetFilesAsync(projectId, project.RootPath, baseRef, headRef, includeUntracked ?? true, cancellationToken);
            return Results.Ok(files);
        });

        group.MapGet("/diff", async (
            IProjectRepository projects,
            IGitInspectionService git,
            string projectId,
            string? path,
            string? baseRef,
            string? headRef,
            int? maxBytes,
            bool? staged,
            CancellationToken cancellationToken) =>
        {
            var project = await projects.GetByIdAsync(projectId);
            if (project is null)
                return Results.NotFound(new { error = $"Project '{projectId}' not found" });

            var diff = await git.GetDiffAsync(projectId, project.RootPath, path, baseRef, headRef, maxBytes, staged ?? false, cancellationToken);
            return Results.Ok(diff);
        });
    }
}
