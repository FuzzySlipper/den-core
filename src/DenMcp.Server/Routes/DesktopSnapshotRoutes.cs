using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class DesktopSnapshotRoutes
{
    public static void MapDesktopSnapshotRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/desktop");

        group.MapPut("/git-snapshots", async (
            IDesktopSnapshotRepository repo,
            string projectId,
            UpsertDesktopGitSnapshotRequest req) =>
        {
            try
            {
                var snapshot = BuildGitSnapshot(projectId, req);
                var saved = await repo.UpsertGitSnapshotAsync(snapshot);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.BadRequest(new { error = "Desktop git snapshot references an unknown project, task, or workspace." });
            }
        });

        group.MapGet("/git-snapshots", async (
            IDesktopSnapshotRepository repo,
            string projectId,
            int? taskId,
            string? workspaceId,
            string? sourceInstanceId,
            string? rootPath,
            string? state,
            int? staleAfterSeconds,
            int? limit) =>
        {
            var parsedState = ParseSnapshotState(state);
            if (parsedState.Invalid)
                return Results.BadRequest(new { error = $"Unknown desktop snapshot state: {state}" });

            var snapshots = await repo.ListGitSnapshotsAsync(new DesktopGitSnapshotListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                WorkspaceId = workspaceId,
                SourceInstanceId = sourceInstanceId,
                RootPath = rootPath,
                State = parsedState.State,
                StaleAfter = StaleAfter(staleAfterSeconds),
                Limit = limit ?? 50
            });
            return Results.Ok(snapshots);
        });

        group.MapGet("/git-snapshots/latest", async (
            IDesktopSnapshotRepository repo,
            string projectId,
            int? taskId,
            string? workspaceId,
            string? sourceInstanceId,
            string? rootPath,
            int? staleAfterSeconds) =>
        {
            var latest = await repo.GetLatestGitSnapshotAsync(new DesktopGitSnapshotListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                WorkspaceId = workspaceId,
                SourceInstanceId = sourceInstanceId,
                RootPath = rootPath,
                StaleAfter = StaleAfter(staleAfterSeconds),
                Limit = 1
            });
            return Results.Ok(latest);
        });

        group.MapPut("/diff-snapshots", async (
            IDesktopSnapshotRepository repo,
            string projectId,
            UpsertDesktopDiffSnapshotRequest req) =>
        {
            try
            {
                var snapshot = BuildDiffSnapshot(projectId, req);
                var saved = await repo.UpsertDiffSnapshotAsync(snapshot);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.BadRequest(new { error = "Desktop diff snapshot references an unknown project, task, or workspace." });
            }
        });

        group.MapGet("/diff-snapshots/latest", async (
            IDesktopSnapshotRepository repo,
            string projectId,
            int? taskId,
            string? workspaceId,
            string? sourceInstanceId,
            string? rootPath,
            string? path,
            string? baseRef,
            string? headRef,
            bool? staged,
            int? staleAfterSeconds) =>
        {
            if (string.IsNullOrWhiteSpace(sourceInstanceId) || string.IsNullOrWhiteSpace(rootPath))
                return Results.BadRequest(new { error = "sourceInstanceId and rootPath are required." });

            var key = new DesktopDiffSnapshot
            {
                ProjectId = projectId,
                TaskId = taskId,
                WorkspaceId = workspaceId,
                SourceInstanceId = sourceInstanceId,
                RootPath = rootPath,
                Path = path,
                BaseRef = baseRef,
                HeadRef = headRef,
                Staged = staged ?? false,
                MaxBytes = 1,
                ObservedAt = DateTime.UtcNow
            };
            var latest = await repo.GetLatestDiffSnapshotAsync(key, StaleAfter(staleAfterSeconds));
            var result = latest is null
                ? new DesktopDiffSnapshotLatestResult
                {
                    ProjectId = projectId,
                    TaskId = taskId,
                    WorkspaceId = workspaceId,
                    RootPath = rootPath,
                    Path = path,
                    SourceInstanceId = sourceInstanceId,
                    State = DesktopSnapshotState.Missing,
                    IsStale = false,
                    FreshnessStatus = "missing"
                }
                : new DesktopDiffSnapshotLatestResult
                {
                    ProjectId = latest.ProjectId,
                    TaskId = latest.TaskId,
                    WorkspaceId = latest.WorkspaceId,
                    RootPath = latest.RootPath,
                    Path = latest.Path,
                    SourceInstanceId = latest.SourceInstanceId,
                    State = latest.IsStale ? DesktopSnapshotState.SourceOffline : DesktopSnapshotState.Ok,
                    IsStale = latest.IsStale,
                    FreshnessStatus = latest.IsStale ? "stale" : "fresh",
                    Snapshot = latest
                };
            return Results.Ok(result);
        });

        group.MapPut("/session-snapshots", async (
            IDesktopSnapshotRepository repo,
            string projectId,
            UpsertDesktopSessionSnapshotRequest req) =>
        {
            try
            {
                var snapshot = BuildSessionSnapshot(projectId, req);
                var saved = await repo.UpsertSessionSnapshotAsync(snapshot);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.BadRequest(new { error = "Desktop session snapshot references an unknown project, task, or workspace." });
            }
        });

        group.MapGet("/session-snapshots", async (
            IDesktopSnapshotRepository repo,
            string projectId,
            int? taskId,
            string? workspaceId,
            string? sourceInstanceId,
            string? sessionId,
            int? staleAfterSeconds,
            int? limit) =>
        {
            var snapshots = await repo.ListSessionSnapshotsAsync(new DesktopSessionSnapshotListOptions
            {
                ProjectId = projectId,
                TaskId = taskId,
                WorkspaceId = workspaceId,
                SourceInstanceId = sourceInstanceId,
                SessionId = sessionId,
                StaleAfter = StaleAfter(staleAfterSeconds),
                Limit = limit ?? 50
            });
            return Results.Ok(snapshots);
        });
    }

    private static DesktopGitSnapshot BuildGitSnapshot(string projectId, UpsertDesktopGitSnapshotRequest req) => new()
    {
        ProjectId = projectId.Trim(),
        TaskId = req.TaskId,
        WorkspaceId = TrimToNull(req.WorkspaceId),
        RootPath = req.RootPath?.Trim() ?? string.Empty,
        State = req.State ?? DesktopSnapshotState.Ok,
        Branch = TrimToNull(req.Branch),
        IsDetached = req.IsDetached ?? false,
        HeadSha = TrimToNull(req.HeadSha),
        Upstream = TrimToNull(req.Upstream),
        Ahead = req.Ahead,
        Behind = req.Behind,
        DirtyCounts = req.DirtyCounts ?? new GitDirtyCounts(),
        ChangedFiles = req.ChangedFiles ?? [],
        Warnings = req.Warnings ?? [],
        Truncated = req.Truncated ?? false,
        SourceInstanceId = req.SourceInstanceId?.Trim() ?? string.Empty,
        SourceDisplayName = TrimToNull(req.SourceDisplayName),
        ObservedAt = req.ObservedAt ?? DateTime.UtcNow
    };

    private static DesktopDiffSnapshot BuildDiffSnapshot(string projectId, UpsertDesktopDiffSnapshotRequest req) => new()
    {
        ProjectId = projectId.Trim(),
        TaskId = req.TaskId,
        WorkspaceId = TrimToNull(req.WorkspaceId),
        RootPath = req.RootPath?.Trim() ?? string.Empty,
        Path = TrimToNull(req.Path),
        BaseRef = TrimToNull(req.BaseRef),
        HeadRef = TrimToNull(req.HeadRef),
        MaxBytes = req.MaxBytes ?? Math.Max(req.Diff?.Length ?? 0, 1),
        Staged = req.Staged ?? false,
        Diff = req.Diff ?? string.Empty,
        Truncated = req.Truncated ?? false,
        Binary = req.Binary ?? false,
        Warnings = req.Warnings ?? [],
        SourceInstanceId = req.SourceInstanceId?.Trim() ?? string.Empty,
        SourceDisplayName = TrimToNull(req.SourceDisplayName),
        ObservedAt = req.ObservedAt ?? DateTime.UtcNow
    };

    private static DesktopSessionSnapshot BuildSessionSnapshot(string projectId, UpsertDesktopSessionSnapshotRequest req) => new()
    {
        ProjectId = projectId.Trim(),
        TaskId = req.TaskId,
        WorkspaceId = TrimToNull(req.WorkspaceId),
        SessionId = req.SessionId?.Trim() ?? string.Empty,
        ParentSessionId = TrimToNull(req.ParentSessionId),
        AgentIdentity = TrimToNull(req.AgentIdentity),
        Role = TrimToNull(req.Role),
        CurrentCommand = TrimToNull(req.CurrentCommand),
        CurrentPhase = TrimToNull(req.CurrentPhase),
        Title = TrimToNull(req.Title),
        DisplayName = TrimToNull(req.DisplayName),
        Cwd = TrimToNull(req.Cwd),
        Kind = TrimToNull(req.Kind),
        Backend = TrimToNull(req.Backend),
        Status = TrimToNull(req.Status),
        StartedAt = req.StartedAt,
        LastActivityAt = req.LastActivityAt,
        ExitedAt = req.ExitedAt,
        ExitCode = req.ExitCode,
        SourceDisplayName = TrimToNull(req.SourceDisplayName),
        Capabilities = req.Capabilities?.Clone(),
        RecentActivity = req.RecentActivity?.Clone(),
        ChildSessions = req.ChildSessions?.Clone(),
        ControlCapabilities = req.ControlCapabilities?.Clone(),
        Warnings = req.Warnings ?? [],
        SourceInstanceId = req.SourceInstanceId?.Trim() ?? string.Empty,
        ObservedAt = req.ObservedAt ?? DateTime.UtcNow
    };

    private static (DesktopSnapshotState? State, bool Invalid) ParseSnapshotState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return (null, false);

        try
        {
            return (EnumExtensions.ParseDesktopSnapshotState(state.Trim()), false);
        }
        catch (ArgumentException)
        {
            return (null, true);
        }
    }

    private static TimeSpan StaleAfter(int? seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds ?? 120, 1, 86_400));
    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record UpsertDesktopGitSnapshotRequest
{
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RootPath { get; init; }
    public DesktopSnapshotState? State { get; init; }
    public string? Branch { get; init; }
    public bool? IsDetached { get; init; }
    public string? HeadSha { get; init; }
    public string? Upstream { get; init; }
    public int? Ahead { get; init; }
    public int? Behind { get; init; }
    public GitDirtyCounts? DirtyCounts { get; init; }
    public List<GitFileStatus>? ChangedFiles { get; init; }
    public List<string>? Warnings { get; init; }
    public bool? Truncated { get; init; }
    public string? SourceInstanceId { get; init; }
    public string? SourceDisplayName { get; init; }
    public DateTime? ObservedAt { get; init; }
}

