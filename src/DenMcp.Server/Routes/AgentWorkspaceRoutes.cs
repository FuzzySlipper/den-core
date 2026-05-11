using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class AgentWorkspaceRoutes
{
    public static void MapAgentWorkspaceRoutes(this WebApplication app)
    {
        app.MapGet("/api/agent-workspaces", async (
            IAgentWorkspaceRepository repo,
            string? projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var parsedState = ParseState(state);
            if (parsedState.Invalid)
                return Results.BadRequest(new { error = $"Unknown agent workspace state: {state}" });

            var workspaces = await repo.ListAsync(new AgentWorkspaceListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = parsedState.State,
                Limit = limit ?? 50
            });
            return Results.Ok(workspaces);
        });

        app.MapGet("/api/agent-workspaces/{workspaceId}", async (
            IAgentWorkspaceRepository repo,
            string workspaceId,
            string? projectId) =>
        {
            var workspace = await repo.GetAsync(workspaceId, projectId);
            return workspace is null
                ? Results.NotFound(new { error = $"Agent workspace {workspaceId} not found" })
                : Results.Ok(workspace);
        });

        app.MapGet("/api/projects/{projectId}/agent-workspaces", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            int? taskId,
            string? state,
            int? limit) =>
        {
            var parsedState = ParseState(state);
            if (parsedState.Invalid)
                return Results.BadRequest(new { error = $"Unknown agent workspace state: {state}" });

            var workspaces = await repo.ListAsync(new AgentWorkspaceListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                State = parsedState.State,
                Limit = limit ?? 50
            });
            return Results.Ok(workspaces);
        });

        app.MapGet("/api/projects/{projectId}/agent-workspaces/{workspaceId}", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            string workspaceId) =>
        {
            var workspace = await repo.GetAsync(workspaceId, projectId);
            return workspace is null
                ? Results.NotFound(new { error = $"Agent workspace {workspaceId} not found" })
                : Results.Ok(workspace);
        });

        app.MapGet("/api/projects/{projectId}/agent-workspaces/{workspaceId}/git/status", async (
            IAgentWorkspaceRepository repo,
            ITaskRepository tasks,
            IGitInspectionService git,
            string projectId,
            string workspaceId,
            CancellationToken cancellationToken) =>
        {
            var loaded = await LoadWorkspaceForGitAsync(repo, tasks, projectId, workspaceId);
            if (loaded.Error is not null)
                return loaded.Error;
            var workspace = loaded.Workspace!;

            var status = await git.GetStatusAsync(projectId, workspace.WorktreePath, cancellationToken);
            ApplyWorkspaceMetadata(status, workspace);
            AddAlignmentWarnings(status, workspace, status);
            return Results.Ok(status);
        });

        app.MapGet("/api/projects/{projectId}/agent-workspaces/{workspaceId}/git/files", async (
            IAgentWorkspaceRepository repo,
            ITaskRepository tasks,
            IGitInspectionService git,
            string projectId,
            string workspaceId,
            string? baseRef,
            string? headRef,
            bool? includeUntracked,
            CancellationToken cancellationToken) =>
        {
            var loaded = await LoadWorkspaceForGitAsync(repo, tasks, projectId, workspaceId);
            if (loaded.Error is not null)
                return loaded.Error;
            var workspace = loaded.Workspace!;

            var files = await git.GetFilesAsync(projectId, workspace.WorktreePath, baseRef, headRef, includeUntracked ?? true, cancellationToken);
            ApplyWorkspaceMetadata(files, workspace);
            await AddAlignmentWarningsAsync(git, files.Warnings, workspace, projectId, cancellationToken);
            return Results.Ok(files);
        });

        app.MapGet("/api/projects/{projectId}/agent-workspaces/{workspaceId}/git/diff", async (
            IAgentWorkspaceRepository repo,
            ITaskRepository tasks,
            IGitInspectionService git,
            string projectId,
            string workspaceId,
            string? path,
            string? baseRef,
            string? headRef,
            int? maxBytes,
            bool? staged,
            CancellationToken cancellationToken) =>
        {
            var loaded = await LoadWorkspaceForGitAsync(repo, tasks, projectId, workspaceId);
            if (loaded.Error is not null)
                return loaded.Error;
            var workspace = loaded.Workspace!;

            var diff = await git.GetDiffAsync(projectId, workspace.WorktreePath, path, baseRef, headRef, maxBytes, staged ?? false, cancellationToken);
            ApplyWorkspaceMetadata(diff, workspace);
            await AddAlignmentWarningsAsync(git, diff.Warnings, workspace, projectId, cancellationToken);
            return Results.Ok(diff);
        });

        app.MapPost("/api/projects/{projectId}/agent-workspaces", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            UpsertAgentWorkspaceRequest req) =>
        {
            var workspace = BuildWorkspace(projectId, req);
            var validationError = Validate(workspace);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            return await SaveWorkspaceAsync(repo, workspace);
        });

        app.MapPut("/api/projects/{projectId}/agent-workspaces/{workspaceId}", async (
            IAgentWorkspaceRepository repo,
            string projectId,
            string workspaceId,
            UpsertAgentWorkspaceRequest req) =>
        {
            var workspace = BuildWorkspace(projectId, req with { Id = workspaceId });
            var validationError = Validate(workspace);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            return await SaveWorkspaceAsync(repo, workspace);
        });
    }

    private static async Task<(AgentWorkspace? Workspace, IResult? Error)> LoadWorkspaceForGitAsync(
        IAgentWorkspaceRepository repo,
        ITaskRepository tasks,
        string projectId,
        string workspaceId)
    {
        var workspace = await repo.GetAsync(workspaceId, projectId);
        if (workspace is null)
            return (null, Results.NotFound(new { error = $"Agent workspace {workspaceId} not found" }));

        var task = await tasks.GetByIdAsync(workspace.TaskId);
        if (task is null || !string.Equals(task.ProjectId, projectId, StringComparison.Ordinal))
            return (null, Results.Conflict(new { error = $"Agent workspace {workspaceId} references task {workspace.TaskId}, which does not belong to project '{projectId}'." }));

        return (workspace, null);
    }

    private static void ApplyWorkspaceMetadata(GitStatusResponse response, AgentWorkspace workspace)
    {
        response.WorkspaceId = workspace.Id;
        response.TaskId = workspace.TaskId;
        response.WorkspaceBranch = workspace.Branch;
        response.WorkspaceBaseBranch = workspace.BaseBranch;
        response.WorkspaceBaseCommit = workspace.BaseCommit;
        response.WorkspaceHeadCommit = workspace.HeadCommit;
    }

    private static void ApplyWorkspaceMetadata(GitFilesResponse response, AgentWorkspace workspace)
    {
        response.WorkspaceId = workspace.Id;
        response.TaskId = workspace.TaskId;
        response.WorkspaceBranch = workspace.Branch;
        response.WorkspaceBaseBranch = workspace.BaseBranch;
        response.WorkspaceBaseCommit = workspace.BaseCommit;
        response.WorkspaceHeadCommit = workspace.HeadCommit;
    }

    private static void ApplyWorkspaceMetadata(GitDiffResponse response, AgentWorkspace workspace)
    {
        response.WorkspaceId = workspace.Id;
        response.TaskId = workspace.TaskId;
        response.WorkspaceBranch = workspace.Branch;
        response.WorkspaceBaseBranch = workspace.BaseBranch;
        response.WorkspaceBaseCommit = workspace.BaseCommit;
        response.WorkspaceHeadCommit = workspace.HeadCommit;
    }

    private static async Task AddAlignmentWarningsAsync(
        IGitInspectionService git,
        List<string> warnings,
        AgentWorkspace workspace,
        string projectId,
        CancellationToken cancellationToken)
    {
        var status = await git.GetStatusAsync(projectId, workspace.WorktreePath, cancellationToken);
        if (status.Errors.Count > 0)
            return;
        AddAlignmentWarnings(warnings, workspace, status);
    }

    private static void AddAlignmentWarnings(GitStatusResponse response, AgentWorkspace workspace, GitStatusResponse liveStatus) =>
        AddAlignmentWarnings(response.Warnings, workspace, liveStatus);

    private static void AddAlignmentWarnings(List<string> warnings, AgentWorkspace workspace, GitStatusResponse liveStatus)
    {
        if (liveStatus.IsDetached && !string.IsNullOrWhiteSpace(workspace.Branch))
        {
            warnings.Add($"Workspace git checkout is detached; stored workspace branch is '{workspace.Branch}'.");
        }
        else if (!string.IsNullOrWhiteSpace(workspace.Branch)
                 && !string.IsNullOrWhiteSpace(liveStatus.Branch)
                 && !string.Equals(workspace.Branch, liveStatus.Branch, StringComparison.Ordinal))
        {
            warnings.Add($"Workspace branch metadata '{workspace.Branch}' differs from live git branch '{liveStatus.Branch}'.");
        }

        if (!string.IsNullOrWhiteSpace(workspace.HeadCommit)
            && !string.IsNullOrWhiteSpace(liveStatus.HeadSha)
            && !SameCommit(workspace.HeadCommit, liveStatus.HeadSha))
        {
            warnings.Add($"Workspace head metadata '{workspace.HeadCommit}' differs from live git HEAD '{liveStatus.HeadSha}'.");
        }
    }

    private static bool SameCommit(string left, string right) =>
        left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> SaveWorkspaceAsync(IAgentWorkspaceRepository repo, AgentWorkspace workspace)
    {
        try
        {
            var saved = await repo.UpsertAsync(workspace);
            return Results.Ok(saved);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Results.BadRequest(new { error = "Agent workspace references an unknown project, task, or run." });
        }
    }

    private static AgentWorkspace BuildWorkspace(string projectId, UpsertAgentWorkspaceRequest req) => new()
    {
        Id = string.IsNullOrWhiteSpace(req.Id) ? NewWorkspaceId() : req.Id.Trim(),
        ProjectId = projectId.Trim(),
        TaskId = req.TaskId,
        Branch = req.Branch?.Trim() ?? string.Empty,
        WorktreePath = req.WorktreePath?.Trim() ?? string.Empty,
        BaseBranch = req.BaseBranch?.Trim() ?? string.Empty,
        BaseCommit = TrimToNull(req.BaseCommit),
        HeadCommit = TrimToNull(req.HeadCommit),
        State = req.State ?? AgentWorkspaceState.Active,
        CreatedByRunId = TrimToNull(req.CreatedByRunId),
        DevServerUrl = TrimToNull(req.DevServerUrl),
        PreviewUrl = TrimToNull(req.PreviewUrl),
        CleanupPolicy = req.CleanupPolicy ?? AgentWorkspaceCleanupPolicy.Keep,
        ChangedFileSummary = req.ChangedFileSummary is null ? null : req.ChangedFileSummary.Value.Clone()
    };

    private static string NewWorkspaceId() => $"ws_{Guid.NewGuid():N}";

    private static (AgentWorkspaceState? State, bool Invalid) ParseState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return (null, false);

        try
        {
            return (EnumExtensions.ParseAgentWorkspaceState(state.Trim()), false);
        }
        catch (ArgumentException)
        {
            return (null, true);
        }
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Validate(AgentWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.ProjectId))
            return "Project id is required.";
        if (workspace.TaskId <= 0)
            return "Task id must be positive.";
        if (string.IsNullOrWhiteSpace(workspace.Branch))
            return "Branch is required.";
        if (string.IsNullOrWhiteSpace(workspace.WorktreePath))
            return "Worktree path is required.";
        if (string.IsNullOrWhiteSpace(workspace.BaseBranch))
            return "Base branch is required.";
        if (!IsCompactChangedFileSummary(workspace.ChangedFileSummary))
            return "Changed file summary must be compact JSON at or below 12000 characters.";
        return null;
    }

    private static bool IsCompactChangedFileSummary(JsonElement? summary)
    {
        if (summary is null)
            return true;
        return JsonSerializer.Serialize(summary.Value).Length <= 12_000;
    }
}

public sealed record UpsertAgentWorkspaceRequest
{
    public string? Id { get; init; }
    public int TaskId { get; init; }
    public string? Branch { get; init; }
    public string? WorktreePath { get; init; }
    public string? BaseBranch { get; init; }
    public string? BaseCommit { get; init; }
    public string? HeadCommit { get; init; }
    public AgentWorkspaceState? State { get; init; }
    public string? CreatedByRunId { get; init; }
    public string? DevServerUrl { get; init; }
    public string? PreviewUrl { get; init; }
    public AgentWorkspaceCleanupPolicy? CleanupPolicy { get; init; }
    public JsonElement? ChangedFileSummary { get; init; }
}
