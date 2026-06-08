using DenCore.Data;
using DenCore.Models;
using Microsoft.Data.Sqlite;

namespace DenCore.Tests.Data;

/// <summary>
/// Tests for nullable project_id on infrastructure tables (task #1931).
/// These tests must FAIL before schema changes are applied (RED phase).
/// </summary>
public class InfrastructureNullableProjectIdTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private IProjectRepository _projects = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _projects = new ProjectRepository(_testDb.Db);
        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private async Task<SqliteConnection> OpenConnectionAsync()
        => await _testDb.Db.CreateConnectionAsync();

    // ── RED phase tests: these should FAIL because NOT NULL is still in the schema ──

    [Fact]
    public async Task AgentSessions_CanInsertWithNullProjectId()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_sessions (agent, project_id, status)
            VALUES ('global-agent', NULL, 'active')
            """;
        // Should not throw FK/NOT NULL violation
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AgentSessions_AllowsGlobalSession_OnePerAgent()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Insert a global session (project_id = NULL)
        cmd.CommandText = """
            INSERT INTO agent_sessions (agent, project_id, status)
            VALUES ('global-agent', NULL, 'active')
            """;
        await cmd.ExecuteNonQueryAsync();

        // Verify we can update it with ON CONFLICT on agent only
        cmd.CommandText = """
            INSERT INTO agent_sessions (agent, project_id, session_id, status, checked_in_at, last_heartbeat, metadata)
            VALUES ('global-agent', NULL, 'sess-1', 'active', datetime('now'), datetime('now'), 'meta')
            ON CONFLICT(agent) DO UPDATE SET
                session_id = COALESCE('sess-1', agent_sessions.session_id),
                status = 'active',
                checked_in_at = datetime('now'),
                last_heartbeat = datetime('now'),
                metadata = COALESCE('meta', agent_sessions.metadata)
            """;
        await cmd.ExecuteNonQueryAsync();

        // Read back
        cmd.CommandText = "SELECT agent, project_id, session_id FROM agent_sessions WHERE agent = 'global-agent'";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("global-agent", reader.GetString(0));
        Assert.True(reader.IsDBNull(1)); // project_id IS NULL
        Assert.Equal("sess-1", reader.GetString(2));
    }

    [Fact]
    public async Task AgentInstanceBindings_CanInsertWithNullProjectId()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_instance_bindings (
                instance_id, project_id, agent_identity, agent_family, transport_kind, status
            ) VALUES (
                'inst-null-proj', NULL, 'global-worker', 'hermes', 'channels', 'active'
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DispatchEntries_CanInsertWithNullProjectId()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dispatch_entries (
                project_id, target_agent, trigger_type, trigger_id, dedup_key, expires_at
            ) VALUES (
                NULL, 'global-dispatcher', 'message', 1, 'dedup-global-1',
                datetime('now', '+24 hours')
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DesktopGitSnapshots_CanInsertWithNullProjectId()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO desktop_git_snapshots (
                project_id, root_path, scope_key, state, dirty_counts,
                changed_files, warnings, source_instance_id, observed_at
            ) VALUES (
                NULL, '/tmp/test', 'scope-1', 'ok', '{}',
                '[]', '[]', 'src-1', datetime('now')
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DesktopDiffSnapshots_CanInsertWithNullProjectId()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO desktop_diff_snapshots (
                project_id, root_path, diff_key, max_bytes, diff,
                warnings, source_instance_id, observed_at
            ) VALUES (
                NULL, '/tmp/test', 'diff-key-1', 1024, '--- a\n+++ b\n',
                '[]', 'src-1', datetime('now')
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DesktopSessionSnapshots_CanInsertWithNullProjectId()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO desktop_session_snapshots (
                project_id, session_id, warnings, source_instance_id, observed_at
            ) VALUES (
                NULL, 'sess-global-1', '[]', 'src-1', datetime('now')
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DesktopSessionEvents_CanInsertWithNullProjectId()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO desktop_session_events (
                project_id, source_instance_id, session_id, event_type, payload, observed_at
            ) VALUES (
                NULL, 'src-1', 'sess-1', 'created', '{}', datetime('now')
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task WorkerAssignments_CanInsertWithNullProjectId()
    {
        // Seed a pool member first
        await using var conn = await OpenConnectionAsync();
        await using var seedCmd = conn.CreateCommand();
        seedCmd.CommandText = """
            INSERT INTO worker_pool_members (worker_identity, profile_identity, status, last_heartbeat)
            VALUES ('wk-null-proj', 'spawned-coder', 'available', datetime('now'))
            """;
        await seedCmd.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO worker_assignments (
                worker_identity, run_id, project_id, role, assigned_by, state
            ) VALUES (
                'wk-null-proj', 'run-1', NULL, 'coder', 'runner', 'ack'
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task OrchestratorLeases_CanInsertWithNullProjectId()
    {
        // Seed a pool member first
        await using var conn = await OpenConnectionAsync();
        await using var seedCmd = conn.CreateCommand();
        seedCmd.CommandText = """
            INSERT INTO worker_pool_members (worker_identity, profile_identity, status, last_heartbeat)
            VALUES ('orch-null-proj', 'runner', 'available', datetime('now'))
            """;
        await seedCmd.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO orchestrator_leases (
                lease_id, project_id, lease_owner, orchestrator_identity
            ) VALUES (
                'lease-null-proj', NULL, 'admin', 'orch-null-proj'
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── ON DELETE SET NULL tests ──

    [Fact]
    public async Task AgentInstanceBindings_ProjectIdSetNullOnProjectDelete()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Insert a binding with a known project
        cmd.CommandText = """
            INSERT INTO agent_instance_bindings (
                instance_id, project_id, agent_identity, agent_family, transport_kind, status
            ) VALUES (
                'inst-del-test', 'test-proj', 'del-worker', 'hermes', 'channels', 'active'
            )
            """;
        await cmd.ExecuteNonQueryAsync();

        // Delete the project
        cmd.CommandText = "DELETE FROM projects WHERE id = 'test-proj'";
        await cmd.ExecuteNonQueryAsync();

        // Verify the binding's project_id is now NULL
        cmd.CommandText = "SELECT project_id FROM agent_instance_bindings WHERE instance_id = 'inst-del-test'";
        var projectId = await cmd.ExecuteScalarAsync();
        Assert.True(projectId is DBNull || projectId is null);
    }

    [Fact]
    public async Task DispatchEntries_ProjectIdSetNullOnProjectDelete()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Need to re-create project since the prior test may have deleted it
        try
        {
            cmd.CommandText = "INSERT OR IGNORE INTO projects (id, name) VALUES ('test-proj', 'Test Project')";
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* may already exist */ }

        cmd.CommandText = """
            INSERT INTO dispatch_entries (
                project_id, target_agent, trigger_type, trigger_id, dedup_key, expires_at
            ) VALUES (
                'test-proj', 'del-dispatcher', 'message', 1, 'dedup-del-test',
                datetime('now', '+24 hours')
            )
            """;
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "DELETE FROM projects WHERE id = 'test-proj'";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "SELECT project_id FROM dispatch_entries WHERE dedup_key = 'dedup-del-test'";
        var projectId = await cmd.ExecuteScalarAsync();
        Assert.True(projectId is DBNull || projectId is null);
    }
}
