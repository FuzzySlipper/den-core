using DenCore.Data;
using DenCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Tests.Data;

/// <summary>
/// Direct repository tests for nullable project_id flowing through production
/// repository paths (not just direct SQL). Addresses acceptance gaps from
/// validation packet #13214 for task #1931.
/// </summary>
public class RepositoryNullableProjectIdTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private IAgentInstanceBindingRepository _bindings = null!;
    private IWorkerPoolRepository _workers = null!;
    private IDesktopSessionEventRepository _events = null!;
    private IAgentSessionRepository _sessions = null!;
    private IProjectRepository _projects = null!;
    private ITaskRepository _tasks = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _bindings = new AgentInstanceBindingRepository(_testDb.Db);
        _workers = new WorkerPoolRepository(_testDb.Db);
        _events = new DesktopSessionEventRepository(_testDb.Db);
        _sessions = new AgentSessionRepository(_testDb.Db);
        _projects = new ProjectRepository(_testDb.Db);
        _tasks = new TaskRepository(_testDb.Db);
        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();



    // ── AgentSessionRepository with null ProjectId ───────────────────────

    [Fact]
    public async Task AgentSession_CheckInHeartbeatAndCheckout_WithNullProjectId_UsesGlobalSession()
    {
        var checkedIn = await _sessions.CheckInAsync(
            agent: "global-agent",
            projectId: null,
            sessionId: "global-session",
            metadata: "{}");

        Assert.Equal("global-agent", checkedIn.Agent);
        Assert.Null(checkedIn.ProjectId);
        Assert.Equal("global-session", checkedIn.SessionId);

        Assert.True(await _sessions.HeartbeatAsync("global-agent", null));
        Assert.True(await _sessions.CheckOutAsync("global-agent", null));

        var active = await _sessions.ListActiveAsync();
        Assert.DoesNotContain(active, session => session.Agent == "global-agent");
    }

    [Fact]
    public async Task AgentSession_ListActiveByProject_ExcludesGlobalSessions()
    {
        await _sessions.CheckInAsync("global-agent-filter", null, "global-filter-session");
        await _sessions.CheckInAsync("project-agent-filter", "test-proj", "project-filter-session");

        var projectSessions = await _sessions.ListActiveAsync("test-proj");

        var session = Assert.Single(projectSessions, item => item.Agent == "project-agent-filter");
        Assert.Equal("test-proj", session.ProjectId);
        Assert.DoesNotContain(projectSessions, item => item.Agent == "global-agent-filter");
    }

    [Fact]
    public async Task DatabaseInitializer_MigratesLegacyAgentSessionsTable_ToNullableGlobalSessionShape()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"den-core-legacy-agent-sessions-{Guid.NewGuid()}.db");
        try
        {
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA foreign_keys = ON;
                    CREATE TABLE projects (
                        id            TEXT PRIMARY KEY,
                        name          TEXT NOT NULL,
                        kind          TEXT NOT NULL DEFAULT 'project'
                                      CHECK (kind IN ('project', 'personal', 'assistant', 'knowledge_base', 'system')),
                        visibility    TEXT NOT NULL DEFAULT 'normal'
                                      CHECK (visibility IN ('normal', 'hidden', 'archived')),
                        owner         TEXT,
                        root_path     TEXT,
                        description   TEXT,
                        settings_json TEXT,
                        created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                        updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
                    );
                    INSERT INTO projects (id, name, kind, visibility) VALUES ('legacy-proj', 'Legacy Project', 'project', 'normal');
                    CREATE TABLE agent_sessions (
                        agent           TEXT NOT NULL,
                        project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                        session_id      TEXT,
                        status          TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
                        checked_in_at   TEXT NOT NULL DEFAULT (datetime('now')),
                        last_heartbeat  TEXT NOT NULL DEFAULT (datetime('now')),
                        metadata        TEXT,
                        PRIMARY KEY (agent, project_id)
                    );
                    INSERT INTO agent_sessions (agent, project_id, session_id, status, metadata)
                    VALUES ('legacy-agent', 'legacy-proj', 'legacy-session', 'active', '{"legacy":true}');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var initializer = new DatabaseInitializer(dbPath, NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            await using (var conn = new SqliteConnection(initializer.ConnectionString))
            {
                await conn.OpenAsync();
                var columns = await ReadTableInfoAsync(conn, "agent_sessions");
                Assert.Equal(0, columns["project_id"].NotNull);
                Assert.Equal(1, columns["agent"].PrimaryKeyOrdinal);
                Assert.Equal(0, columns["project_id"].PrimaryKeyOrdinal);
            }

            var repo = new AgentSessionRepository(new DbConnectionFactory(initializer.ConnectionString));
            var global = await repo.CheckInAsync("global-after-migration", null, "global-after-migration-session");
            Assert.Null(global.ProjectId);
            Assert.True(await repo.HeartbeatAsync("global-after-migration", null));

            var legacyProjectSessions = await repo.ListActiveAsync("legacy-proj");
            Assert.Contains(legacyProjectSessions, session => session.Agent == "legacy-agent");
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }


    [Fact]
    public async Task DatabaseInitializer_MigratesLegacyInfrastructureTables_ToNullableProjectIdShape()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"den-core-legacy-infra-nullable-project-{Guid.NewGuid()}.db");
        try
        {
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA foreign_keys = ON;
                    CREATE TABLE projects (
                        id            TEXT PRIMARY KEY,
                        name          TEXT NOT NULL,
                        kind          TEXT NOT NULL DEFAULT 'project'
                                      CHECK (kind IN ('project', 'personal', 'assistant', 'knowledge_base', 'system')),
                        visibility    TEXT NOT NULL DEFAULT 'normal'
                                      CHECK (visibility IN ('normal', 'hidden', 'archived')),
                        owner         TEXT,
                        root_path     TEXT,
                        description   TEXT,
                        settings_json TEXT,
                        created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                        updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
                    );
                    INSERT INTO projects (id, name, kind, visibility) VALUES ('legacy-proj', 'Legacy Project', 'project', 'normal');

                    CREATE TABLE agent_instance_bindings (
                        instance_id      TEXT PRIMARY KEY,
                        project_id       TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                        agent_identity   TEXT NOT NULL,
                        agent_family     TEXT NOT NULL,
                        role             TEXT,
                        transport_kind   TEXT NOT NULL,
                        status           TEXT NOT NULL DEFAULT 'active'
                                         CHECK (status IN ('active', 'inactive', 'degraded')),
                        session_id       TEXT,
                        checked_in_at    TEXT NOT NULL DEFAULT (datetime('now')),
                        last_heartbeat   TEXT,
                        metadata         TEXT,
                        created_at       TEXT NOT NULL DEFAULT (datetime('now')),
                        updated_at       TEXT NOT NULL DEFAULT (datetime('now'))
                    );
                    INSERT INTO agent_instance_bindings (
                        instance_id, project_id, agent_identity, agent_family, transport_kind, status
                    ) VALUES ('legacy-binding', 'legacy-proj', 'legacy-agent', 'hermes', 'channels', 'active');

                    CREATE TABLE dispatch_entries (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                        target_agent    TEXT NOT NULL,
                        status          TEXT NOT NULL DEFAULT 'pending'
                                        CHECK (status IN ('pending', 'approved', 'rejected', 'completed', 'expired')),
                        trigger_type    TEXT NOT NULL
                                        CHECK (trigger_type IN ('message', 'task_status')),
                        trigger_id      INTEGER NOT NULL,
                        task_id         INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                        summary         TEXT,
                        context_prompt  TEXT,
                        context_json    TEXT,
                        dedup_key       TEXT NOT NULL,
                        created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                        expires_at      TEXT NOT NULL,
                        decided_at      TEXT,
                        completed_at    TEXT,
                        decided_by      TEXT,
                        completed_by    TEXT
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var initializer = new DatabaseInitializer(dbPath, NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            await using var verifyConn = new SqliteConnection(initializer.ConnectionString);
            await verifyConn.OpenAsync();
            var bindingColumns = await ReadTableInfoAsync(verifyConn, "agent_instance_bindings");
            var dispatchColumns = await ReadTableInfoAsync(verifyConn, "dispatch_entries");
            Assert.Equal(0, bindingColumns["project_id"].NotNull);
            Assert.Equal(0, dispatchColumns["project_id"].NotNull);

            var bindingSchema = await ReadTableSchemaAsync(verifyConn, "agent_instance_bindings");
            var dispatchSchema = await ReadTableSchemaAsync(verifyConn, "dispatch_entries");
            Assert.Contains("project_id TEXT REFERENCES projects(id) ON DELETE SET NULL", bindingSchema);
            Assert.Contains("project_id TEXT REFERENCES projects(id) ON DELETE SET NULL", dispatchSchema);

            var repo = new AgentInstanceBindingRepository(new DbConnectionFactory(initializer.ConnectionString));
            var globalBinding = await repo.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "global-binding-after-migration",
                ProjectId = null,
                AgentIdentity = "global-agent-after-migration",
                AgentFamily = "hermes",
                TransportKind = "channels",
                Status = AgentInstanceBindingStatus.Active
            });
            Assert.Null(globalBinding.ProjectId);
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static async Task<Dictionary<string, TableColumnInfo>> ReadTableInfoAsync(SqliteConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        var result = new Dictionary<string, TableColumnInfo>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(1)] = new TableColumnInfo(
                NotNull: reader.GetInt32(3),
                PrimaryKeyOrdinal: reader.GetInt32(5));
        }
        return result;
    }

    private static async Task<string> ReadTableSchemaAsync(SqliteConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.AddParameterWithValue("@name", tableName);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed record TableColumnInfo(int NotNull, int PrimaryKeyOrdinal);


    // ── AgentInstanceBindingRepository ───────────────────────────────────

    [Fact]
    public async Task AgentInstanceBinding_UpsertWithNullProjectId_ReadsBackNull()
    {
        var binding = new AgentInstanceBinding
        {
            InstanceId = "inst-null-proj",
            ProjectId = null,                // NULL project — cross-project binding
            AgentIdentity = "global-worker",
            AgentFamily = "hermes",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        };

        var result = await _bindings.UpsertAsync(binding);
        Assert.Equal("inst-null-proj", result.InstanceId);
        Assert.Null(result.ProjectId);
        Assert.Equal("global-worker", result.AgentIdentity);
    }

    [Fact]
    public async Task AgentInstanceBinding_UpsertWithNonNullProjectId_ReadsBackProjectId()
    {
        var binding = new AgentInstanceBinding
        {
            InstanceId = "inst-proj",
            ProjectId = "test-proj",
            AgentIdentity = "proj-worker",
            AgentFamily = "hermes",
            TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        };

        var result = await _bindings.UpsertAsync(binding);
        Assert.Equal("test-proj", result.ProjectId);
    }

    [Fact]
    public async Task AgentInstanceBinding_ListByProject_ReturnsOnlyMatchingProject()
    {
        // Insert one with test-proj and one with NULL project_id
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "inst-a", ProjectId = "test-proj",
            AgentIdentity = "a", AgentFamily = "h", TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        });
        await _bindings.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "inst-b", ProjectId = null,
            AgentIdentity = "b", AgentFamily = "h", TransportKind = "channels",
            Status = AgentInstanceBindingStatus.Active,
        });

        // List by test-proj — should only get inst-a (project filter preservation)
        var projList = await _bindings.ListAsync(new AgentInstanceBindingListOptions { ProjectId = "test-proj" });
        Assert.Single(projList);
        Assert.Equal("inst-a", projList[0].InstanceId);
    }

    // ── DesktopSessionEventRepository ────────────────────────────────────

    [Fact]
    public async Task DesktopSessionEvent_AppendWithNullProjectId_ReadsBackNull()
    {
        var evt = new DesktopSessionEvent
        {
            ProjectId = null,                 // NULL project
            SourceInstanceId = "src-1",
            SessionId = "sess-1",
            EventType = "created",
            Payload = "{}",
            ObservedAt = DateTime.UtcNow,
        };

        var result = await _events.AppendAsync(evt);
        Assert.Null(result.ProjectId);
        Assert.Equal("sess-1", result.SessionId);
    }

    [Fact]
    public async Task DesktopSessionEvent_AppendWithNonNullProjectId_ReadsBackProjectId()
    {
        var evt = new DesktopSessionEvent
        {
            ProjectId = "test-proj",
            SourceInstanceId = "src-2",
            SessionId = "sess-2",
            EventType = "created",
            Payload = "{}",
            ObservedAt = DateTime.UtcNow,
        };

        var result = await _events.AppendAsync(evt);
        Assert.Equal("test-proj", result.ProjectId);
    }

    // ── LeaseAvailableWorkerAsync with null ProjectId ────────────────────

    [Fact]
    public async Task LeaseWorker_WithNullProjectId_CreatesAssignment()
    {
        // Seed a pool member
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "wk-null",
            ProfileIdentity = "spawned-coder",
            DisplayName = "Null Proj Worker",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new LeaseWorkerInput
        {
            ProjectId = null,                 // NULL project
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-null-proj",
            PreferredWorkerIdentity = "wk-null",
        };

        var assignment = await _workers.LeaseAvailableWorkerAsync(input);
        Assert.NotNull(assignment);
        Assert.Equal("run-null-proj", assignment!.RunId);
        Assert.Null(assignment.ProjectId);
    }

    // ── CreateOrchestratorLeaseAsync with null ProjectId ─────────────────

    [Fact]
    public async Task CreateOrchLease_WithNullProjectId_CreatesLease()
    {
        // Seed a pool member with orchestrator role
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "orch-null",
            ProfileIdentity = "runner",
            WorkerRole = "project_orchestrator",
            DisplayName = "Null Proj Orch",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new CreateOrchestratorLeaseInput
        {
            ProjectId = null,                 // NULL project
            LeaseOwner = "admin",
            PreferredOrchestratorIdentity = "orch-null",
        };

        var lease = await _workers.CreateOrchestratorLeaseAsync(input);
        Assert.NotNull(lease);
        Assert.Equal("orch-null", lease.OrchestratorIdentity);
        Assert.Null(lease.ProjectId);
    }

    // ── LeaseWorkerAsync with non-null ProjectId (project-filter works) ──

    [Fact]
    public async Task LeaseWorker_WithNonNullProjectId_PreservesProjectOnAssignment()
    {
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "wk-proj",
            ProfileIdentity = "spawned-coder",
            DisplayName = "Proj Worker",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new LeaseWorkerInput
        {
            ProjectId = "test-proj",
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-proj-1",
            PreferredWorkerIdentity = "wk-proj",
        };

        var assignment = await _workers.LeaseAvailableWorkerAsync(input);
        Assert.NotNull(assignment);
        Assert.Equal("test-proj", assignment!.ProjectId);
    }

    // ── CreateOrchestratorLeaseAsync with non-null ProjectId ─────────────

    [Fact]
    public async Task CreateOrchLease_WithNonNullProjectId_PreservesProjectOnLease()
    {
        await _workers.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "orch-proj",
            ProfileIdentity = "runner",
            WorkerRole = "project_orchestrator",
            DisplayName = "Proj Orch",
            Status = WorkerPoolStates.MemberAvailable,
            LastHeartbeat = DateTime.UtcNow.ToString("o"),
        });

        var input = new CreateOrchestratorLeaseInput
        {
            ProjectId = "test-proj",
            LeaseOwner = "admin",
            PreferredOrchestratorIdentity = "orch-proj",
        };

        var lease = await _workers.CreateOrchestratorLeaseAsync(input);
        Assert.NotNull(lease);
        Assert.Equal("test-proj", lease.ProjectId);
    }
}
