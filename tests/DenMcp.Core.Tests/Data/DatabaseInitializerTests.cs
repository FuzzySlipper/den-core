using DenMcp.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Data;

public class DatabaseInitializerTests : IDisposable
{
    private readonly string _dbPath;

    public DatabaseInitializerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-test-{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public async Task InitializeAsync_CreatesAllTables()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        var tables = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        Assert.Contains("projects", tables);
        Assert.Contains("tasks", tables);
        Assert.Contains("task_dependencies", tables);
        Assert.Contains("task_history", tables);
        Assert.Contains("messages", tables);
        Assert.Contains("message_reads", tables);
        Assert.Contains("review_rounds", tables);
        Assert.Contains("review_findings", tables);
        Assert.DoesNotContain("notification_message_links", tables);
        Assert.Contains("documents", tables);
        Assert.Contains("documents_fts", tables);
        Assert.Contains("agent_stream_entries", tables);
        Assert.Contains("agent_runs", tables);
        Assert.Contains("agent_workspaces", tables);
        Assert.Contains("agent_instance_bindings", tables);
        Assert.Contains("consolidation_topics", tables);
        Assert.Contains("topic_clip_queue_items", tables);
        Assert.Contains("curation_decisions", tables);
        Assert.Contains("channels", tables);
        Assert.Contains("channel_messages", tables);
        Assert.Contains("channel_memberships", tables);
        Assert.Contains("channel_reactions", tables);
    }

