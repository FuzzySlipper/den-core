using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class DesktopSnapshotRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private DateTime _now = new(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);
    private DesktopSnapshotRepository _snapshots = null!;
    private DesktopSessionEventRepository _sessionEvents = null!;
    private ProjectTask _task = null!;
    private AgentWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _snapshots = new DesktopSnapshotRepository(_testDb.Db, () => _now);
        _sessionEvents = new DesktopSessionEventRepository(_testDb.Db, () => _now);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project", RootPath = "/not/local" });

        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Desktop snapshot host"
        });

        var workspaces = new AgentWorkspaceRepository(_testDb.Db);
        _workspace = await workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = "ws-1",
            ProjectId = "proj",
            TaskId = _task.Id,
            Branch = "task/desktop",
            WorktreePath = "/home/patch/dev/proj-worktree",
            BaseBranch = "main"
        });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpsertGitSnapshot_CreatesAndUpdatesLatestSnapshotForSameSourceScope()
    {
        var observed = _now.AddSeconds(-15);
        var created = await _snapshots.UpsertGitSnapshotAsync(NewGitSnapshot(observed));

        Assert.Equal("proj", created.ProjectId);
        Assert.Equal(_task.Id, created.TaskId);
        Assert.Equal("ws-1", created.WorkspaceId);
        Assert.Equal("task/desktop", created.Branch);
        Assert.Equal("abcdef123456", created.HeadSha);
        Assert.Equal("origin/task/desktop", created.Upstream);
        Assert.Equal(2, created.Ahead);
        Assert.Equal(1, created.Behind);
        Assert.Equal(2, created.DirtyCounts.Total);
        Assert.Equal("src/Foo.cs", Assert.Single(created.ChangedFiles).Path);
        Assert.Equal("No upstream freshness issue", Assert.Single(created.Warnings));
        Assert.False(created.IsStale);
        Assert.Equal(15, created.FreshnessSeconds);

        var updatedInput = NewGitSnapshot(_now.AddSeconds(-5));
        updatedInput.Branch = "task/desktop-updated";
        updatedInput.HeadSha = "ffffeeee";
        updatedInput.DirtyCounts = new GitDirtyCounts { Total = 0 };
        updatedInput.ChangedFiles = [];
        var updated = await _snapshots.UpsertGitSnapshotAsync(updatedInput);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("task/desktop-updated", updated.Branch);
        Assert.Equal("ffffeeee", updated.HeadSha);
        Assert.Equal(0, updated.DirtyCounts.Total);
        Assert.Empty(updated.ChangedFiles);

        var listed = await _snapshots.ListGitSnapshotsAsync(new DesktopGitSnapshotListOptions
        {
            ProjectId = "proj",
            WorkspaceId = "ws-1",
            SourceInstanceId = "desktop-a",
            Limit = 10
        });
        var only = Assert.Single(listed);
        Assert.Equal(updated.Id, only.Id);
    }

    [Fact]
    public async Task GetLatestGitSnapshot_ReturnsMissingAndStaleStatesWithoutErrors()
    {
        var missing = await _snapshots.GetLatestGitSnapshotAsync(new DesktopGitSnapshotListOptions
        {
            ProjectId = "proj",
            WorkspaceId = "ws-missing",
            SourceInstanceId = "desktop-a",
            StaleAfter = TimeSpan.FromSeconds(30)
        });

        Assert.Equal(DesktopSnapshotState.Missing, missing.State);
        Assert.Equal("missing", missing.FreshnessStatus);
        Assert.Null(missing.Snapshot);

        await _snapshots.UpsertGitSnapshotAsync(NewGitSnapshot(_now.AddMinutes(-5)));
        var stale = await _snapshots.GetLatestGitSnapshotAsync(new DesktopGitSnapshotListOptions
        {
            ProjectId = "proj",
            WorkspaceId = "ws-1",
            SourceInstanceId = "desktop-a",
            StaleAfter = TimeSpan.FromSeconds(30)
        });

        Assert.Equal(DesktopSnapshotState.SourceOffline, stale.State);
        Assert.True(stale.IsStale);
        Assert.Equal("stale", stale.FreshnessStatus);
        Assert.NotNull(stale.Snapshot);
        Assert.True(stale.Snapshot!.IsStale);
    }

    [Fact]
    public async Task UpsertGitSnapshot_AllowsPathNotVisibleAsStatusData()
    {
        var saved = await _snapshots.UpsertGitSnapshotAsync(new DesktopGitSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            RootPath = "/remote/path/not/visible",
            State = DesktopSnapshotState.PathNotVisible,
            SourceInstanceId = "desktop-a",
            SourceDisplayName = "Desktop A",
            ObservedAt = _now,
            Warnings = ["Path is not visible on this desktop instance."]
        });

        Assert.Equal(DesktopSnapshotState.PathNotVisible, saved.State);
        Assert.Equal("Path is not visible on this desktop instance.", Assert.Single(saved.Warnings));
        Assert.Equal(0, saved.DirtyCounts.Total);
        Assert.Empty(saved.ChangedFiles);
    }

    [Fact]
    public async Task UpsertDiffSnapshot_StoresBoundedDiffForLaterLookup()
    {
        var saved = await _snapshots.UpsertDiffSnapshotAsync(new DesktopDiffSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            RootPath = _workspace.WorktreePath,
            Path = "src/Foo.cs",
            BaseRef = "main",
            HeadRef = "task/desktop",
            MaxBytes = 4096,
            Diff = "diff --git a/src/Foo.cs b/src/Foo.cs",
            SourceInstanceId = "desktop-a",
            SourceDisplayName = "Desktop A",
            ObservedAt = _now.AddSeconds(-4)
        });

        var loaded = await _snapshots.GetLatestDiffSnapshotAsync(new DesktopDiffSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            RootPath = _workspace.WorktreePath,
            Path = "src/Foo.cs",
            BaseRef = "main",
            HeadRef = "task/desktop",
            SourceInstanceId = "desktop-a",
            MaxBytes = 1,
            ObservedAt = _now
        }, TimeSpan.FromSeconds(30));

        Assert.NotNull(loaded);
        Assert.Equal(saved.Id, loaded!.Id);
        Assert.Equal("Desktop A", loaded.SourceDisplayName);
        Assert.False(loaded.IsStale);
        Assert.Contains("diff --git", loaded.Diff);
    }

    [Fact]
    public async Task UpsertSessionSnapshot_StoresControlCapabilitiesWithoutConflatingObservation()
    {
        var saved = await _snapshots.UpsertSessionSnapshotAsync(new DesktopSessionSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            SessionId = "pty-1",
            AgentIdentity = "pi",
            Role = "conductor",
            CurrentCommand = "pi",
            CurrentPhase = "working",
            ControlCapabilities = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""
                {"focus":true,"terminate":false,"launch_reviewer":true}
                """),
            SourceInstanceId = "desktop-a",
            ObservedAt = _now.AddSeconds(-2)
        });

        Assert.False(saved.IsStale);
        Assert.NotNull(saved.ControlCapabilities);
        Assert.True(saved.ControlCapabilities!.Value.GetProperty("focus").GetBoolean());
        Assert.False(saved.ControlCapabilities!.Value.GetProperty("terminate").GetBoolean());

        var listed = await _snapshots.ListSessionSnapshotsAsync(new DesktopSessionSnapshotListOptions
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            SourceInstanceId = "desktop-a",
            Limit = 10
        });
        Assert.Single(listed);
    }

    [Fact]
    public async Task UpsertSessionSnapshot_RoundTripsNewFirstClassFields()
    {
        var capabilitiesJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""
            {"can_attach":false,"can_terminate":true,"can_send_input":false,"can_stream_terminal":true}
            """);
        var saved = await _snapshots.UpsertSessionSnapshotAsync(new DesktopSessionSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            SessionId = "pty-2",
            Title = "My Terminal",
            DisplayName = "bash (task/foo)",
            Cwd = "/home/user/dev/project",
            Kind = "terminal",
            Backend = "direct_pty",
            Status = "running",
            StartedAt = _now.AddMinutes(-30),
            LastActivityAt = _now.AddSeconds(-10),
            ExitedAt = null,
            ExitCode = null,
            SourceDisplayName = "Desktop A",
            Capabilities = capabilitiesJson,
            SourceInstanceId = "desktop-a",
            ObservedAt = _now.AddSeconds(-2)
        });

        Assert.Equal("My Terminal", saved.Title);
        Assert.Equal("bash (task/foo)", saved.DisplayName);
        Assert.Equal("/home/user/dev/project", saved.Cwd);
        Assert.Equal("terminal", saved.Kind);
        Assert.Equal("direct_pty", saved.Backend);
        Assert.Equal("running", saved.Status);
        Assert.Equal(_now.AddMinutes(-30), saved.StartedAt);
        Assert.Equal(_now.AddSeconds(-10), saved.LastActivityAt);
        Assert.Null(saved.ExitedAt);
        Assert.Null(saved.ExitCode);
        Assert.Equal("Desktop A", saved.SourceDisplayName);
        Assert.NotNull(saved.Capabilities);
        Assert.True(saved.Capabilities!.Value.GetProperty("can_stream_terminal").GetBoolean());

        // Update with exited state
        var updated = await _snapshots.UpsertSessionSnapshotAsync(new DesktopSessionSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            SessionId = "pty-2",
            Status = "exited",
            ExitedAt = _now,
            ExitCode = 0,
            SourceDisplayName = "Desktop A",
            SourceInstanceId = "desktop-a",
            ObservedAt = _now
        });

        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal("exited", updated.Status);
        Assert.Equal(_now, updated.ExitedAt);
        Assert.Equal(0, updated.ExitCode);
        Assert.Equal("Desktop A", updated.SourceDisplayName);
    }

    private DesktopGitSnapshot NewGitSnapshot(DateTime observedAt) => new()
    {
        ProjectId = "proj",
        TaskId = _task.Id,
        WorkspaceId = _workspace.Id,
        RootPath = _workspace.WorktreePath,
        State = DesktopSnapshotState.Ok,
        Branch = "task/desktop",
        HeadSha = "abcdef123456",
        Upstream = "origin/task/desktop",
        Ahead = 2,
        Behind = 1,
        DirtyCounts = new GitDirtyCounts { Total = 2, Modified = 1, Untracked = 1 },
        ChangedFiles = [new GitFileStatus
        {
            Path = "src/Foo.cs",
            WorktreeStatus = "M",
            Category = "modified"
        }],
        Warnings = ["No upstream freshness issue"],
        SourceInstanceId = "desktop-a",
        SourceDisplayName = "Desktop A",
        ObservedAt = observedAt
    };

    [Fact]
    public async Task SessionEvent_AppendAndListRoundTripsFields()
    {
        var evt = await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            SourceInstanceId = "desktop-a",
            SessionId = "pty-1",
            EventType = "created",
            Payload = "{\"kind\":\"terminal\"}",
            RequestedBy = "user",
            Reason = "Session launched from project dashboard",
            ObservedAt = _now.AddSeconds(-10)
        });

        Assert.True(evt.Id > 0);
        Assert.Equal("proj", evt.ProjectId);
        Assert.Equal(_task.Id, evt.TaskId);
        Assert.Equal(_workspace.Id, evt.WorkspaceId);
        Assert.Equal("desktop-a", evt.SourceInstanceId);
        Assert.Equal("pty-1", evt.SessionId);
        Assert.Equal("created", evt.EventType);
        Assert.Contains("terminal", evt.Payload);
        Assert.Equal("user", evt.RequestedBy);
        Assert.Contains("dashboard", evt.Reason);
        Assert.Equal(_now.AddSeconds(-10), evt.ObservedAt);
        Assert.Equal(_now, evt.CreatedAt);
    }

    [Fact]
    public async Task SessionEvent_AppendMultipleAndListBySession()
    {
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-2",
            SourceInstanceId = "desktop-a",
            EventType = "created",
            ObservedAt = _now.AddSeconds(-20)
        });
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-2",
            SourceInstanceId = "desktop-a",
            EventType = "status_changed",
            Payload = "{\"from\":\"starting\",\"to\":\"running\"}",
            ObservedAt = _now.AddSeconds(-10)
        });
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-2",
            SourceInstanceId = "desktop-a",
            EventType = "crashed",
            Reason = "PTY process exited with signal 9",
            ObservedAt = _now
        });

        var all = await _sessionEvents.ListAsync(new DesktopSessionEventListOptions
        {
            ProjectId = "proj",
            SessionId = "pty-2",
            Limit = 10
        });
        Assert.Equal(3, all.Count);
        Assert.Equal("crashed", all[0].EventType);
        Assert.Equal("status_changed", all[1].EventType);
        Assert.Equal("created", all[2].EventType);
    }

    [Fact]
    public async Task SessionEvent_ListByEventTypeFilter()
    {
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-3",
            SourceInstanceId = "desktop-a",
            EventType = "created",
            ObservedAt = _now.AddSeconds(-5)
        });
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-3",
            SourceInstanceId = "desktop-a",
            EventType = "attached",
            RequestedBy = "pi",
            ObservedAt = _now
        });
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-3",
            SourceInstanceId = "desktop-a",
            EventType = "lease_acquired",
            Payload = "{\"lease_id\":\"l1\"}",
            ObservedAt = _now
        });

        var typeFiltered = await _sessionEvents.ListAsync(new DesktopSessionEventListOptions
        {
            ProjectId = "proj",
            SessionId = "pty-3",
            EventTypes = "created,attached",
            Limit = 10
        });
        Assert.Equal(2, typeFiltered.Count);
        Assert.Contains(typeFiltered, e => e.EventType == "created");
        Assert.Contains(typeFiltered, e => e.EventType == "attached");
    }

    [Fact]
    public async Task SessionEvent_ReconnectRequestedAndReconnectedRemainCompatibleWithLegacyReconnect()
    {
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-reconnect",
            SourceInstanceId = "desktop-a",
            EventType = SessionEventType.Reconnect.ToDbValue(),
            ObservedAt = _now.AddSeconds(-3)
        });
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-reconnect",
            SourceInstanceId = "desktop-a",
            EventType = SessionEventType.ReconnectRequested.ToDbValue(),
            ObservedAt = _now.AddSeconds(-2)
        });
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-reconnect",
            SourceInstanceId = "desktop-a",
            EventType = SessionEventType.Reconnected.ToDbValue(),
            ObservedAt = _now.AddSeconds(-1)
        });

        Assert.Equal(SessionEventType.ReconnectRequested, EnumExtensions.ParseSessionEventType("reconnect_requested"));
        Assert.Equal(SessionEventType.Reconnected, EnumExtensions.ParseSessionEventType("reconnected"));

        var events = await _sessionEvents.ListAsync(new DesktopSessionEventListOptions
        {
            ProjectId = "proj",
            SessionId = "pty-reconnect",
            EventTypes = "reconnect,reconnect_requested,reconnected",
            Limit = 10
        });
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.EventType == "reconnect");
        Assert.Contains(events, e => e.EventType == "reconnect_requested");
        Assert.Contains(events, e => e.EventType == "reconnected");
    }

    [Fact]
    public async Task SessionEvent_DatabaseDefaultCreatedAtIsUtcIsoWhenRepositoryDoesNotSupplyIt()
    {
        await using var conn = await _testDb.Db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO desktop_session_events (
                project_id, source_instance_id, session_id, event_type, observed_at
            ) VALUES (
                @projectId, @sourceInstanceId, @sessionId, @eventType, @observedAt
            )
            RETURNING created_at
            """;
        cmd.Parameters.AddWithValue("@projectId", "proj");
        cmd.Parameters.AddWithValue("@sourceInstanceId", "desktop-a");
        cmd.Parameters.AddWithValue("@sessionId", "pty-default-created");
        cmd.Parameters.AddWithValue("@eventType", "created");
        cmd.Parameters.AddWithValue("@observedAt", _now.ToString("O"));

        var createdAt = Assert.IsType<string>(await cmd.ExecuteScalarAsync());
        Assert.Contains('T', createdAt);
        Assert.EndsWith("Z", createdAt);
        Assert.Equal(DateTimeKind.Utc, DateTime.Parse(createdAt, null, System.Globalization.DateTimeStyles.RoundtripKind).Kind);
    }

    [Fact]
    public async Task SessionEvent_ListBySourceInstanceAndTask()
    {
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            SessionId = "pty-4",
            SourceInstanceId = "desktop-a",
            EventType = "created",
            ObservedAt = _now
        });
        await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-5",
            SourceInstanceId = "desktop-b",
            EventType = "created",
            ObservedAt = _now
        });

        var sourceFiltered = await _sessionEvents.ListAsync(new DesktopSessionEventListOptions
        {
            ProjectId = "proj",
            SourceInstanceId = "desktop-a",
            Limit = 10
        });
        Assert.Single(sourceFiltered);
        Assert.Equal("pty-4", sourceFiltered[0].SessionId);

        var taskFiltered = await _sessionEvents.ListAsync(new DesktopSessionEventListOptions
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            Limit = 10
        });
        Assert.Single(taskFiltered);
        Assert.Equal("pty-4", taskFiltered[0].SessionId);
    }

    [Fact]
    public async Task SessionEvent_DoesNotStoreRawTerminalBytesOrHeartbeats()
    {
        // Verify payload bounds are enforced
        var largePayload = new string('x', 10241);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sessionEvents.AppendAsync(new DesktopSessionEvent
            {
                ProjectId = "proj",
                SessionId = "pty-6",
                SourceInstanceId = "desktop-a",
                EventType = "warning",
                Payload = largePayload,
                ObservedAt = _now
            }));
        Assert.Contains("10240", ex.Message);

        // Verify reason length is bounded
        var longReason = new string('y', 2001);
        ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sessionEvents.AppendAsync(new DesktopSessionEvent
            {
                ProjectId = "proj",
                SessionId = "pty-6",
                SourceInstanceId = "desktop-a",
                EventType = "warning",
                Reason = longReason,
                ObservedAt = _now
            }));
        Assert.Contains("2000", ex.Message);

        // Valid bounded payload is accepted
        var valid = await _sessionEvents.AppendAsync(new DesktopSessionEvent
        {
            ProjectId = "proj",
            SessionId = "pty-6",
            SourceInstanceId = "desktop-a",
            EventType = "warning",
            Payload = "{\"code\":\"E001\",\"message\":\"Disk space low\"}",
            Reason = "Low disk space warning",
            ObservedAt = _now
        });
        Assert.Equal("warning", valid.EventType);
        Assert.NotNull(valid.Payload);
        Assert.NotNull(valid.Reason);
    }

    [Fact]
    public async Task SessionEvent_RejectsMissingRequiredFields()
    {
        // Missing project id
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sessionEvents.AppendAsync(new DesktopSessionEvent
            {
                ProjectId = "",
                SessionId = "pty-7",
                SourceInstanceId = "desktop-a",
                EventType = "created",
                ObservedAt = _now
            }));
        Assert.Contains("Project id", ex.Message);

        // Missing session id
        ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sessionEvents.AppendAsync(new DesktopSessionEvent
            {
                ProjectId = "proj",
                SessionId = "",
                SourceInstanceId = "desktop-a",
                EventType = "created",
                ObservedAt = _now
            }));
        Assert.Contains("Session id", ex.Message);

        // Missing source instance id
        ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sessionEvents.AppendAsync(new DesktopSessionEvent
            {
                ProjectId = "proj",
                SessionId = "pty-7",
                SourceInstanceId = "",
                EventType = "created",
                ObservedAt = _now
            }));
        Assert.Contains("Source instance id", ex.Message);

        // Missing event type
        ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sessionEvents.AppendAsync(new DesktopSessionEvent
            {
                ProjectId = "proj",
                SessionId = "pty-7",
                SourceInstanceId = "desktop-a",
                EventType = "",
                ObservedAt = _now
            }));
        Assert.Contains("Event type", ex.Message);

        // Missing observed at
        ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sessionEvents.AppendAsync(new DesktopSessionEvent
            {
                ProjectId = "proj",
                SessionId = "pty-7",
                SourceInstanceId = "desktop-a",
                EventType = "created",
                ObservedAt = default
            }));
        Assert.Contains("Observed at", ex.Message);
    }
}
