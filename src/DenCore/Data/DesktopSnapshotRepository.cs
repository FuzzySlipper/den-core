using System.Globalization;
using System.Data.Common;
using System.Text.Json;
using DenCore.Models;

namespace DenCore.Data;

public interface IDesktopSnapshotRepository
{
    Task<DesktopGitSnapshot> UpsertGitSnapshotAsync(DesktopGitSnapshot snapshot);
    Task<List<DesktopGitSnapshot>> ListGitSnapshotsAsync(DesktopGitSnapshotListOptions options);
    Task<DesktopGitSnapshotLatestResult> GetLatestGitSnapshotAsync(DesktopGitSnapshotListOptions options);
    Task<DesktopDiffSnapshot> UpsertDiffSnapshotAsync(DesktopDiffSnapshot snapshot);
    Task<DesktopDiffSnapshot?> GetLatestDiffSnapshotAsync(DesktopDiffSnapshot snapshotKey, TimeSpan staleAfter);
    Task<DesktopSessionSnapshot> UpsertSessionSnapshotAsync(DesktopSessionSnapshot snapshot);
    Task<List<DesktopSessionSnapshot>> ListSessionSnapshotsAsync(DesktopSessionSnapshotListOptions options);
}

public sealed class DesktopSnapshotRepository : IDesktopSnapshotRepository
{
    private const string GitColumns = """
        id, project_id, task_id, workspace_id, root_path, state, branch, is_detached,
        head_sha, upstream, ahead, behind, dirty_counts, changed_files, warnings,
        truncated, source_instance_id, source_display_name, observed_at, received_at, updated_at
        """;

    private const string DiffColumns = """
        id, project_id, task_id, workspace_id, root_path, path, base_ref, head_ref,
        max_bytes, staged, diff, truncated, is_binary, warnings, source_instance_id,
        source_display_name, observed_at, received_at, updated_at
        """;