public sealed record UpsertDesktopDiffSnapshotRequest
{
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RootPath { get; init; }
    public string? Path { get; init; }
    public string? BaseRef { get; init; }
    public string? HeadRef { get; init; }
    public int? MaxBytes { get; init; }
    public bool? Staged { get; init; }
    public string? Diff { get; init; }
    public bool? Truncated { get; init; }
    public bool? Binary { get; init; }
    public List<string>? Warnings { get; init; }
    public string? SourceInstanceId { get; init; }
    public string? SourceDisplayName { get; init; }
    public DateTime? ObservedAt { get; init; }
}

public sealed record UpsertDesktopSessionSnapshotRequest
{
    public int? TaskId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? ParentSessionId { get; init; }
    public string? AgentIdentity { get; init; }
    public string? Role { get; init; }
    public string? CurrentCommand { get; init; }
    public string? CurrentPhase { get; init; }
    public string? Title { get; init; }
    public string? DisplayName { get; init; }
    public string? Cwd { get; init; }
    public string? Kind { get; init; }
    public string? Backend { get; init; }
    public string? Status { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public DateTime? ExitedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? SourceDisplayName { get; init; }
    public JsonElement? Capabilities { get; init; }
    public JsonElement? RecentActivity { get; init; }
    public JsonElement? ChildSessions { get; init; }
    public JsonElement? ControlCapabilities { get; init; }
    public List<string>? Warnings { get; init; }
    public string? SourceInstanceId { get; init; }
    public DateTime? ObservedAt { get; init; }
}