    [Fact]
    public async Task InitializeAsync_CreatesChannelMessageSourcePointerColumns()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        var columns = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(channel_messages)";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("source_kind", columns);
        Assert.Contains("source_id", columns);
        Assert.Contains("deep_link", columns);
        Assert.Contains("metadata_json", columns);
    }

    [Fact]
    public async Task ChannelMessages_CanStoreCanonicalSourcePointers()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var insertChannel = conn.CreateCommand();
        insertChannel.CommandText = """
            INSERT INTO channels (slug, display_name, kind, created_by)
            VALUES ('system-test', 'System Test', 'system', 'test')
            RETURNING id
            """;
        var channelId = (long)(await insertChannel.ExecuteScalarAsync())!;

        await using var insertMessage = conn.CreateCommand();
        insertMessage.CommandText = """
            INSERT INTO channel_messages (
                channel_id, sender_type, sender_identity, body, message_kind,
                source_kind, source_id, deep_link, metadata_json
            ) VALUES (
                @channelId, 'system', 'router', 'Task #42 updated', 'mirror_summary',
                'task_message', '42', 'den://project/den-mcp/message/42', '{"summary":true}'
            )
            RETURNING source_kind, source_id, deep_link, metadata_json
            """;
        insertMessage.Parameters.AddWithValue("@channelId", channelId);
        await using var reader = await insertMessage.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("task_message", reader.GetString(0));
        Assert.Equal("42", reader.GetString(1));
        Assert.Equal("den://project/den-mcp/message/42", reader.GetString(2));
        Assert.Equal("{\"summary\":true}", reader.GetString(3));
    }

    [Fact]
    public async Task ChannelSchema_RejectsInvalidKindsAndDuplicateProjectDefaults()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var invalidChannel = conn.CreateCommand();
        invalidChannel.CommandText = """
            INSERT INTO channels (slug, display_name, kind, created_by)
            VALUES ('bad-kind', 'Bad Kind', 'not_a_channel_kind', 'test')
            """;
        await Assert.ThrowsAsync<SqliteException>(() => invalidChannel.ExecuteNonQueryAsync());

        await using var insertProject = conn.CreateCommand();
        insertProject.CommandText = "INSERT INTO projects (id, name, kind) VALUES ('dup', 'Duplicate Default', 'project')";
        await insertProject.ExecuteNonQueryAsync();

        await using var duplicateDefault = conn.CreateCommand();
        duplicateDefault.CommandText = """
            INSERT INTO channels (slug, display_name, kind, project_id, created_by)
            VALUES ('project-dup-second', 'Duplicate Default 2', 'project_default', 'dup', 'test')
            """;
        await Assert.ThrowsAsync<SqliteException>(() => duplicateDefault.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ChannelMessageSchema_RejectsInvalidSourceKinds()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var insertChannel = conn.CreateCommand();
        insertChannel.CommandText = """
            INSERT INTO channels (slug, display_name, kind, created_by)
            VALUES ('constraint-test', 'Constraint Test', 'system', 'test')
            RETURNING id
            """;
        var channelId = (long)(await insertChannel.ExecuteScalarAsync())!;

        await using var invalidMessage = conn.CreateCommand();
        invalidMessage.CommandText = """
            INSERT INTO channel_messages (
                channel_id, sender_type, sender_identity, body, source_kind
            ) VALUES (
                @channelId, 'system', 'router', 'Bad source', 'not_a_source_kind'
            )
            """;
        invalidMessage.Parameters.AddWithValue("@channelId", channelId);
        await Assert.ThrowsAsync<SqliteException>(() => invalidMessage.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task InitializeAsync_EnsuresExistingProjectDefaultChannelsIdempotently()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var seed = conn.CreateCommand();
            seed.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root_path TEXT,
                    description TEXT,
                    kind TEXT NOT NULL DEFAULT 'project',
                    visibility TEXT NOT NULL DEFAULT 'normal'
                );
                INSERT INTO projects (id, name, kind) VALUES ('alpha', 'Alpha Project', 'project');
                INSERT INTO projects (id, name, kind) VALUES ('assistant-space', 'Assistant Space', 'assistant');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var channelCmd = verify.CreateCommand();
        channelCmd.CommandText = """
            SELECT slug, display_name, kind, created_by, COUNT(*) OVER ()
            FROM channels
            WHERE project_id = 'alpha' AND kind = 'project_default'
            """;
        await using var channelReader = await channelCmd.ExecuteReaderAsync();
        Assert.True(await channelReader.ReadAsync());
        Assert.Equal("project-alpha", channelReader.GetString(0));
        Assert.Equal("Alpha Project", channelReader.GetString(1));
        Assert.Equal("project_default", channelReader.GetString(2));
        Assert.Equal("system", channelReader.GetString(3));
        Assert.Equal(1, channelReader.GetInt32(4));
        Assert.False(await channelReader.ReadAsync());

        await using var spaceCmd = verify.CreateCommand();
        spaceCmd.CommandText = "SELECT COUNT(*) FROM channels WHERE project_id = 'assistant-space'";
        Assert.Equal(0L, await spaceCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProjectRepositoryCreateAsync_AutoCreatesDefaultProjectChannel()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        var repo = new ProjectRepository(new DbConnectionFactory(initializer.ConnectionString));
        await repo.CreateAsync(new DenMcp.Core.Models.Project
        {
            Id = "den-mcp",
            Name = "Den MCP",
            Kind = "project",
            Visibility = "normal"
        });

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT slug, display_name, kind
            FROM channels
            WHERE project_id = 'den-mcp' AND kind = 'project_default'
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("project-den-mcp", reader.GetString(0));
        Assert.Equal("Den MCP", reader.GetString(1));
        Assert.Equal("project_default", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task InitializeAsync_SeedsGlobalProject()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, kind, visibility FROM projects WHERE id = '_global'";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Global", reader.GetString(0));
        Assert.Equal("system", reader.GetString(1));
        Assert.Equal("hidden", reader.GetString(2));
    }

    [Fact]
    public async Task InitializeAsync_EnablesWalMode()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var result = await cmd.ExecuteScalarAsync();

        Assert.Equal("wal", result?.ToString());
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync(); // second call should not throw

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projects WHERE id = '_global'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task InitializeAsync_MigratesDesktopSessionEventsForSplitReconnectAndUtcDefault()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root_path TEXT,
                    description TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT INTO projects (id, name) VALUES ('proj', 'Project');
                CREATE TABLE desktop_session_events (
                    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    task_id               INTEGER,
                    workspace_id          TEXT,
                    source_instance_id    TEXT NOT NULL,
                    session_id            TEXT NOT NULL,
                    event_type            TEXT NOT NULL CHECK (event_type IN ('created', 'reconnect')),
                    payload               TEXT CHECK (length(payload) <= 10240),
                    requested_by          TEXT,
                    reason                TEXT CHECK (reason IS NULL OR length(reason) <= 2000),
                    observed_at           TEXT NOT NULL,
                    created_at            TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT INTO desktop_session_events (
                    project_id, source_instance_id, session_id, event_type, observed_at, created_at
                ) VALUES (
                    'proj', 'desktop-a', 'pty-old', 'reconnect', '2026-04-27T12:00:00.0000000Z', '2026-04-27 12:00:00'
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using (var checkCmd = verify.CreateCommand())
        {
            checkCmd.CommandText = """
                INSERT INTO desktop_session_events (
                    project_id, source_instance_id, session_id, event_type, observed_at
                ) VALUES (
                    'proj', 'desktop-a', 'pty-new', 'reconnect_requested', '2026-04-27T12:01:00.0000000Z'
                )
                RETURNING created_at
                """;
            var createdAt = Assert.IsType<string>(await checkCmd.ExecuteScalarAsync());
            Assert.Contains('T', createdAt);
            Assert.EndsWith("Z", createdAt);
        }

        await using (var countCmd = verify.CreateCommand())
        {
            countCmd.CommandText = """
                SELECT COUNT(*)
                FROM desktop_session_events
                WHERE session_id = 'pty-old' AND event_type = 'reconnect'
                """;
            Assert.Equal(1L, await countCmd.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task InitializeAsync_AddsReviewRoundDiffMetadataColumnsToExistingDatabase()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE review_rounds (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id INTEGER NOT NULL REFERENCES tasks(id),
                    round_number INTEGER NOT NULL,
                    requested_by TEXT NOT NULL,
                    branch TEXT NOT NULL,
                    base_branch TEXT NOT NULL,
                    base_commit TEXT NOT NULL,
                    head_commit TEXT NOT NULL,
                    last_reviewed_head_commit TEXT,
                    commits_since_last_review INTEGER,
                    tests_run TEXT,
                    notes TEXT,
                    verdict TEXT,
                    verdict_by TEXT,
                    verdict_notes TEXT,
                    requested_at TEXT NOT NULL DEFAULT (datetime('now')),
                    verdict_at TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var columns = new List<string>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(review_rounds)";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("preferred_diff_base_ref", columns);
        Assert.Contains("alternate_diff_base_ref", columns);
        Assert.Contains("delta_base_commit", columns);
        Assert.Contains("inherited_commit_count", columns);
        Assert.Contains("task_local_commit_count", columns);

        await using var seedProject = verify.CreateCommand();
        seedProject.CommandText = "INSERT INTO projects (id, name) VALUES ('proj', 'Test')";
        await seedProject.ExecuteNonQueryAsync();

        await using var seedTask = verify.CreateCommand();
        seedTask.CommandText = "INSERT INTO tasks (project_id, title) VALUES ('proj', 'Review target')";
        await seedTask.ExecuteNonQueryAsync();

        await using var invalidInsert = verify.CreateCommand();
        invalidInsert.CommandText = """
            INSERT INTO review_rounds (
                task_id, round_number, requested_by, branch, base_branch, base_commit, head_commit,
                inherited_commit_count
            )
            VALUES (1, 1, 'codex', 'task/596', 'main', 'abc123', 'def456', -1)
            """;

        await Assert.ThrowsAsync<SqliteException>(() => invalidInsert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task InitializeAsync_AddsCompletedByColumnToExistingDispatchEntriesTable()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );

                CREATE TABLE dispatch_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    target_agent TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    trigger_type TEXT NOT NULL,
                    trigger_id INTEGER NOT NULL,
                    task_id INTEGER,
                    summary TEXT,
                    context_prompt TEXT,
                    dedup_key TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    expires_at TEXT NOT NULL,
                    decided_at TEXT,
                    completed_at TEXT,
                    decided_by TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var columns = new List<string>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(dispatch_entries)";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("completed_by", columns);
    }

    [Fact]
    public async Task InitializeAsync_AddsAgentStreamTableToExistingDatabase()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'agent_stream_entries'";
        var result = await checkCmd.ExecuteScalarAsync();

        Assert.Equal("agent_stream_entries", result);
    }

    [Fact]
    public async Task InitializeAsync_AddsAgentInstanceBindingsTableToExistingDatabase()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'agent_instance_bindings'";
        var result = await checkCmd.ExecuteScalarAsync();

        Assert.Equal("agent_instance_bindings", result);
    }

    [Fact]
    public async Task InitializeAsync_AddsAgentStreamIndexesAndDeduplicatesExistingRows()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );

                INSERT INTO projects (id, name) VALUES ('proj', 'Project');

                CREATE TABLE agent_stream_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stream_kind TEXT NOT NULL CHECK (stream_kind IN ('ops', 'message')),
                    event_type TEXT NOT NULL,
                    project_id TEXT,
                    task_id INTEGER,
                    thread_id INTEGER,
                    dispatch_id INTEGER,
                    sender TEXT NOT NULL,
                    sender_instance_id TEXT,
                    recipient_agent TEXT,
                    recipient_role TEXT,
                    recipient_instance_id TEXT,
                    delivery_mode TEXT NOT NULL CHECK (delivery_mode IN ('record_only', 'notify', 'wake')),
                    body TEXT,
                    metadata TEXT,
                    dedup_key TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                INSERT INTO agent_stream_entries (
                    stream_kind, event_type, project_id, sender, recipient_agent, delivery_mode, dedup_key, body
                ) VALUES
                    ('ops', 'review_requested', 'proj', 'codex', 'claude-code', 'wake', 'dup-key', 'first'),
                    ('ops', 'review_requested', 'proj', 'codex', 'claude-code', 'wake', 'dup-key', 'second');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var countCmd = verify.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM agent_stream_entries WHERE dedup_key = 'dup-key'";
        var duplicateCount = (long)(await countCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, duplicateCount);

        var indexes = new List<string>();
        await using var indexCmd = verify.CreateCommand();
        indexCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'agent_stream_entries'";
        await using var reader = await indexCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));

        Assert.Contains("idx_agent_stream_sender_created", indexes);
        Assert.Contains("idx_agent_stream_sender_instance_created", indexes);
        Assert.Contains("idx_agent_stream_recipient_agent_created", indexes);
        Assert.Contains("idx_agent_stream_recipient_role_created", indexes);
        Assert.Contains("idx_agent_stream_recipient_instance_created", indexes);
        Assert.Contains("idx_agent_stream_dedup", indexes);
    }

    [Fact]
    public async Task InitializeAsync_AddsIntentColumnAndBackfillsLegacyMessageTypes()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    task_id INTEGER,
                    thread_id INTEGER,
                    sender TEXT NOT NULL,
                    content TEXT NOT NULL,
                    metadata TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                INSERT INTO messages (project_id, sender, content, metadata) VALUES
                    ('proj', 'codex', 'Request review', '{"type":"review_request_packet","recipient":"claude-code"}'),
                    ('proj', 'codex', 'Planning handoff', '{"type":"planning_summary","recipient":"claude-code"}'),
                    ('proj', 'codex', 'Unknown legacy type', '{"type":"something_else"}'),
                    ('proj', 'codex', 'Malformed json', '{not-json');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var rows = new List<(string Content, string Intent)>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT content, intent FROM messages ORDER BY id";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1)));

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("Request review", row.Content);
                Assert.Equal("review_request", row.Intent);
            },
            row =>
            {
                Assert.Equal("Planning handoff", row.Content);
                Assert.Equal("handoff", row.Intent);
            },
            row =>
            {
                Assert.Equal("Unknown legacy type", row.Content);
                Assert.Equal("general", row.Intent);
            },
            row =>
            {
                Assert.Equal("Malformed json", row.Content);
                Assert.Equal("general", row.Intent);
            });
    }

    [Fact]
    public async Task InitializeAsync_MessageIntentConstraintRejectsUnknownValues()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var insert = verify.CreateCommand();
        insert.CommandText = """
            INSERT INTO messages (project_id, sender, content, intent)
            VALUES ('_global', 'codex', 'Bad intent', 'not_real')
            """;

        await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task InitializeAsync_BackfillsHistoricalDispatchCleanup()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using (var seed = new SqliteConnection(initializer.ConnectionString))
        {
            await seed.OpenAsync();

            await using var cmd = seed.CreateCommand();
            cmd.CommandText = """
                INSERT INTO projects (id, name) VALUES ('proj', 'Test Project');

                INSERT INTO tasks (id, project_id, title, status) VALUES
                    (1, 'proj', 'Done task', 'done'),
                    (2, 'proj', 'Cancelled task', 'cancelled'),
                    (3, 'proj', 'Active review task', 'review'),
                    (4, 'proj', 'Other active task', 'review');

                INSERT INTO dispatch_entries (
                    id, project_id, target_agent, status, trigger_type, trigger_id, task_id, dedup_key, expires_at
                ) VALUES
                    (1, 'proj', 'claude-code', 'pending', 'message', 1001, 1, 'done-task-pending', datetime('now', '+1 day')),
                    (2, 'proj', 'claude-code', 'approved', 'message', 1002, 2, 'cancelled-task-approved', datetime('now', '+1 day')),
                    (3, 'proj', 'claude-code', 'pending', 'message', 1003, 3, 'older-open-review-request', datetime('now', '+1 day')),
                    (4, 'proj', 'claude-code', 'pending', 'message', 1004, 3, 'newer-open-review-request', datetime('now', '+1 day')),
                    (5, 'proj', 'codex', 'pending', 'message', 1005, 3, 'different-target-stays-open', datetime('now', '+1 day')),
                    (6, 'proj', 'claude-code', 'pending', 'message', 1006, 4, 'different-task-stays-open', datetime('now', '+1 day')),
                    (7, 'proj', 'claude-code', 'pending', 'message', 1007, NULL, 'project-level-dispatch-stays-open', datetime('now', '+1 day'));
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var statuses = new List<(int Id, string Status)>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT id, status FROM dispatch_entries ORDER BY id";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            statuses.Add((reader.GetInt32(0), reader.GetString(1)));

        Assert.Collection(
            statuses,
            row => Assert.Equal((1, "expired"), row),
            row => Assert.Equal((2, "expired"), row),
            row => Assert.Equal((3, "expired"), row),
            row => Assert.Equal((4, "pending"), row),
            row => Assert.Equal((5, "pending"), row),
            row => Assert.Equal((6, "pending"), row),
            row => Assert.Equal((7, "pending"), row));
    }

    [Fact]
    public async Task InitializeAsync_AddsSpaceMetadataColumnsToProjects()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        var columns = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(projects)";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("kind", columns);
        Assert.Contains("visibility", columns);
        Assert.Contains("owner", columns);
        Assert.Contains("settings_json", columns);
    }

    [Fact]
    public async Task InitializeAsync_ProjectsConstraintRejectsInvalidKind()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, name, kind)
            VALUES ('bad', 'Bad', 'not_a_kind')
            """;

        await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task InitializeAsync_ProjectsConstraintRejectsInvalidVisibility()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, name, visibility)
            VALUES ('bad', 'Bad', 'not_a_visibility')
            """;

        await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task InitializeAsync_MigratesSpaceMetadataColumnsAndBackfillsGlobal()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root_path TEXT,
                    description TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT INTO projects (id, name) VALUES ('_global', 'Global');
                INSERT INTO projects (id, name) VALUES ('existing-proj', 'Existing Project');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var rows = new List<(string Id, string Kind, string Visibility)>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT id, kind, visibility FROM projects ORDER BY id";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("_global", row.Id);
                Assert.Equal("system", row.Kind);
                Assert.Equal("hidden", row.Visibility);
            },
            row =>
            {
                Assert.Equal("existing-proj", row.Id);
                Assert.Equal("project", row.Kind);
                Assert.Equal("normal", row.Visibility);
            });
    }

    [Fact]
    public async Task InitializeAsync_MigratesDocumentsToAddSummaryAndMemoryDocType()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );
                INSERT INTO projects (id, name) VALUES ('proj', 'Project');
                CREATE TABLE documents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    slug TEXT NOT NULL,
                    title TEXT NOT NULL,
                    content TEXT NOT NULL,
                    doc_type TEXT NOT NULL DEFAULT 'spec'
                        CHECK (doc_type IN (
                            'prd',
                            'spec',
                            'adr',
                            'convention',
                            'reference',
                            'note'
                        )),
                    tags TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(project_id, slug)
                );
                INSERT INTO documents (project_id, slug, title, content, doc_type, tags)
                VALUES ('proj', 'old-doc', 'Old Doc', 'content', 'spec', '["tag1"]');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        // Verify summary column exists
        var columns = new List<string>();
        await using var colCmd = verify.CreateCommand();
        colCmd.CommandText = "PRAGMA table_info(documents)";
        await using var colReader = await colCmd.ExecuteReaderAsync();
        while (await colReader.ReadAsync())
            columns.Add(colReader.GetString(1));
        Assert.Contains("summary", columns);

        // Verify old data preserved
        await using var dataCmd = verify.CreateCommand();
        dataCmd.CommandText = "SELECT slug, doc_type, tags FROM documents WHERE slug = 'old-doc'";
        await using var dataReader = await dataCmd.ExecuteReaderAsync();
        Assert.True(await dataReader.ReadAsync());
        Assert.Equal("old-doc", dataReader.GetString(0));
        Assert.Equal("spec", dataReader.GetString(1));
        Assert.Equal("[\"tag1\"]", dataReader.GetString(2));

        // Verify memory doc_type is accepted
        await using var insertCmd = verify.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO documents (project_id, slug, title, content, doc_type, summary)
            VALUES ('proj', 'memory-doc', 'Memory Doc', 'memory content', 'memory', 'A memory summary')
            """;
        await insertCmd.ExecuteNonQueryAsync();

        await using var fetchCmd = verify.CreateCommand();
        fetchCmd.CommandText = "SELECT doc_type, summary FROM documents WHERE slug = 'memory-doc'";
        await using var fetchReader = await fetchCmd.ExecuteReaderAsync();
        Assert.True(await fetchReader.ReadAsync());
        Assert.Equal("memory", fetchReader.GetString(0));
        Assert.Equal("A memory summary", fetchReader.GetString(1));
    }

    [Fact]
    public async Task InitializeAsync_MigratesDocumentsWithoutLeavingAgentGuidanceForeignKeysOnTemporaryTable()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root_path TEXT,
                    description TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT INTO projects (id, name) VALUES ('proj', 'Project');
                CREATE TABLE documents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    slug TEXT NOT NULL,
                    title TEXT NOT NULL,
                    content TEXT NOT NULL,
                    doc_type TEXT NOT NULL DEFAULT 'spec'
                        CHECK (doc_type IN ('prd', 'spec', 'adr', 'convention', 'reference', 'note')),
                    tags TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(project_id, slug)
                );
                CREATE TABLE agent_guidance_entries (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id          TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    document_project_id TEXT NOT NULL,
                    document_slug       TEXT NOT NULL,
                    importance          TEXT NOT NULL DEFAULT 'important'
                                        CHECK (importance IN ('required', 'important')),
                    audience            TEXT,
                    sort_order          INTEGER NOT NULL DEFAULT 0,
                    notes               TEXT,
                    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(project_id, document_project_id, document_slug),
                    FOREIGN KEY (document_project_id, document_slug)
                        REFERENCES documents(project_id, slug) ON DELETE CASCADE
                );
                INSERT INTO documents (project_id, slug, title, content, doc_type)
                VALUES ('proj', 'guidance-doc', 'Guidance Doc', 'content', 'spec');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using (var schemaCmd = verify.CreateCommand())
        {
            schemaCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agent_guidance_entries'";
            var schema = Assert.IsType<string>(await schemaCmd.ExecuteScalarAsync());
            Assert.DoesNotContain("documents_old", schema, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("REFERENCES documents", schema, StringComparison.OrdinalIgnoreCase);
        }

        await using (var insertCmd = verify.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO agent_guidance_entries (
                    project_id, document_project_id, document_slug, importance
                ) VALUES (
                    'proj', 'proj', 'guidance-doc', 'required'
                )
                """;
            await insertCmd.ExecuteNonQueryAsync();
        }
    }
}