    private const string SessionColumns = """
        id, project_id, task_id, workspace_id, session_id, parent_session_id, agent_identity,
        role, current_command, current_phase, title, display_name, cwd, kind, backend, status,
        started_at, last_activity_at, exited_at, exit_code, source_display_name, capabilities,
        recent_activity, child_sessions, control_capabilities, warnings, source_instance_id,
        observed_at, received_at, updated_at
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DbConnectionFactory _db;
    private readonly Func<DateTime> _utcNow;

    public DesktopSnapshotRepository(DbConnectionFactory db, Func<DateTime>? utcNow = null)
    {
        _db = db;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<DesktopGitSnapshot> UpsertGitSnapshotAsync(DesktopGitSnapshot snapshot)
    {
        ValidateGitSnapshot(snapshot);
        var now = _utcNow();
        snapshot.ReceivedAt = now;
        snapshot.UpdatedAt = now;

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO desktop_git_snapshots (
                project_id, task_id, workspace_id, root_path, scope_key, state, branch,
                is_detached, head_sha, upstream, ahead, behind, dirty_counts, changed_files,
                warnings, truncated, source_instance_id, source_display_name, observed_at,
                received_at, updated_at
            ) VALUES (
                @projectId, @taskId, @workspaceId, @rootPath, @scopeKey, @state, @branch,
                @isDetached, @headSha, @upstream, @ahead, @behind, @dirtyCounts, @changedFiles,
                @warnings, @truncated, @sourceInstanceId, @sourceDisplayName, @observedAt,
                @receivedAt, @updatedAt
            )
            ON CONFLICT(project_id, scope_key) DO UPDATE SET
                task_id = excluded.task_id,
                workspace_id = excluded.workspace_id,
                root_path = excluded.root_path,
                state = excluded.state,
                branch = excluded.branch,
                is_detached = excluded.is_detached,
                head_sha = excluded.head_sha,
                upstream = excluded.upstream,
                ahead = excluded.ahead,
                behind = excluded.behind,
                dirty_counts = excluded.dirty_counts,
                changed_files = excluded.changed_files,
                warnings = excluded.warnings,
                truncated = excluded.truncated,
                source_instance_id = excluded.source_instance_id,
                source_display_name = excluded.source_display_name,
                observed_at = excluded.observed_at,
                received_at = excluded.received_at,
                updated_at = excluded.updated_at
            RETURNING {GitColumns}
            """;
        AddGitParameters(cmd, snapshot);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadGitSnapshot(reader, TimeSpan.FromMinutes(2));
    }

    public async Task<List<DesktopGitSnapshot>> ListGitSnapshotsAsync(DesktopGitSnapshotListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var where = BuildGitWhere(cmd, options);
        cmd.CommandText = $"""
            SELECT {GitColumns}
            FROM desktop_git_snapshots
            {where}
            ORDER BY observed_at DESC, updated_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.AddParameterWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var result = new List<DesktopGitSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(ReadGitSnapshot(reader, options.StaleAfter));
        return result;
    }

    public async Task<DesktopGitSnapshotLatestResult> GetLatestGitSnapshotAsync(DesktopGitSnapshotListOptions options)
    {
        var latest = (await ListGitSnapshotsAsync(WithLimit(1))).FirstOrDefault();
        if (latest is null)
        {
            return new DesktopGitSnapshotLatestResult
            {
                ProjectId = options.ProjectId ?? string.Empty,
                TaskId = options.TaskId,
                WorkspaceId = options.WorkspaceId,
                RootPath = options.RootPath,
                SourceInstanceId = options.SourceInstanceId,
                State = DesktopSnapshotState.Missing,
                IsStale = false,
                FreshnessStatus = "missing"
            };
        }

        return new DesktopGitSnapshotLatestResult
        {
            ProjectId = latest.ProjectId,
            TaskId = latest.TaskId,
            WorkspaceId = latest.WorkspaceId,
            RootPath = latest.RootPath,
            SourceInstanceId = latest.SourceInstanceId,
            State = latest.IsStale ? DesktopSnapshotState.SourceOffline : latest.State,
            IsStale = latest.IsStale,
            FreshnessStatus = latest.IsStale ? "stale" : "fresh",
            Snapshot = latest
        };

        DesktopGitSnapshotListOptions WithLimit(int limit) => new()
        {
            ProjectId = options.ProjectId,
            TaskId = options.TaskId,
            WorkspaceId = options.WorkspaceId,
            SourceInstanceId = options.SourceInstanceId,
            RootPath = options.RootPath,
            State = options.State,
            StaleAfter = options.StaleAfter,
            Limit = limit
        };
    }

    public async Task<DesktopDiffSnapshot> UpsertDiffSnapshotAsync(DesktopDiffSnapshot snapshot)
    {
        ValidateDiffSnapshot(snapshot);
        var now = _utcNow();
        snapshot.ReceivedAt = now;
        snapshot.UpdatedAt = now;

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO desktop_diff_snapshots (
                project_id, task_id, workspace_id, root_path, path, base_ref, head_ref,
                diff_key, max_bytes, staged, diff, truncated, is_binary, warnings,
                source_instance_id, source_display_name, observed_at, received_at, updated_at
            ) VALUES (
                @projectId, @taskId, @workspaceId, @rootPath, @path, @baseRef, @headRef,
                @diffKey, @maxBytes, @staged, @diff, @truncated, @binary, @warnings,
                @sourceInstanceId, @sourceDisplayName, @observedAt, @receivedAt, @updatedAt
            )
            ON CONFLICT(project_id, diff_key) DO UPDATE SET
                task_id = excluded.task_id,
                workspace_id = excluded.workspace_id,
                root_path = excluded.root_path,
                path = excluded.path,
                base_ref = excluded.base_ref,
                head_ref = excluded.head_ref,
                max_bytes = excluded.max_bytes,
                staged = excluded.staged,
                diff = excluded.diff,
                truncated = excluded.truncated,
                is_binary = excluded.is_binary,
                warnings = excluded.warnings,
                source_instance_id = excluded.source_instance_id,
                source_display_name = excluded.source_display_name,
                observed_at = excluded.observed_at,
                received_at = excluded.received_at,
                updated_at = excluded.updated_at
            RETURNING {DiffColumns}
            """;
        AddDiffParameters(cmd, snapshot);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadDiffSnapshot(reader, TimeSpan.FromMinutes(2));
    }

    public async Task<DesktopDiffSnapshot?> GetLatestDiffSnapshotAsync(DesktopDiffSnapshot snapshotKey, TimeSpan staleAfter)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {DiffColumns}
            FROM desktop_diff_snapshots
            WHERE project_id = @projectId
              AND diff_key = @diffKey
            ORDER BY observed_at DESC, updated_at DESC, id DESC
            LIMIT 1
            """;
        cmd.AddParameterWithValue("@projectId", (object?)snapshotKey.ProjectId ?? DBNull.Value);
        cmd.AddParameterWithValue("@diffKey", BuildDiffKey(snapshotKey));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDiffSnapshot(reader, staleAfter) : null;
    }

    public async Task<DesktopSessionSnapshot> UpsertSessionSnapshotAsync(DesktopSessionSnapshot snapshot)
    {
        ValidateSessionSnapshot(snapshot);
        var now = _utcNow();
        snapshot.ReceivedAt = now;
        snapshot.UpdatedAt = now;

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO desktop_session_snapshots (
                project_id, task_id, workspace_id, session_id, parent_session_id,
                agent_identity, role, current_command, current_phase, title, display_name,
                cwd, kind, backend, status, started_at, last_activity_at, exited_at,
                exit_code, source_display_name, capabilities, recent_activity,
                child_sessions, control_capabilities, warnings, source_instance_id,
                observed_at, received_at, updated_at
            ) VALUES (
                @projectId, @taskId, @workspaceId, @sessionId, @parentSessionId,
                @agentIdentity, @role, @currentCommand, @currentPhase, @title, @displayName,
                @cwd, @kind, @backend, @status, @startedAt, @lastActivityAt, @exitedAt,
                @exitCode, @sourceDisplayName, @capabilities, @recentActivity,
                @childSessions, @controlCapabilities, @warnings, @sourceInstanceId,
                @observedAt, @receivedAt, @updatedAt
            )
            ON CONFLICT(project_id, source_instance_id, session_id) DO UPDATE SET
                task_id = excluded.task_id,
                workspace_id = excluded.workspace_id,
                parent_session_id = excluded.parent_session_id,
                agent_identity = excluded.agent_identity,
                role = excluded.role,
                current_command = excluded.current_command,
                current_phase = excluded.current_phase,
                title = excluded.title,
                display_name = excluded.display_name,
                cwd = excluded.cwd,
                kind = excluded.kind,
                backend = excluded.backend,
                status = excluded.status,
                started_at = excluded.started_at,
                last_activity_at = excluded.last_activity_at,
                exited_at = excluded.exited_at,
                exit_code = excluded.exit_code,
                source_display_name = excluded.source_display_name,
                capabilities = excluded.capabilities,
                recent_activity = excluded.recent_activity,
                child_sessions = excluded.child_sessions,
                control_capabilities = excluded.control_capabilities,
                warnings = excluded.warnings,
                observed_at = excluded.observed_at,
                received_at = excluded.received_at,
                updated_at = excluded.updated_at
            RETURNING {SessionColumns}
            """;
        AddSessionParameters(cmd, snapshot);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadSessionSnapshot(reader, TimeSpan.FromMinutes(2));
    }

    public async Task<List<DesktopSessionSnapshot>> ListSessionSnapshotsAsync(DesktopSessionSnapshotListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.AddParameterWithValue("@projectId", options.ProjectId.Trim());
        }
        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.AddParameterWithValue("@taskId", options.TaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.WorkspaceId))
        {
            where.Add("workspace_id = @workspaceId");
            cmd.AddParameterWithValue("@workspaceId", options.WorkspaceId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.SourceInstanceId))
        {
            where.Add("source_instance_id = @sourceInstanceId");
            cmd.AddParameterWithValue("@sourceInstanceId", options.SourceInstanceId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            where.Add("session_id = @sessionId");
            cmd.AddParameterWithValue("@sessionId", options.SessionId.Trim());
        }

        var whereClause = where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
        cmd.CommandText = $"""
            SELECT {SessionColumns}
            FROM desktop_session_snapshots
            {whereClause}
            ORDER BY observed_at DESC, updated_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.AddParameterWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var result = new List<DesktopSessionSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(ReadSessionSnapshot(reader, options.StaleAfter));
        return result;
    }

    private string BuildGitWhere(DbCommand cmd, DesktopGitSnapshotListOptions options)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.AddParameterWithValue("@projectId", options.ProjectId.Trim());
        }
        if (options.TaskId is not null)
        {
            where.Add("task_id = @taskId");
            cmd.AddParameterWithValue("@taskId", options.TaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.WorkspaceId))
        {
            where.Add("workspace_id = @workspaceId");
            cmd.AddParameterWithValue("@workspaceId", options.WorkspaceId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.SourceInstanceId))
        {
            where.Add("source_instance_id = @sourceInstanceId");
            cmd.AddParameterWithValue("@sourceInstanceId", options.SourceInstanceId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.RootPath))
        {
            where.Add("root_path = @rootPath");
            cmd.AddParameterWithValue("@rootPath", options.RootPath.Trim());
        }
        if (options.State is not null)
        {
            where.Add("state = @state");
            cmd.AddParameterWithValue("@state", options.State.Value.ToDbValue());
        }
        return where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
    }

    private void AddGitParameters(DbCommand cmd, DesktopGitSnapshot snapshot)
    {
        cmd.AddParameterWithValue("@projectId", (object?)snapshot.ProjectId ?? DBNull.Value);
        cmd.AddParameterWithValue("@taskId", (object?)snapshot.TaskId ?? DBNull.Value);
        cmd.AddParameterWithValue("@workspaceId", NullIfWhiteSpace(snapshot.WorkspaceId));
        cmd.AddParameterWithValue("@rootPath", snapshot.RootPath.Trim());
        cmd.AddParameterWithValue("@scopeKey", BuildGitScopeKey(snapshot));
        cmd.AddParameterWithValue("@state", snapshot.State.ToDbValue());
        cmd.AddParameterWithValue("@branch", NullIfWhiteSpace(snapshot.Branch));
        cmd.AddParameterWithValue("@isDetached", snapshot.IsDetached ? 1 : 0);
        cmd.AddParameterWithValue("@headSha", NullIfWhiteSpace(snapshot.HeadSha));
        cmd.AddParameterWithValue("@upstream", NullIfWhiteSpace(snapshot.Upstream));
        cmd.AddParameterWithValue("@ahead", (object?)snapshot.Ahead ?? DBNull.Value);
        cmd.AddParameterWithValue("@behind", (object?)snapshot.Behind ?? DBNull.Value);
        cmd.AddParameterWithValue("@dirtyCounts", JsonSerializer.Serialize(snapshot.DirtyCounts, JsonOptions));
        cmd.AddParameterWithValue("@changedFiles", JsonSerializer.Serialize(snapshot.ChangedFiles, JsonOptions));
        cmd.AddParameterWithValue("@warnings", JsonSerializer.Serialize(snapshot.Warnings, JsonOptions));
        cmd.AddParameterWithValue("@truncated", snapshot.Truncated ? 1 : 0);
        cmd.AddParameterWithValue("@sourceInstanceId", snapshot.SourceInstanceId.Trim());
        cmd.AddParameterWithValue("@sourceDisplayName", NullIfWhiteSpace(snapshot.SourceDisplayName));
        cmd.AddParameterWithValue("@observedAt", ToDbTime(snapshot.ObservedAt));
        cmd.AddParameterWithValue("@receivedAt", ToDbTime(snapshot.ReceivedAt));
        cmd.AddParameterWithValue("@updatedAt", ToDbTime(snapshot.UpdatedAt));
    }

    private void AddDiffParameters(DbCommand cmd, DesktopDiffSnapshot snapshot)
    {
        cmd.AddParameterWithValue("@projectId", (object?)snapshot.ProjectId ?? DBNull.Value);
        cmd.AddParameterWithValue("@taskId", (object?)snapshot.TaskId ?? DBNull.Value);
        cmd.AddParameterWithValue("@workspaceId", NullIfWhiteSpace(snapshot.WorkspaceId));
        cmd.AddParameterWithValue("@rootPath", snapshot.RootPath.Trim());
        cmd.AddParameterWithValue("@path", NullIfWhiteSpace(snapshot.Path));
        cmd.AddParameterWithValue("@baseRef", NullIfWhiteSpace(snapshot.BaseRef));
        cmd.AddParameterWithValue("@headRef", NullIfWhiteSpace(snapshot.HeadRef));
        cmd.AddParameterWithValue("@diffKey", BuildDiffKey(snapshot));
        cmd.AddParameterWithValue("@maxBytes", snapshot.MaxBytes);
        cmd.AddParameterWithValue("@staged", snapshot.Staged ? 1 : 0);
        cmd.AddParameterWithValue("@diff", snapshot.Diff);
        cmd.AddParameterWithValue("@truncated", snapshot.Truncated ? 1 : 0);
        cmd.AddParameterWithValue("@binary", snapshot.Binary ? 1 : 0);
        cmd.AddParameterWithValue("@warnings", JsonSerializer.Serialize(snapshot.Warnings, JsonOptions));
        cmd.AddParameterWithValue("@sourceInstanceId", snapshot.SourceInstanceId.Trim());
        cmd.AddParameterWithValue("@sourceDisplayName", NullIfWhiteSpace(snapshot.SourceDisplayName));
        cmd.AddParameterWithValue("@observedAt", ToDbTime(snapshot.ObservedAt));
        cmd.AddParameterWithValue("@receivedAt", ToDbTime(snapshot.ReceivedAt));
        cmd.AddParameterWithValue("@updatedAt", ToDbTime(snapshot.UpdatedAt));
    }

    private void AddSessionParameters(DbCommand cmd, DesktopSessionSnapshot snapshot)
    {
        cmd.AddParameterWithValue("@projectId", (object?)snapshot.ProjectId ?? DBNull.Value);
        cmd.AddParameterWithValue("@taskId", (object?)snapshot.TaskId ?? DBNull.Value);
        cmd.AddParameterWithValue("@workspaceId", NullIfWhiteSpace(snapshot.WorkspaceId));
        cmd.AddParameterWithValue("@sessionId", snapshot.SessionId.Trim());
        cmd.AddParameterWithValue("@parentSessionId", NullIfWhiteSpace(snapshot.ParentSessionId));
        cmd.AddParameterWithValue("@agentIdentity", NullIfWhiteSpace(snapshot.AgentIdentity));
        cmd.AddParameterWithValue("@role", NullIfWhiteSpace(snapshot.Role));
        cmd.AddParameterWithValue("@currentCommand", NullIfWhiteSpace(snapshot.CurrentCommand));
        cmd.AddParameterWithValue("@currentPhase", NullIfWhiteSpace(snapshot.CurrentPhase));
        cmd.AddParameterWithValue("@title", NullIfWhiteSpace(snapshot.Title));
        cmd.AddParameterWithValue("@displayName", NullIfWhiteSpace(snapshot.DisplayName));
        cmd.AddParameterWithValue("@cwd", NullIfWhiteSpace(snapshot.Cwd));
        cmd.AddParameterWithValue("@kind", NullIfWhiteSpace(snapshot.Kind));
        cmd.AddParameterWithValue("@backend", NullIfWhiteSpace(snapshot.Backend));
        cmd.AddParameterWithValue("@status", NullIfWhiteSpace(snapshot.Status));
        cmd.AddParameterWithValue("@startedAt", snapshot.StartedAt is null ? DBNull.Value : ToDbTime(snapshot.StartedAt.Value));
        cmd.AddParameterWithValue("@lastActivityAt", snapshot.LastActivityAt is null ? DBNull.Value : ToDbTime(snapshot.LastActivityAt.Value));
        cmd.AddParameterWithValue("@exitedAt", snapshot.ExitedAt is null ? DBNull.Value : ToDbTime(snapshot.ExitedAt.Value));
        cmd.AddParameterWithValue("@exitCode", (object?)snapshot.ExitCode ?? DBNull.Value);
        cmd.AddParameterWithValue("@sourceDisplayName", NullIfWhiteSpace(snapshot.SourceDisplayName));
        cmd.AddParameterWithValue("@capabilities", JsonOrNull(snapshot.Capabilities));
        cmd.AddParameterWithValue("@recentActivity", JsonOrNull(snapshot.RecentActivity));
        cmd.AddParameterWithValue("@childSessions", JsonOrNull(snapshot.ChildSessions));
        cmd.AddParameterWithValue("@controlCapabilities", JsonOrNull(snapshot.ControlCapabilities));
        cmd.AddParameterWithValue("@warnings", JsonSerializer.Serialize(snapshot.Warnings, JsonOptions));
        cmd.AddParameterWithValue("@sourceInstanceId", snapshot.SourceInstanceId.Trim());
        cmd.AddParameterWithValue("@observedAt", ToDbTime(snapshot.ObservedAt));
        cmd.AddParameterWithValue("@receivedAt", ToDbTime(snapshot.ReceivedAt));
        cmd.AddParameterWithValue("@updatedAt", ToDbTime(snapshot.UpdatedAt));
    }

    private DesktopGitSnapshot ReadGitSnapshot(DbDataReader reader, TimeSpan staleAfter)
    {
        var observedAt = FromDbTime(reader.GetString(18));
        var freshnessSeconds = FreshnessSeconds(observedAt);
        return new DesktopGitSnapshot
        {
            Id = reader.GetInt64(0),
            ProjectId = reader.IsDBNull(1) ? null : reader.GetString(1),
            TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            RootPath = reader.GetString(4),
            State = EnumExtensions.ParseDesktopSnapshotState(reader.GetString(5)),
            Branch = reader.IsDBNull(6) ? null : reader.GetString(6),
            IsDetached = reader.GetInt32(7) != 0,
            HeadSha = reader.IsDBNull(8) ? null : reader.GetString(8),
            Upstream = reader.IsDBNull(9) ? null : reader.GetString(9),
            Ahead = reader.IsDBNull(10) ? null : reader.GetInt32(10),
            Behind = reader.IsDBNull(11) ? null : reader.GetInt32(11),
            DirtyCounts = JsonSerializer.Deserialize<GitDirtyCounts>(reader.GetString(12), JsonOptions) ?? new GitDirtyCounts(),
            ChangedFiles = JsonSerializer.Deserialize<List<GitFileStatus>>(reader.GetString(13), JsonOptions) ?? [],
            Warnings = JsonSerializer.Deserialize<List<string>>(reader.GetString(14), JsonOptions) ?? [],
            Truncated = reader.GetInt32(15) != 0,
            SourceInstanceId = reader.GetString(16),
            SourceDisplayName = reader.IsDBNull(17) ? null : reader.GetString(17),
            ObservedAt = observedAt,
            ReceivedAt = FromDbTime(reader.GetString(19)),
            UpdatedAt = FromDbTime(reader.GetString(20)),
            IsStale = IsStale(observedAt, staleAfter),
            FreshnessSeconds = freshnessSeconds
        };
    }

    private DesktopDiffSnapshot ReadDiffSnapshot(DbDataReader reader, TimeSpan staleAfter)
    {
        var observedAt = FromDbTime(reader.GetString(16));
        return new DesktopDiffSnapshot
        {
            Id = reader.GetInt64(0),
            ProjectId = reader.IsDBNull(1) ? null : reader.GetString(1),
            TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            RootPath = reader.GetString(4),
            Path = reader.IsDBNull(5) ? null : reader.GetString(5),
            BaseRef = reader.IsDBNull(6) ? null : reader.GetString(6),
            HeadRef = reader.IsDBNull(7) ? null : reader.GetString(7),
            MaxBytes = reader.GetInt32(8),
            Staged = reader.GetInt32(9) != 0,
            Diff = reader.GetString(10),
            Truncated = reader.GetInt32(11) != 0,
            Binary = reader.GetInt32(12) != 0,
            Warnings = JsonSerializer.Deserialize<List<string>>(reader.GetString(13), JsonOptions) ?? [],
            SourceInstanceId = reader.GetString(14),
            SourceDisplayName = reader.IsDBNull(15) ? null : reader.GetString(15),
            ObservedAt = observedAt,
            ReceivedAt = FromDbTime(reader.GetString(17)),
            UpdatedAt = FromDbTime(reader.GetString(18)),
            IsStale = IsStale(observedAt, staleAfter),
            FreshnessSeconds = FreshnessSeconds(observedAt)
        };
    }

    private DesktopSessionSnapshot ReadSessionSnapshot(DbDataReader reader, TimeSpan staleAfter)
    {
        var observedAt = FromDbTime(reader.GetString(27));
        return new DesktopSessionSnapshot
        {
            Id = reader.GetInt64(0),
            ProjectId = reader.IsDBNull(1) ? null : reader.GetString(1),
            TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            SessionId = reader.GetString(4),
            ParentSessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
            AgentIdentity = reader.IsDBNull(6) ? null : reader.GetString(6),
            Role = reader.IsDBNull(7) ? null : reader.GetString(7),
            CurrentCommand = reader.IsDBNull(8) ? null : reader.GetString(8),
            CurrentPhase = reader.IsDBNull(9) ? null : reader.GetString(9),
            Title = reader.IsDBNull(10) ? null : reader.GetString(10),
            DisplayName = reader.IsDBNull(11) ? null : reader.GetString(11),
            Cwd = reader.IsDBNull(12) ? null : reader.GetString(12),
            Kind = reader.IsDBNull(13) ? null : reader.GetString(13),
            Backend = reader.IsDBNull(14) ? null : reader.GetString(14),
            Status = reader.IsDBNull(15) ? null : reader.GetString(15),
            StartedAt = reader.IsDBNull(16) ? null : FromDbTime(reader.GetString(16)),
            LastActivityAt = reader.IsDBNull(17) ? null : FromDbTime(reader.GetString(17)),
            ExitedAt = reader.IsDBNull(18) ? null : FromDbTime(reader.GetString(18)),
            ExitCode = reader.IsDBNull(19) ? null : reader.GetInt32(19),
            SourceDisplayName = reader.IsDBNull(20) ? null : reader.GetString(20),
            Capabilities = reader.IsDBNull(21) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(21)).Clone(),
            RecentActivity = reader.IsDBNull(22) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(22)).Clone(),
            ChildSessions = reader.IsDBNull(23) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(23)).Clone(),
            ControlCapabilities = reader.IsDBNull(24) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(24)).Clone(),
            Warnings = JsonSerializer.Deserialize<List<string>>(reader.GetString(25), JsonOptions) ?? [],
            SourceInstanceId = reader.GetString(26),
            ObservedAt = observedAt,
            ReceivedAt = FromDbTime(reader.GetString(28)),
            UpdatedAt = FromDbTime(reader.GetString(29)),
            IsStale = IsStale(observedAt, staleAfter),
            FreshnessSeconds = FreshnessSeconds(observedAt)
        };
    }

    private static void ValidateGitSnapshot(DesktopGitSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ProjectId)) throw new ArgumentException("Project id is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.RootPath)) throw new ArgumentException("Root path is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.SourceInstanceId)) throw new ArgumentException("Source instance id is required.", nameof(snapshot));
        if (snapshot.ObservedAt == default) throw new ArgumentException("Observed at is required.", nameof(snapshot));
        if (snapshot.ChangedFiles.Count > 1_000) throw new ArgumentException("Changed file list is limited to 1000 entries.", nameof(snapshot));
    }

    private static void ValidateDiffSnapshot(DesktopDiffSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ProjectId)) throw new ArgumentException("Project id is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.RootPath)) throw new ArgumentException("Root path is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.SourceInstanceId)) throw new ArgumentException("Source instance id is required.", nameof(snapshot));
        if (snapshot.ObservedAt == default) throw new ArgumentException("Observed at is required.", nameof(snapshot));
        if (snapshot.MaxBytes <= 0) throw new ArgumentException("Max bytes must be positive.", nameof(snapshot));
        if (snapshot.Diff.Length > snapshot.MaxBytes * 2) throw new ArgumentException("Diff text is too large for the declared bound.", nameof(snapshot));
    }

    private static void ValidateSessionSnapshot(DesktopSessionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ProjectId)) throw new ArgumentException("Project id is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.SessionId)) throw new ArgumentException("Session id is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.SourceInstanceId)) throw new ArgumentException("Source instance id is required.", nameof(snapshot));
        if (snapshot.ObservedAt == default) throw new ArgumentException("Observed at is required.", nameof(snapshot));
    }

    private static string BuildGitScopeKey(DesktopGitSnapshot snapshot) => string.Join("\u001f", [
        snapshot.SourceInstanceId.Trim(),
        snapshot.WorkspaceId?.Trim() ?? string.Empty,
        snapshot.TaskId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        snapshot.RootPath.Trim()
    ]);

    private static string BuildDiffKey(DesktopDiffSnapshot snapshot) => string.Join("\u001f", [
        snapshot.SourceInstanceId.Trim(),
        snapshot.WorkspaceId?.Trim() ?? string.Empty,
        snapshot.TaskId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        snapshot.RootPath.Trim(),
        snapshot.Path?.Trim() ?? string.Empty,
        snapshot.BaseRef?.Trim() ?? string.Empty,
        snapshot.HeadRef?.Trim() ?? string.Empty,
        snapshot.Staged ? "staged" : "worktree"
    ]);

    private static object NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static object JsonOrNull(JsonElement? value) => value is null ? DBNull.Value : JsonSerializer.Serialize(value.Value, JsonOptions);
    private static string ToDbTime(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromDbTime(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private bool IsStale(DateTime observedAt, TimeSpan staleAfter) => _utcNow().ToUniversalTime() - observedAt.ToUniversalTime() > staleAfter;
    private int FreshnessSeconds(DateTime observedAt) => Math.Max(0, (int)Math.Round((_utcNow().ToUniversalTime() - observedAt.ToUniversalTime()).TotalSeconds));
}
