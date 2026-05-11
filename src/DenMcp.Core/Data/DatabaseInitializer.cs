using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Data;

public sealed class DatabaseInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(string databasePath, ILogger<DatabaseInitializer> logger)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={databasePath}";
        _logger = logger;
    }

    public string ConnectionString => _connectionString;

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
        await pragmaCmd.ExecuteNonQueryAsync();

        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = Schema;
        await schemaCmd.ExecuteNonQueryAsync();

        // Migrations for existing databases
        await RunMigrationsAsync(connection);

        await EnsureGlobalProjectAsync(connection);
        await EnsureProjectChannelsAsync(connection);

        _logger.LogInformation("Database initialized at {ConnectionString}", _connectionString);
    }

    internal const string Schema = """
        ------------------------------------------------------------
        -- PROJECTS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS projects (
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



        ------------------------------------------------------------
        -- TASKS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS tasks (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            parent_id   INTEGER REFERENCES tasks(id) ON DELETE CASCADE,
            title       TEXT NOT NULL,
            description TEXT,
            status      TEXT NOT NULL DEFAULT 'planned'
                        CHECK (status IN (
                            'planned',
                            'in_progress',
                            'review',
                            'blocked',
                            'done',
                            'cancelled'
                        )),
            priority    INTEGER NOT NULL DEFAULT 3
                        CHECK (priority BETWEEN 1 AND 5),
            assigned_to TEXT,
            tags        TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_tasks_project_status ON tasks(project_id, status);
        CREATE INDEX IF NOT EXISTS idx_tasks_assigned ON tasks(assigned_to);
        CREATE INDEX IF NOT EXISTS idx_tasks_parent ON tasks(parent_id);

        ------------------------------------------------------------
        -- TASK DEPENDENCIES
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS task_dependencies (
            task_id     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            depends_on  INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            PRIMARY KEY (task_id, depends_on),
            CHECK (task_id != depends_on)
        );

        ------------------------------------------------------------
        -- TASK HISTORY
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS task_history (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            field       TEXT NOT NULL,
            old_value   TEXT,
            new_value   TEXT,
            changed_by  TEXT,
            changed_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_task_history_task ON task_history(task_id);

        ------------------------------------------------------------
        -- MESSAGES
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS messages (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id     INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            thread_id   INTEGER REFERENCES messages(id) ON DELETE SET NULL,
            sender      TEXT NOT NULL,
            content     TEXT NOT NULL,
            intent      TEXT NOT NULL DEFAULT 'general'
                        CHECK (intent IN (
                            'general',
                            'note',
                            'status_update',
                            'question',
                            'answer',
                            'handoff',
                            'review_request',
                            'review_feedback',
                            'review_approval',
                            'task_ready',
                            'task_blocked',
                            'notification'
                        )),
            metadata    TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_messages_project_task ON messages(project_id, task_id);
        CREATE INDEX IF NOT EXISTS idx_messages_thread ON messages(thread_id);

        ------------------------------------------------------------
        -- MESSAGE READ STATE
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS message_reads (
            message_id  INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            agent       TEXT NOT NULL,
            read_at     TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (message_id, agent)
        );

        ------------------------------------------------------------
        -- DOCUMENTS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS documents (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            slug        TEXT NOT NULL,
            title       TEXT NOT NULL,
            content     TEXT NOT NULL,
            doc_type    TEXT NOT NULL DEFAULT 'spec'
                        CHECK (doc_type IN (
                            'prd',
                            'spec',
                            'adr',
                            'convention',
                            'reference',
                            'note',
                            'memory'
                        )),
            tags        TEXT,
            summary     TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(project_id, slug)
        );

        CREATE INDEX IF NOT EXISTS idx_documents_project_type ON documents(project_id, doc_type);

        ------------------------------------------------------------
        -- FTS5 for full-text search across documents
        ------------------------------------------------------------
        CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
            title,
            content,
            tags,
            content=documents,
            content_rowid=id,
            tokenize='porter unicode61'
        );

        -- Triggers to keep FTS in sync
        CREATE TRIGGER IF NOT EXISTS documents_ai AFTER INSERT ON documents BEGIN
            INSERT INTO documents_fts(rowid, title, content, tags)
            VALUES (new.id, new.title, new.content, new.tags);
        END;

        CREATE TRIGGER IF NOT EXISTS documents_ad AFTER DELETE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
            VALUES ('delete', old.id, old.title, old.content, old.tags);
        END;

        CREATE TRIGGER IF NOT EXISTS documents_au AFTER UPDATE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
            VALUES ('delete', old.id, old.title, old.content, old.tags);
            INSERT INTO documents_fts(rowid, title, content, tags)
            VALUES (new.id, new.title, new.content, new.tags);
        END;

        ------------------------------------------------------------
        -- SHARED BLACKBOARD MEMORY
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS blackboard_entries (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            slug                 TEXT NOT NULL UNIQUE,
            title                TEXT NOT NULL,
            content              TEXT NOT NULL,
            tags                 TEXT,
            idle_ttl_seconds     INTEGER CHECK (idle_ttl_seconds IS NULL OR idle_ttl_seconds > 0),
            created_at           TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at           TEXT NOT NULL DEFAULT (datetime('now')),
            last_accessed_at     TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_blackboard_updated
            ON blackboard_entries(updated_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_blackboard_last_accessed
            ON blackboard_entries(last_accessed_at ASC)
            WHERE idle_ttl_seconds IS NOT NULL;

        ------------------------------------------------------------
        -- AGENT SESSIONS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS agent_sessions (
            agent           TEXT NOT NULL,
            project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            session_id      TEXT,
            status          TEXT NOT NULL DEFAULT 'active'
                            CHECK (status IN ('active', 'inactive')),
            checked_in_at   TEXT NOT NULL DEFAULT (datetime('now')),
            last_heartbeat  TEXT NOT NULL DEFAULT (datetime('now')),
            metadata        TEXT,
            PRIMARY KEY (agent, project_id)
        );

        CREATE INDEX IF NOT EXISTS idx_agent_sessions_project_status
            ON agent_sessions(project_id, status);

        ------------------------------------------------------------
        -- AGENT INSTANCE BINDINGS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS agent_instance_bindings (
            instance_id      TEXT PRIMARY KEY,
            project_id       TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            agent_identity   TEXT NOT NULL,
            agent_family     TEXT NOT NULL,
            role             TEXT,
            transport_kind   TEXT NOT NULL,
            session_id       TEXT,
            status           TEXT NOT NULL DEFAULT 'active'
                             CHECK (status IN ('active', 'inactive', 'degraded')),
            metadata         TEXT,
            checked_in_at    TEXT NOT NULL DEFAULT (datetime('now')),
            last_heartbeat   TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_agent_bindings_project_status
            ON agent_instance_bindings(project_id, status, last_heartbeat DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_bindings_project_role_status
            ON agent_instance_bindings(project_id, role, status, last_heartbeat DESC)
            WHERE role IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_agent_bindings_project_agent_status
            ON agent_instance_bindings(project_id, agent_identity, status, last_heartbeat DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_bindings_session
            ON agent_instance_bindings(session_id)
            WHERE session_id IS NOT NULL;

        ------------------------------------------------------------
        -- REVIEW ROUNDS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS review_rounds (
            id                          INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id                     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            round_number                INTEGER NOT NULL,
            requested_by                TEXT NOT NULL,
            branch                      TEXT NOT NULL,
            base_branch                 TEXT NOT NULL,
            base_commit                 TEXT NOT NULL,
            head_commit                 TEXT NOT NULL,
            last_reviewed_head_commit   TEXT,
            commits_since_last_review   INTEGER,
            tests_run                   TEXT,
            notes                       TEXT,
            preferred_diff_base_ref     TEXT,
            preferred_diff_base_commit  TEXT,
            preferred_diff_head_ref     TEXT,
            preferred_diff_head_commit  TEXT,
            alternate_diff_base_ref     TEXT,
            alternate_diff_base_commit  TEXT,
            alternate_diff_head_ref     TEXT,
            alternate_diff_head_commit  TEXT,
            delta_base_commit           TEXT,
            inherited_commit_count      INTEGER
                                        CHECK (inherited_commit_count IS NULL OR inherited_commit_count >= 0),
            task_local_commit_count     INTEGER
                                        CHECK (task_local_commit_count IS NULL OR task_local_commit_count >= 0),
            verdict                     TEXT
                                        CHECK (verdict IS NULL OR verdict IN (
                                            'changes_requested',
                                            'looks_good',
                                            'follow_up_needed',
                                            'blocked_by_dependency'
                                        )),
            verdict_by                  TEXT,
            verdict_notes               TEXT,
            requested_at                TEXT NOT NULL DEFAULT (datetime('now')),
            verdict_at                  TEXT,
            UNIQUE(task_id, round_number)
        );

        CREATE INDEX IF NOT EXISTS idx_review_rounds_task
            ON review_rounds(task_id, round_number);

        ------------------------------------------------------------
        -- REVIEW FINDINGS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS review_findings (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            finding_key         TEXT NOT NULL UNIQUE,
            task_id             INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            review_round_id     INTEGER NOT NULL REFERENCES review_rounds(id) ON DELETE CASCADE,
            finding_number      INTEGER NOT NULL,
            created_by          TEXT NOT NULL,
            category            TEXT NOT NULL
                                CHECK (category IN (
                                    'blocking_bug',
                                    'acceptance_gap',
                                    'test_weakness',
                                    'follow_up_candidate'
                                )),
            summary             TEXT NOT NULL,
            notes               TEXT,
            file_references     TEXT,
            test_commands       TEXT,
            status              TEXT NOT NULL DEFAULT 'open'
                                CHECK (status IN (
                                    'open',
                                    'claimed_fixed',
                                    'verified_fixed',
                                    'not_fixed',
                                    'superseded',
                                    'split_to_follow_up'
                                )),
            status_updated_by   TEXT,
            status_notes        TEXT,
            status_updated_at   TEXT,
            response_by         TEXT,
            response_notes      TEXT,
            response_at         TEXT,
            follow_up_task_id   INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            created_at          TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(task_id, finding_number)
        );

        CREATE INDEX IF NOT EXISTS idx_review_findings_task_status
            ON review_findings(task_id, status, finding_number);
        CREATE INDEX IF NOT EXISTS idx_review_findings_round
            ON review_findings(review_round_id, finding_number);

        ------------------------------------------------------------
        -- DISPATCH ENTRIES
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS dispatch_entries (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            target_agent    TEXT NOT NULL,
            status          TEXT NOT NULL DEFAULT 'pending'
                            CHECK (status IN (
                                'pending',
                                'approved',
                                'rejected',
                                'completed',
                                'expired'
                            )),
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

        CREATE INDEX IF NOT EXISTS idx_dispatch_status
            ON dispatch_entries(status);
        CREATE INDEX IF NOT EXISTS idx_dispatch_project_status
            ON dispatch_entries(project_id, status);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_dispatch_dedup
            ON dispatch_entries(dedup_key) WHERE status = 'pending';

        ------------------------------------------------------------
        -- AGENT STREAM
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS agent_stream_entries (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            stream_kind             TEXT NOT NULL
                                    CHECK (stream_kind IN ('ops', 'message')),
            event_type              TEXT NOT NULL,
            project_id              TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id                 INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            thread_id               INTEGER REFERENCES messages(id) ON DELETE SET NULL,
            dispatch_id             INTEGER REFERENCES dispatch_entries(id) ON DELETE SET NULL,
            sender                  TEXT NOT NULL,
            sender_instance_id      TEXT,
            recipient_agent         TEXT,
            recipient_role          TEXT,
            recipient_instance_id   TEXT,
            delivery_mode           TEXT NOT NULL
                                    CHECK (delivery_mode IN ('record_only', 'notify', 'wake')),
            body                    TEXT,
            metadata                TEXT,
            dedup_key               TEXT,
            created_at              TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_agent_stream_created
            ON agent_stream_entries(created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_stream_project_created
            ON agent_stream_entries(project_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_stream_kind_event_created
            ON agent_stream_entries(stream_kind, event_type, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_stream_task_created
            ON agent_stream_entries(task_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_stream_dispatch
            ON agent_stream_entries(dispatch_id);
        CREATE INDEX IF NOT EXISTS idx_agent_stream_sender_created
            ON agent_stream_entries(sender, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_stream_sender_instance_created
            ON agent_stream_entries(sender_instance_id, created_at DESC, id DESC)
            WHERE sender_instance_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_agent_stream_recipient_agent_created
            ON agent_stream_entries(recipient_agent, created_at DESC, id DESC)
            WHERE recipient_agent IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_agent_stream_recipient_role_created
            ON agent_stream_entries(recipient_role, created_at DESC, id DESC)
            WHERE recipient_role IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_agent_stream_recipient_instance_created
            ON agent_stream_entries(recipient_instance_id, created_at DESC, id DESC)
            WHERE recipient_instance_id IS NOT NULL;

        ------------------------------------------------------------
        -- CHANNELS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS channels (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            slug          TEXT NOT NULL UNIQUE,
            display_name  TEXT NOT NULL,
            kind          TEXT NOT NULL
                          CHECK (kind IN ('project_default', 'project_activity', 'task_room', 'ad_hoc', 'system', 'dm', 'small_group')),
            project_id    TEXT REFERENCES projects(id) ON DELETE CASCADE,
            space_id      TEXT REFERENCES projects(id) ON DELETE SET NULL,
            created_by    TEXT NOT NULL DEFAULT 'system',
            visibility    TEXT NOT NULL DEFAULT 'normal'
                          CHECK (visibility IN ('normal', 'private', 'archived')),
            settings_json TEXT,
            created_at    TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at    TEXT NOT NULL DEFAULT (datetime('now')),
            archived_at   TEXT
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_channels_project_default_unique
            ON channels(project_id)
            WHERE project_id IS NOT NULL AND kind = 'project_default';
        CREATE INDEX IF NOT EXISTS idx_channels_project_kind
            ON channels(project_id, kind);

        CREATE TABLE IF NOT EXISTS channel_messages (
            id                     INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id             INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            sender_type            TEXT NOT NULL
                                   CHECK (sender_type IN ('user', 'agent', 'system', 'bridge')),
            sender_identity        TEXT NOT NULL,
            body                   TEXT NOT NULL,
            message_kind           TEXT NOT NULL DEFAULT 'human_text'
                                   CHECK (message_kind IN ('human_text', 'agent_text', 'system_event', 'mirror_summary', 'command', 'command_result')),
            source_kind            TEXT
                                   CHECK (source_kind IS NULL OR source_kind IN ('task_message', 'agent_stream_entry', 'notification', 'worker_run', 'review_round', 'review_finding', 'wake_event', 'external_adapter_message')),
            source_id              TEXT,
            summary                TEXT,
            deep_link              TEXT,
            thread_root_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            reply_to_message_id    INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            metadata_json          TEXT,
            created_at             TEXT NOT NULL DEFAULT (datetime('now')),
            edited_at              TEXT,
            deleted_at             TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_channel_messages_channel_created
            ON channel_messages(channel_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_channel_messages_source
            ON channel_messages(source_kind, source_id)
            WHERE source_kind IS NOT NULL AND source_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS channel_memberships (
            id                            INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id                    INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            member_type                   TEXT NOT NULL
                                          CHECK (member_type IN ('user', 'agent', 'role', 'group')),
            member_identity               TEXT NOT NULL,
            membership_status             TEXT NOT NULL DEFAULT 'active'
                                          CHECK (membership_status IN ('active', 'muted', 'left', 'banned')),
            wake_policy                   TEXT NOT NULL DEFAULT 'mentions_only'
                                          CHECK (wake_policy IN ('never', 'mentions_only', 'direct_questions_only', 'substantive_digest', 'all_human_messages', 'all_messages_except_self')),
            can_send                      INTEGER NOT NULL DEFAULT 1 CHECK (can_send IN (0, 1)),
            can_react                     INTEGER NOT NULL DEFAULT 1 CHECK (can_react IN (0, 1)),
            can_invite                    INTEGER NOT NULL DEFAULT 0 CHECK (can_invite IN (0, 1)),
            cooldown_seconds              INTEGER NOT NULL DEFAULT 0 CHECK (cooldown_seconds >= 0),
            max_auto_replies_per_window   INTEGER NOT NULL DEFAULT 0 CHECK (max_auto_replies_per_window >= 0),
            settings_json                 TEXT,
            created_at                    TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                    TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, member_type, member_identity)
        );

        CREATE INDEX IF NOT EXISTS idx_channel_memberships_member
            ON channel_memberships(member_type, member_identity, membership_status);

        CREATE TABLE IF NOT EXISTS channel_reactions (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_message_id    INTEGER NOT NULL REFERENCES channel_messages(id) ON DELETE CASCADE,
            reactor_type          TEXT NOT NULL CHECK (reactor_type IN ('user', 'agent', 'system', 'bridge')),
            reactor_identity      TEXT NOT NULL,
            reaction_key          TEXT NOT NULL,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_message_id, reactor_type, reactor_identity, reaction_key)
        );

        CREATE INDEX IF NOT EXISTS idx_channel_reactions_message
            ON channel_reactions(channel_message_id);

        ------------------------------------------------------------
        -- AGENT RUNS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS agent_runs (
            run_id                          TEXT PRIMARY KEY,
            project_id                      TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id                         INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            review_round_id                 INTEGER REFERENCES review_rounds(id) ON DELETE SET NULL,
            workspace_id                    TEXT,
            role                            TEXT,
            backend                         TEXT,
            model                           TEXT,
            sender_instance_id              TEXT,
            state                           TEXT NOT NULL DEFAULT 'unknown'
                                            CHECK (state IN (
                                                'running',
                                                'retrying',
                                                'aborting',
                                                'rerun_requested',
                                                'rerun_accepted',
                                                'complete',
                                                'failed',
                                                'timeout',
                                                'aborted',
                                                'unknown'
                                            )),
            started_at                      TEXT,
            ended_at                        TEXT,
            duration_ms                     INTEGER,
            pid                             INTEGER,
            exit_code                       INTEGER,
            signal                          TEXT,
            timeout_kind                    TEXT,
            output_status                   TEXT,
            infrastructure_failure_reason   TEXT,
            infrastructure_warning_reason   TEXT,
            artifact_dir                    TEXT,
            stdout_jsonl_path               TEXT,
            stderr_log_path                 TEXT,
            status_json_path                TEXT,
            events_jsonl_path               TEXT,
            rerun_of_run_id                 TEXT,
            fallback_model                  TEXT,
            fallback_from_model             TEXT,
            fallback_from_exit_code         INTEGER,
            latest_stream_entry_id          INTEGER REFERENCES agent_stream_entries(id) ON DELETE SET NULL,
            started_stream_entry_id         INTEGER REFERENCES agent_stream_entries(id) ON DELETE SET NULL,
            heartbeat_count                 INTEGER NOT NULL DEFAULT 0,
            assistant_output_count          INTEGER NOT NULL DEFAULT 0,
            event_count                     INTEGER NOT NULL DEFAULT 0,
            raw_work_event_count            INTEGER NOT NULL DEFAULT 0,
            operator_events_json            TEXT,
            last_heartbeat_at               TEXT,
            last_assistant_output_at        TEXT,
            created_at                      TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                      TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_agent_runs_project_updated
            ON agent_runs(project_id, updated_at DESC, latest_stream_entry_id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_runs_task_updated
            ON agent_runs(task_id, updated_at DESC, latest_stream_entry_id DESC)
            WHERE task_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_agent_runs_state_updated
            ON agent_runs(state, updated_at DESC, latest_stream_entry_id DESC);

        ------------------------------------------------------------
        -- AGENT WORKSPACES
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS agent_workspaces (
            id                    TEXT PRIMARY KEY,
            project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id               INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            branch                TEXT NOT NULL,
            worktree_path         TEXT NOT NULL,
            base_branch           TEXT NOT NULL,
            base_commit           TEXT,
            head_commit           TEXT,
            state                 TEXT NOT NULL DEFAULT 'active'
                                  CHECK (state IN (
                                      'planned',
                                      'active',
                                      'review',
                                      'complete',
                                      'failed',
                                      'archived'
                                  )),
            created_by_run_id     TEXT REFERENCES agent_runs(run_id) ON DELETE SET NULL,
            dev_server_url        TEXT,
            preview_url           TEXT,
            cleanup_policy        TEXT NOT NULL DEFAULT 'keep'
                                  CHECK (cleanup_policy IN (
                                      'keep',
                                      'delete_worktree',
                                      'archive'
                                  )),
            changed_file_summary  TEXT,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(project_id, task_id, branch)
        );

        CREATE INDEX IF NOT EXISTS idx_agent_workspaces_project_updated
            ON agent_workspaces(project_id, updated_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_workspaces_task_updated
            ON agent_workspaces(task_id, updated_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_workspaces_state_updated
            ON agent_workspaces(state, updated_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_agent_workspaces_created_by_run
            ON agent_workspaces(created_by_run_id)
            WHERE created_by_run_id IS NOT NULL;

        ------------------------------------------------------------
        -- DEN-OWNED PI SESSIONS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS pi_sessions (
            session_id                 TEXT PRIMARY KEY,
            project_id                 TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id                    INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            workspace_id               TEXT,
            run_id                     TEXT,
            title                      TEXT,
            tool_profile               TEXT,
            model                      TEXT,
            provider                   TEXT,
            host_id                    TEXT NOT NULL,
            tmux_session_name          TEXT NOT NULL,
            container_id               TEXT,
            container_name             TEXT,
            state                      TEXT NOT NULL DEFAULT 'launching'
                                       CHECK (state IN ('launching', 'running', 'stale', 'terminating', 'completed', 'failed')),
            state_reason               TEXT,
            launch_profile_kind        TEXT NOT NULL,
            launch_profile_id          TEXT,
            launch_profile_json        TEXT NOT NULL,
            launch_command_json        TEXT NOT NULL,
            launch_command_display     TEXT NOT NULL,
            created_at                 TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            started_at                 TEXT,
            last_activity_at           TEXT,
            output_tail                TEXT CHECK (output_tail IS NULL OR length(output_tail) <= 12000),
            output_tail_captured_at    TEXT,
            output_tail_truncated      INTEGER NOT NULL DEFAULT 0,
            output_tail_sha256         TEXT,
            attention_state            TEXT CHECK (attention_state IS NULL OR attention_state IN ('user_input_needed', 'waiting_for_direction', 'blocked', 'stalled')),
            attention_reason           TEXT CHECK (attention_reason IS NULL OR length(attention_reason) <= 2000),
            attention_since_at         TEXT,
            attention_updated_at       TEXT,
            needs_user_input           INTEGER NOT NULL DEFAULT 0,
            ended_at                   TEXT,
            updated_at                 TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            termination_requested_at   TEXT,
            termination_requested_by   TEXT,
            termination_reason         TEXT,
            cleanup_requested_at       TEXT,
            cleanup_requested_by       TEXT,
            cleanup_reason             TEXT,
            cleanup_completed_at       TEXT,
            UNIQUE(project_id, tmux_session_name)
        );

        CREATE INDEX IF NOT EXISTS idx_pi_sessions_project_state_updated
            ON pi_sessions(project_id, state, updated_at DESC, session_id DESC);
        CREATE INDEX IF NOT EXISTS idx_pi_sessions_task_updated
            ON pi_sessions(task_id, updated_at DESC, session_id DESC);
        CREATE INDEX IF NOT EXISTS idx_pi_sessions_run
            ON pi_sessions(run_id)
            WHERE run_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_pi_sessions_workspace
            ON pi_sessions(workspace_id)
            WHERE workspace_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS pi_session_events (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id         INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            workspace_id    TEXT,
            session_id      TEXT NOT NULL REFERENCES pi_sessions(session_id) ON DELETE CASCADE,
            event_type      TEXT NOT NULL,
            payload         TEXT CHECK (payload IS NULL OR length(payload) <= 10240),
            requested_by    TEXT,
            reason          TEXT CHECK (reason IS NULL OR length(reason) <= 2000),
            created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );

        CREATE INDEX IF NOT EXISTS idx_pi_session_events_project_created
            ON pi_session_events(project_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_pi_session_events_session_created
            ON pi_session_events(session_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_pi_session_events_task_created
            ON pi_session_events(task_id, created_at DESC, id DESC)
            WHERE task_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_pi_session_events_type_created
            ON pi_session_events(event_type, created_at DESC, id DESC);

        ------------------------------------------------------------
        -- DESKTOP-PUBLISHED SNAPSHOTS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS desktop_git_snapshots (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
            root_path             TEXT NOT NULL,
            scope_key             TEXT NOT NULL,
            state                 TEXT NOT NULL DEFAULT 'ok'
                                  CHECK (state IN (
                                      'ok',
                                      'path_not_visible',
                                      'not_git_repository',
                                      'git_error',
                                      'source_offline',
                                      'missing'
                                  )),
            branch                TEXT,
            is_detached           INTEGER NOT NULL DEFAULT 0,
            head_sha              TEXT,
            upstream              TEXT,
            ahead                 INTEGER,
            behind                INTEGER,
            dirty_counts          TEXT NOT NULL,
            changed_files         TEXT NOT NULL,
            warnings              TEXT NOT NULL,
            truncated             INTEGER NOT NULL DEFAULT 0,
            source_instance_id    TEXT NOT NULL,
            source_display_name   TEXT,
            observed_at           TEXT NOT NULL,
            received_at           TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(project_id, scope_key)
        );

        CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_project_observed
            ON desktop_git_snapshots(project_id, observed_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_task_observed
            ON desktop_git_snapshots(task_id, observed_at DESC, id DESC)
            WHERE task_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_workspace_observed
            ON desktop_git_snapshots(workspace_id, observed_at DESC, id DESC)
            WHERE workspace_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_source_observed
            ON desktop_git_snapshots(source_instance_id, observed_at DESC, id DESC);

        CREATE TABLE IF NOT EXISTS desktop_diff_snapshots (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
            root_path             TEXT NOT NULL,
            path                  TEXT,
            base_ref              TEXT,
            head_ref              TEXT,
            diff_key              TEXT NOT NULL,
            max_bytes             INTEGER NOT NULL,
            staged                INTEGER NOT NULL DEFAULT 0,
            diff                  TEXT NOT NULL,
            truncated             INTEGER NOT NULL DEFAULT 0,
            binary                INTEGER NOT NULL DEFAULT 0,
            warnings              TEXT NOT NULL,
            source_instance_id    TEXT NOT NULL,
            source_display_name   TEXT,
            observed_at           TEXT NOT NULL,
            received_at           TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(project_id, diff_key)
        );

        CREATE INDEX IF NOT EXISTS idx_desktop_diff_snapshots_project_observed
            ON desktop_diff_snapshots(project_id, observed_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_desktop_diff_snapshots_source_observed
            ON desktop_diff_snapshots(source_instance_id, observed_at DESC, id DESC);

        CREATE TABLE IF NOT EXISTS desktop_session_snapshots (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
            session_id            TEXT NOT NULL,
            parent_session_id     TEXT,
            agent_identity        TEXT,
            role                  TEXT,
            current_command       TEXT,
            current_phase         TEXT,
            title                 TEXT,
            display_name          TEXT,
            cwd                   TEXT,
            kind                  TEXT,
            backend               TEXT,
            status                TEXT,
            started_at            TEXT,
            last_activity_at      TEXT,
            exited_at             TEXT,
            exit_code             INTEGER,
            source_display_name   TEXT,
            capabilities          TEXT,
            recent_activity       TEXT,
            child_sessions        TEXT,
            control_capabilities  TEXT,
            warnings              TEXT NOT NULL,
            source_instance_id    TEXT NOT NULL,
            observed_at           TEXT NOT NULL,
            received_at           TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(project_id, source_instance_id, session_id)
        );

        CREATE INDEX IF NOT EXISTS idx_desktop_session_snapshots_project_observed
            ON desktop_session_snapshots(project_id, observed_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_desktop_session_snapshots_task_observed
            ON desktop_session_snapshots(task_id, observed_at DESC, id DESC)
            WHERE task_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_desktop_session_snapshots_source_observed
            ON desktop_session_snapshots(source_instance_id, observed_at DESC, id DESC);

        ------------------------------------------------------------
        -- DESKTOP SESSION EVENTS (append-only lifecycle/control event log)
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS desktop_session_events (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
            source_instance_id    TEXT NOT NULL,
            session_id            TEXT NOT NULL,
            event_type            TEXT NOT NULL
                                  CHECK (event_type IN (
                                      'created',
                                      'discovered',
                                      'status_changed',
                                      'capabilities_changed',
                                      'attached',
                                      'detached',
                                      'input_sent',
                                      'resize_requested',
                                      'terminate_requested',
                                      'terminate_completed',
                                      'reconnect',
                                      'reconnect_requested',
                                      'reconnected',
                                      'lease_acquired',
                                      'lease_lost',
                                      'lease_conflict',
                                      'warning',
                                      'crashed',
                                      'exited',
                                      'snapshot_published',
                                      'snapshot_publish_failed'
                                  )),
            payload               TEXT
                                  CHECK (length(payload) <= 10240),
            requested_by          TEXT,
            reason                TEXT
                                  CHECK (reason IS NULL OR length(reason) <= 2000),
            observed_at           TEXT NOT NULL,
            created_at            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );

        CREATE INDEX IF NOT EXISTS idx_desktop_session_events_project_created
            ON desktop_session_events(project_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_desktop_session_events_source_created
            ON desktop_session_events(source_instance_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_desktop_session_events_session_created
            ON desktop_session_events(session_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_desktop_session_events_task_created
            ON desktop_session_events(task_id, created_at DESC, id DESC)
            WHERE task_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_desktop_session_events_event_type_created
            ON desktop_session_events(event_type, created_at DESC, id DESC);

        ------------------------------------------------------------
        -- CONSOLIDATION TOPICS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS consolidation_topics (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            slug          TEXT NOT NULL UNIQUE,
            display_name  TEXT NOT NULL,
            description   TEXT,
            aliases       TEXT,
            status        TEXT NOT NULL DEFAULT 'active'
                          CHECK (status IN ('active', 'inactive', 'deprecated')),
            owning_space  TEXT REFERENCES projects(id) ON DELETE SET NULL,
            created_at    TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_consolidation_topics_status
            ON consolidation_topics(status);
        CREATE INDEX IF NOT EXISTS idx_consolidation_topics_owning_space
            ON consolidation_topics(owning_space);
        CREATE INDEX IF NOT EXISTS idx_consolidation_topics_slug_status
            ON consolidation_topics(slug, status);

        ------------------------------------------------------------
        -- TOPIC CLIP QUEUE
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS topic_clip_queue_items (
            id                     INTEGER PRIMARY KEY AUTOINCREMENT,
            source_agent           TEXT NOT NULL,
            source_session_id      TEXT,
            source_conversation_id TEXT,
            source_message_id      INTEGER,
            owning_space           TEXT REFERENCES projects(id) ON DELETE SET NULL,
            canonical_topic_slugs  TEXT NOT NULL,
            raw_content            TEXT NOT NULL,
            status                 TEXT NOT NULL DEFAULT 'pending'
                                   CHECK (status IN ('pending', 'claimed', 'processed', 'discarded', 'escalated')),
            claim_key              TEXT,
            claimed_at             TEXT,
            claim_expires_at       TEXT,
            created_at             TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at             TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_topic_clips_status_created
            ON topic_clip_queue_items(status, created_at);
        CREATE INDEX IF NOT EXISTS idx_topic_clips_claim_key
            ON topic_clip_queue_items(claim_key) WHERE claim_key IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_topic_clips_owning_space
            ON topic_clip_queue_items(owning_space);
        CREATE INDEX IF NOT EXISTS idx_topic_clips_source_agent
            ON topic_clip_queue_items(source_agent);

        ------------------------------------------------------------
        -- CURATION DECISIONS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS curation_decisions (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            clip_id     INTEGER NOT NULL REFERENCES topic_clip_queue_items(id) ON DELETE CASCADE,
            decision    TEXT NOT NULL
                        CHECK (decision IN ('processed', 'discarded', 'escalated')),
            reason      TEXT,
            decided_by  TEXT NOT NULL,
            decided_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_curation_decisions_clip
            ON curation_decisions(clip_id);
        """;

    private async Task RunMigrationsAsync(SqliteConnection connection)
    {
        await EnsureDocumentsSummaryAndMemoryDocTypeAsync(connection);
        await EnsureAgentGuidanceSchemaAsync(connection);
        await EnsureAgentRunSchemaAsync(connection);
        await EnsureConsolidationTopicSchemaAsync(connection);
        await EnsureTopicClipQueueSchemaAsync(connection);
        await EnsureCollaborationSchemaAsync(connection);
        await EnsureAgentWorkspaceSchemaAsync(connection);
        await EnsurePiSessionSchemaAsync(connection);
        await EnsureDesktopSnapshotSchemaAsync(connection);
        await EnsureBlackboardSchemaAsync(connection);

        // Add session_id column to agent_sessions if it doesn't exist.
        // SQLite has no ALTER TABLE ... ADD COLUMN IF NOT EXISTS,
        // so we check via PRAGMA table_info.
        await TryAddColumnAsync(connection, "agent_sessions", "session_id", "TEXT");
        await TryAddColumnAsync(connection, "desktop_diff_snapshots", "source_display_name", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "title", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "display_name", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "cwd", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "kind", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "backend", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "status", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "started_at", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "last_activity_at", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "exited_at", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "exit_code", "INTEGER");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "source_display_name", "TEXT");
        await TryAddColumnAsync(connection, "desktop_session_snapshots", "capabilities", "TEXT");
        await TryAddColumnAsync(connection, "dispatch_entries", "completed_by", "TEXT");
        await TryAddColumnAsync(connection, "dispatch_entries", "context_json", "TEXT");
        await TryAddColumnAsync(connection, "messages", "intent",
            """
            TEXT NOT NULL DEFAULT 'general' CHECK (intent IN (
                'general',
                'note',
                'status_update',
                'question',
                'answer',
                'handoff',
                'review_request',
                'review_feedback',
                'review_approval',
                'task_ready',
                'task_blocked'
            ))
            """);
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_base_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_base_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_head_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_head_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_base_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_base_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_head_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_head_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "delta_base_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "inherited_commit_count",
            "INTEGER CHECK (inherited_commit_count IS NULL OR inherited_commit_count >= 0)");
        await TryAddColumnAsync(connection, "review_rounds", "task_local_commit_count",
            "INTEGER CHECK (task_local_commit_count IS NULL OR task_local_commit_count >= 0)");
        await TryAddColumnAsync(connection, "agent_runs", "raw_work_event_count",
            "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(connection, "agent_runs", "operator_events_json",
            "TEXT");
        await TryAddColumnAsync(connection, "projects", "kind",
            "TEXT NOT NULL DEFAULT 'project' CHECK (kind IN ('project', 'personal', 'assistant', 'knowledge_base', 'system'))");
        await TryAddColumnAsync(connection, "projects", "visibility",
            "TEXT NOT NULL DEFAULT 'normal' CHECK (visibility IN ('normal', 'hidden', 'archived'))");
        await TryAddColumnAsync(connection, "projects", "owner", "TEXT");
        await TryAddColumnAsync(connection, "projects", "settings_json", "TEXT");
        await BackfillProjectSpaceMetadataAsync(connection);
        await EnsureChannelSchemaAsync(connection);
        await EnsureProjectChannelsAsync(connection);
        await EnsureIndexAsync(connection, "idx_messages_project_intent",
            "CREATE INDEX IF NOT EXISTS idx_messages_project_intent ON messages(project_id, intent)");
        await EnsureIndexAsync(connection, "idx_agent_bindings_project_status",
            "CREATE INDEX IF NOT EXISTS idx_agent_bindings_project_status ON agent_instance_bindings(project_id, status, last_heartbeat DESC)");
        await EnsureIndexAsync(connection, "idx_agent_bindings_project_role_status",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_bindings_project_role_status
            ON agent_instance_bindings(project_id, role, status, last_heartbeat DESC)
            WHERE role IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_agent_bindings_project_agent_status",
            "CREATE INDEX IF NOT EXISTS idx_agent_bindings_project_agent_status ON agent_instance_bindings(project_id, agent_identity, status, last_heartbeat DESC)");
        await EnsureIndexAsync(connection, "idx_agent_bindings_session",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_bindings_session
            ON agent_instance_bindings(session_id)
            WHERE session_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_agent_stream_sender_created",
            "CREATE INDEX IF NOT EXISTS idx_agent_stream_sender_created ON agent_stream_entries(sender, created_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_agent_stream_sender_instance_created",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_stream_sender_instance_created
            ON agent_stream_entries(sender_instance_id, created_at DESC, id DESC)
            WHERE sender_instance_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_agent_stream_recipient_agent_created",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_stream_recipient_agent_created
            ON agent_stream_entries(recipient_agent, created_at DESC, id DESC)
            WHERE recipient_agent IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_agent_stream_recipient_role_created",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_stream_recipient_role_created
            ON agent_stream_entries(recipient_role, created_at DESC, id DESC)
            WHERE recipient_role IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_agent_stream_recipient_instance_created",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_stream_recipient_instance_created
            ON agent_stream_entries(recipient_instance_id, created_at DESC, id DESC)
            WHERE recipient_instance_id IS NOT NULL
            """);
        await BackfillMessageIntentsAsync(connection);
        await BackfillHistoricalDispatchCleanupAsync(connection);
        await BackfillAgentStreamDedupAsync(connection);
        await EnsureIndexAsync(connection, "idx_agent_stream_dedup",
            """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_stream_dedup
            ON agent_stream_entries(dedup_key) WHERE dedup_key IS NOT NULL
            """);
    }

    private static async Task EnsureAgentGuidanceSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_guidance_entries (
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
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureAgentGuidanceDocumentForeignKeyAsync(connection);

        await EnsureIndexAsync(connection, "idx_agent_guidance_scope_order",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_guidance_scope_order
            ON agent_guidance_entries(project_id, sort_order, importance, document_project_id, document_slug)
            """);
        await EnsureIndexAsync(connection, "idx_agent_guidance_document",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_guidance_document
            ON agent_guidance_entries(document_project_id, document_slug)
            """);
    }

    private static async Task EnsureAgentGuidanceDocumentForeignKeyAsync(SqliteConnection connection)
    {
        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agent_guidance_entries'";
        var schema = (string?)await schemaCmd.ExecuteScalarAsync();
        if (schema is null || !schema.Contains("documents_old", StringComparison.OrdinalIgnoreCase))
            return;

        await using (var fkOff = connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            await fkOff.ExecuteNonQueryAsync();
        }

        try
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = """
                BEGIN;

                CREATE TABLE agent_guidance_entries_new (
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

                INSERT INTO agent_guidance_entries_new (
                    id, project_id, document_project_id, document_slug, importance,
                    audience, sort_order, notes, created_at, updated_at
                )
                SELECT
                    id, project_id, document_project_id, document_slug, importance,
                    audience, sort_order, notes, created_at, updated_at
                FROM agent_guidance_entries;

                DROP TABLE agent_guidance_entries;
                ALTER TABLE agent_guidance_entries_new RENAME TO agent_guidance_entries;

                COMMIT;
                """;
            await migrateCmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var fkOn = connection.CreateCommand();
            fkOn.CommandText = "PRAGMA foreign_keys = ON;";
            await fkOn.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureAgentRunSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_runs (
                run_id                          TEXT PRIMARY KEY,
                project_id                      TEXT REFERENCES projects(id) ON DELETE SET NULL,
                task_id                         INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                review_round_id                 INTEGER REFERENCES review_rounds(id) ON DELETE SET NULL,
                workspace_id                    TEXT,
                role                            TEXT,
                backend                         TEXT,
                model                           TEXT,
                sender_instance_id              TEXT,
                state                           TEXT NOT NULL DEFAULT 'unknown'
                                                CHECK (state IN (
                                                    'running',
                                                    'retrying',
                                                    'aborting',
                                                    'rerun_requested',
                                                    'rerun_accepted',
                                                    'complete',
                                                    'failed',
                                                    'timeout',
                                                    'aborted',
                                                    'unknown'
                                                )),
                started_at                      TEXT,
                ended_at                        TEXT,
                duration_ms                     INTEGER,
                pid                             INTEGER,
                exit_code                       INTEGER,
                signal                          TEXT,
                timeout_kind                    TEXT,
                output_status                   TEXT,
                infrastructure_failure_reason   TEXT,
                infrastructure_warning_reason   TEXT,
                artifact_dir                    TEXT,
                stdout_jsonl_path               TEXT,
                stderr_log_path                 TEXT,
                status_json_path                TEXT,
                events_jsonl_path               TEXT,
                rerun_of_run_id                 TEXT,
                fallback_model                  TEXT,
                fallback_from_model             TEXT,
                fallback_from_exit_code         INTEGER,
                latest_stream_entry_id          INTEGER REFERENCES agent_stream_entries(id) ON DELETE SET NULL,
                started_stream_entry_id         INTEGER REFERENCES agent_stream_entries(id) ON DELETE SET NULL,
                heartbeat_count                 INTEGER NOT NULL DEFAULT 0,
                assistant_output_count          INTEGER NOT NULL DEFAULT 0,
                event_count                     INTEGER NOT NULL DEFAULT 0,
                raw_work_event_count            INTEGER NOT NULL DEFAULT 0,
                operator_events_json            TEXT,
                last_heartbeat_at               TEXT,
                last_assistant_output_at        TEXT,
                created_at                      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at                      TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_agent_runs_project_updated",
            "CREATE INDEX IF NOT EXISTS idx_agent_runs_project_updated ON agent_runs(project_id, updated_at DESC, latest_stream_entry_id DESC)");
        await EnsureIndexAsync(connection, "idx_agent_runs_task_updated",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_runs_task_updated
            ON agent_runs(task_id, updated_at DESC, latest_stream_entry_id DESC)
            WHERE task_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_agent_runs_state_updated",
            "CREATE INDEX IF NOT EXISTS idx_agent_runs_state_updated ON agent_runs(state, updated_at DESC, latest_stream_entry_id DESC)");
    }

    private static async Task EnsureCollaborationSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS collaboration_sessions (
                id                          INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id                  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id                     INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                message_id                  INTEGER REFERENCES messages(id) ON DELETE SET NULL,
                agent_stream_entry_id       INTEGER REFERENCES agent_stream_entries(id) ON DELETE SET NULL,
                pi_run_id                   TEXT,
                pi_session_id               TEXT,
                desktop_operator_session_id TEXT,
                title                       TEXT,
                status                      TEXT NOT NULL DEFAULT 'active'
                                            CHECK (status IN ('active', 'resolved', 'archived')),
                created_by                  TEXT,
                created_at                  TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at                  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS collaboration_turns (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id          INTEGER NOT NULL REFERENCES collaboration_sessions(id) ON DELETE CASCADE,
                turn_order          INTEGER NOT NULL,
                role                TEXT,
                source_kind         TEXT,
                source_ref          TEXT,
                source_label        TEXT,
                source_uri          TEXT,
                source_context      TEXT,
                raw_markdown        TEXT NOT NULL,
                source_content_hash TEXT NOT NULL,
                segmenter_version   TEXT NOT NULL,
                created_at          TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(session_id, turn_order)
            );

            CREATE TABLE IF NOT EXISTS collaboration_segments (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                turn_id         INTEGER NOT NULL REFERENCES collaboration_turns(id) ON DELETE CASCADE,
                sequence_number INTEGER NOT NULL,
                segment_hash    TEXT NOT NULL,
                segment_type    TEXT NOT NULL CHECK (segment_type IN (
                                    'heading',
                                    'paragraph',
                                    'code_block',
                                    'list',
                                    'block_quote'
                                )),
                raw_markdown    TEXT NOT NULL,
                text            TEXT,
                heading_level   INTEGER CHECK (heading_level IS NULL OR heading_level BETWEEN 1 AND 6),
                code_language   TEXT,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(turn_id, sequence_number),
                UNIQUE(turn_id, segment_hash)
            );

            CREATE TABLE IF NOT EXISTS collaboration_annotations (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id      INTEGER NOT NULL REFERENCES collaboration_sessions(id) ON DELETE CASCADE,
                turn_id         INTEGER NOT NULL REFERENCES collaboration_turns(id) ON DELETE CASCADE,
                segment_id      INTEGER NOT NULL REFERENCES collaboration_segments(id) ON DELETE CASCADE,
                segment_hash    TEXT NOT NULL,
                annotation_type TEXT NOT NULL CHECK (annotation_type IN ('note', 'skip', 'done', 'flag')),
                body            TEXT,
                created_by      TEXT,
                updated_by      TEXT,
                revision        INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS collaboration_response_drafts (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  INTEGER NOT NULL REFERENCES collaboration_sessions(id) ON DELETE CASCADE,
                turn_id     INTEGER REFERENCES collaboration_turns(id) ON DELETE SET NULL,
                content     TEXT NOT NULL,
                created_by  TEXT,
                updated_by  TEXT,
                revision    INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
                created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_collaboration_sessions_project_updated",
            "CREATE INDEX IF NOT EXISTS idx_collaboration_sessions_project_updated ON collaboration_sessions(project_id, updated_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_collaboration_sessions_task_updated",
            """
            CREATE INDEX IF NOT EXISTS idx_collaboration_sessions_task_updated
            ON collaboration_sessions(task_id, updated_at DESC, id DESC)
            WHERE task_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_collaboration_sessions_message",
            """
            CREATE INDEX IF NOT EXISTS idx_collaboration_sessions_message
            ON collaboration_sessions(message_id)
            WHERE message_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_collaboration_sessions_stream",
            """
            CREATE INDEX IF NOT EXISTS idx_collaboration_sessions_stream
            ON collaboration_sessions(agent_stream_entry_id)
            WHERE agent_stream_entry_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_collaboration_sessions_pi_run",
            """
            CREATE INDEX IF NOT EXISTS idx_collaboration_sessions_pi_run
            ON collaboration_sessions(pi_run_id)
            WHERE pi_run_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_collaboration_sessions_pi_session",
            """
            CREATE INDEX IF NOT EXISTS idx_collaboration_sessions_pi_session
            ON collaboration_sessions(pi_session_id)
            WHERE pi_session_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_collaboration_sessions_desktop_operator",
            """
            CREATE INDEX IF NOT EXISTS idx_collaboration_sessions_desktop_operator
            ON collaboration_sessions(desktop_operator_session_id)
            WHERE desktop_operator_session_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_collaboration_turns_session_order",
            "CREATE INDEX IF NOT EXISTS idx_collaboration_turns_session_order ON collaboration_turns(session_id, turn_order)");
        await EnsureIndexAsync(connection, "idx_collaboration_segments_turn_order",
            "CREATE INDEX IF NOT EXISTS idx_collaboration_segments_turn_order ON collaboration_segments(turn_id, sequence_number)");
        await EnsureIndexAsync(connection, "idx_collaboration_annotations_session_updated",
            "CREATE INDEX IF NOT EXISTS idx_collaboration_annotations_session_updated ON collaboration_annotations(session_id, updated_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_collaboration_annotations_segment",
            "CREATE INDEX IF NOT EXISTS idx_collaboration_annotations_segment ON collaboration_annotations(segment_id, updated_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_collaboration_drafts_session_updated",
            "CREATE INDEX IF NOT EXISTS idx_collaboration_drafts_session_updated ON collaboration_response_drafts(session_id, updated_at DESC, id DESC)");
    }

    private static async Task EnsureAgentWorkspaceSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_workspaces (
                id                    TEXT PRIMARY KEY,
                project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id               INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                branch                TEXT NOT NULL,
                worktree_path         TEXT NOT NULL,
                base_branch           TEXT NOT NULL,
                base_commit           TEXT,
                head_commit           TEXT,
                state                 TEXT NOT NULL DEFAULT 'active'
                                      CHECK (state IN (
                                          'planned',
                                          'active',
                                          'review',
                                          'complete',
                                          'failed',
                                          'archived'
                                      )),
                created_by_run_id     TEXT REFERENCES agent_runs(run_id) ON DELETE SET NULL,
                dev_server_url        TEXT,
                preview_url           TEXT,
                cleanup_policy        TEXT NOT NULL DEFAULT 'keep'
                                      CHECK (cleanup_policy IN (
                                          'keep',
                                          'delete_worktree',
                                          'archive'
                                      )),
                changed_file_summary  TEXT,
                created_at            TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(project_id, task_id, branch)
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_agent_workspaces_project_updated",
            "CREATE INDEX IF NOT EXISTS idx_agent_workspaces_project_updated ON agent_workspaces(project_id, updated_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_agent_workspaces_task_updated",
            "CREATE INDEX IF NOT EXISTS idx_agent_workspaces_task_updated ON agent_workspaces(task_id, updated_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_agent_workspaces_state_updated",
            "CREATE INDEX IF NOT EXISTS idx_agent_workspaces_state_updated ON agent_workspaces(state, updated_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_agent_workspaces_created_by_run",
            """
            CREATE INDEX IF NOT EXISTS idx_agent_workspaces_created_by_run
            ON agent_workspaces(created_by_run_id)
            WHERE created_by_run_id IS NOT NULL
            """);
    }

    private static async Task EnsurePiSessionSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pi_sessions (
                session_id                 TEXT PRIMARY KEY,
                project_id                 TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id                    INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                workspace_id               TEXT,
                run_id                     TEXT,
                title                      TEXT,
                tool_profile               TEXT,
                model                      TEXT,
                provider                   TEXT,
                host_id                    TEXT NOT NULL,
                tmux_session_name          TEXT NOT NULL,
                container_id               TEXT,
                container_name             TEXT,
                state                      TEXT NOT NULL DEFAULT 'launching'
                                           CHECK (state IN ('launching', 'running', 'stale', 'terminating', 'completed', 'failed')),
                state_reason               TEXT,
                launch_profile_kind        TEXT NOT NULL,
                launch_profile_id          TEXT,
                launch_profile_json        TEXT NOT NULL,
                launch_command_json        TEXT NOT NULL,
                launch_command_display     TEXT NOT NULL,
                created_at                 TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                started_at                 TEXT,
                last_activity_at           TEXT,
                output_tail                TEXT CHECK (output_tail IS NULL OR length(output_tail) <= 12000),
                output_tail_captured_at    TEXT,
                output_tail_truncated      INTEGER NOT NULL DEFAULT 0,
                output_tail_sha256         TEXT,
                attention_state            TEXT CHECK (attention_state IS NULL OR attention_state IN ('user_input_needed', 'waiting_for_direction', 'blocked', 'stalled')),
                attention_reason           TEXT CHECK (attention_reason IS NULL OR length(attention_reason) <= 2000),
                attention_since_at         TEXT,
                attention_updated_at       TEXT,
                needs_user_input           INTEGER NOT NULL DEFAULT 0,
                ended_at                   TEXT,
                updated_at                 TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                termination_requested_at   TEXT,
                termination_requested_by   TEXT,
                termination_reason         TEXT,
                cleanup_requested_at       TEXT,
                cleanup_requested_by       TEXT,
                cleanup_reason             TEXT,
                cleanup_completed_at       TEXT,
                UNIQUE(project_id, tmux_session_name)
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await using var eventsCmd = connection.CreateCommand();
        eventsCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pi_session_events (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id         INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                workspace_id    TEXT,
                session_id      TEXT NOT NULL REFERENCES pi_sessions(session_id) ON DELETE CASCADE,
                event_type      TEXT NOT NULL,
                payload         TEXT CHECK (payload IS NULL OR length(payload) <= 10240),
                requested_by    TEXT,
                reason          TEXT CHECK (reason IS NULL OR length(reason) <= 2000),
                created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            )
            """;
        await eventsCmd.ExecuteNonQueryAsync();

        await TryAddColumnAsync(connection, "pi_sessions", "output_tail", "TEXT CHECK (output_tail IS NULL OR length(output_tail) <= 12000)");
        await TryAddColumnAsync(connection, "pi_sessions", "output_tail_captured_at", "TEXT");
        await TryAddColumnAsync(connection, "pi_sessions", "output_tail_truncated", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(connection, "pi_sessions", "output_tail_sha256", "TEXT");
        await TryAddColumnAsync(connection, "pi_sessions", "attention_state", "TEXT CHECK (attention_state IS NULL OR attention_state IN ('user_input_needed', 'waiting_for_direction', 'blocked', 'stalled'))");
        await TryAddColumnAsync(connection, "pi_sessions", "attention_reason", "TEXT CHECK (attention_reason IS NULL OR length(attention_reason) <= 2000)");
        await TryAddColumnAsync(connection, "pi_sessions", "attention_since_at", "TEXT");
        await TryAddColumnAsync(connection, "pi_sessions", "attention_updated_at", "TEXT");
        await TryAddColumnAsync(connection, "pi_sessions", "needs_user_input", "INTEGER NOT NULL DEFAULT 0");

        await EnsureIndexAsync(connection, "idx_pi_sessions_project_state_updated",
            "CREATE INDEX IF NOT EXISTS idx_pi_sessions_project_state_updated ON pi_sessions(project_id, state, updated_at DESC, session_id DESC)");
        await EnsureIndexAsync(connection, "idx_pi_sessions_task_updated",
            "CREATE INDEX IF NOT EXISTS idx_pi_sessions_task_updated ON pi_sessions(task_id, updated_at DESC, session_id DESC)");
        await EnsureIndexAsync(connection, "idx_pi_sessions_run",
            "CREATE INDEX IF NOT EXISTS idx_pi_sessions_run ON pi_sessions(run_id) WHERE run_id IS NOT NULL");
        await EnsureIndexAsync(connection, "idx_pi_sessions_workspace",
            "CREATE INDEX IF NOT EXISTS idx_pi_sessions_workspace ON pi_sessions(workspace_id) WHERE workspace_id IS NOT NULL");
        await EnsureIndexAsync(connection, "idx_pi_session_events_project_created",
            "CREATE INDEX IF NOT EXISTS idx_pi_session_events_project_created ON pi_session_events(project_id, created_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_pi_session_events_session_created",
            "CREATE INDEX IF NOT EXISTS idx_pi_session_events_session_created ON pi_session_events(session_id, created_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_pi_session_events_task_created",
            "CREATE INDEX IF NOT EXISTS idx_pi_session_events_task_created ON pi_session_events(task_id, created_at DESC, id DESC) WHERE task_id IS NOT NULL");
        await EnsureIndexAsync(connection, "idx_pi_session_events_type_created",
            "CREATE INDEX IF NOT EXISTS idx_pi_session_events_type_created ON pi_session_events(event_type, created_at DESC, id DESC)");
    }

    private static async Task EnsureDesktopSnapshotSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS desktop_git_snapshots (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
                root_path             TEXT NOT NULL,
                scope_key             TEXT NOT NULL,
                state                 TEXT NOT NULL DEFAULT 'ok'
                                      CHECK (state IN (
                                          'ok',
                                          'path_not_visible',
                                          'not_git_repository',
                                          'git_error',
                                          'source_offline',
                                          'missing'
                                      )),
                branch                TEXT,
                is_detached           INTEGER NOT NULL DEFAULT 0,
                head_sha              TEXT,
                upstream              TEXT,
                ahead                 INTEGER,
                behind                INTEGER,
                dirty_counts          TEXT NOT NULL,
                changed_files         TEXT NOT NULL,
                warnings              TEXT NOT NULL,
                truncated             INTEGER NOT NULL DEFAULT 0,
                source_instance_id    TEXT NOT NULL,
                source_display_name   TEXT,
                observed_at           TEXT NOT NULL,
                received_at           TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(project_id, scope_key)
            );

            CREATE TABLE IF NOT EXISTS desktop_diff_snapshots (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
                root_path             TEXT NOT NULL,
                path                  TEXT,
                base_ref              TEXT,
                head_ref              TEXT,
                diff_key              TEXT NOT NULL,
                max_bytes             INTEGER NOT NULL,
                staged                INTEGER NOT NULL DEFAULT 0,
                diff                  TEXT NOT NULL,
                truncated             INTEGER NOT NULL DEFAULT 0,
                binary                INTEGER NOT NULL DEFAULT 0,
                warnings              TEXT NOT NULL,
                source_instance_id    TEXT NOT NULL,
                source_display_name   TEXT,
                observed_at           TEXT NOT NULL,
                received_at           TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(project_id, diff_key)
            );

            CREATE TABLE IF NOT EXISTS desktop_session_snapshots (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
                session_id            TEXT NOT NULL,
                parent_session_id     TEXT,
                agent_identity        TEXT,
                role                  TEXT,
                current_command       TEXT,
                current_phase         TEXT,
                title                 TEXT,
                display_name          TEXT,
                cwd                   TEXT,
                kind                  TEXT,
                backend               TEXT,
                status                TEXT,
                started_at            TEXT,
                last_activity_at      TEXT,
                exited_at             TEXT,
                exit_code             INTEGER,
                source_display_name   TEXT,
                capabilities          TEXT,
                recent_activity       TEXT,
                child_sessions        TEXT,
                control_capabilities  TEXT,
                warnings              TEXT NOT NULL,
                source_instance_id    TEXT NOT NULL,
                observed_at           TEXT NOT NULL,
                received_at           TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at            TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(project_id, source_instance_id, session_id)
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_desktop_git_snapshots_project_observed",
            "CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_project_observed ON desktop_git_snapshots(project_id, observed_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_git_snapshots_task_observed",
            """
            CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_task_observed
            ON desktop_git_snapshots(task_id, observed_at DESC, id DESC)
            WHERE task_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_desktop_git_snapshots_workspace_observed",
            """
            CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_workspace_observed
            ON desktop_git_snapshots(workspace_id, observed_at DESC, id DESC)
            WHERE workspace_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_desktop_git_snapshots_source_observed",
            "CREATE INDEX IF NOT EXISTS idx_desktop_git_snapshots_source_observed ON desktop_git_snapshots(source_instance_id, observed_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_diff_snapshots_project_observed",
            "CREATE INDEX IF NOT EXISTS idx_desktop_diff_snapshots_project_observed ON desktop_diff_snapshots(project_id, observed_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_diff_snapshots_source_observed",
            "CREATE INDEX IF NOT EXISTS idx_desktop_diff_snapshots_source_observed ON desktop_diff_snapshots(source_instance_id, observed_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_session_snapshots_project_observed",
            "CREATE INDEX IF NOT EXISTS idx_desktop_session_snapshots_project_observed ON desktop_session_snapshots(project_id, observed_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_session_snapshots_task_observed",
            """
            CREATE INDEX IF NOT EXISTS idx_desktop_session_snapshots_task_observed
            ON desktop_session_snapshots(task_id, observed_at DESC, id DESC)
            WHERE task_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_desktop_session_snapshots_source_observed",
            "CREATE INDEX IF NOT EXISTS idx_desktop_session_snapshots_source_observed ON desktop_session_snapshots(source_instance_id, observed_at DESC, id DESC)");

        // Append-only session lifecycle/control event log
        await using var eventsTableCmd = connection.CreateCommand();
        eventsTableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS desktop_session_events (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
                source_instance_id    TEXT NOT NULL,
                session_id            TEXT NOT NULL,
                event_type            TEXT NOT NULL
                                      CHECK (event_type IN (
                                          'created',
                                          'discovered',
                                          'status_changed',
                                          'capabilities_changed',
                                          'attached',
                                          'detached',
                                          'input_sent',
                                          'resize_requested',
                                          'terminate_requested',
                                          'terminate_completed',
                                          'reconnect',
                                          'reconnect_requested',
                                          'reconnected',
                                          'lease_acquired',
                                          'lease_lost',
                                          'lease_conflict',
                                          'warning',
                                          'crashed',
                                          'exited',
                                          'snapshot_published',
                                          'snapshot_publish_failed'
                                      )),
                payload               TEXT
                                      CHECK (length(payload) <= 10240),
                requested_by          TEXT,
                reason                TEXT
                                      CHECK (reason IS NULL OR length(reason) <= 2000),
                observed_at           TEXT NOT NULL,
                created_at            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            )
            """;
        await eventsTableCmd.ExecuteNonQueryAsync();
        await EnsureDesktopSessionEventsCompatibilityAsync(connection);

        await EnsureIndexAsync(connection, "idx_desktop_session_events_project_created",
            "CREATE INDEX IF NOT EXISTS idx_desktop_session_events_project_created ON desktop_session_events(project_id, created_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_session_events_source_created",
            "CREATE INDEX IF NOT EXISTS idx_desktop_session_events_source_created ON desktop_session_events(source_instance_id, created_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_session_events_session_created",
            "CREATE INDEX IF NOT EXISTS idx_desktop_session_events_session_created ON desktop_session_events(session_id, created_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_desktop_session_events_task_created",
            """
            CREATE INDEX IF NOT EXISTS idx_desktop_session_events_task_created
            ON desktop_session_events(task_id, created_at DESC, id DESC)
            WHERE task_id IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_desktop_session_events_event_type_created",
            "CREATE INDEX IF NOT EXISTS idx_desktop_session_events_event_type_created ON desktop_session_events(event_type, created_at DESC, id DESC)");
    }

    private static async Task EnsureDesktopSessionEventsCompatibilityAsync(SqliteConnection connection)
    {
        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'desktop_session_events'
            """;
        var schema = (string?)await schemaCmd.ExecuteScalarAsync();
        if (schema is null ||
            (schema.Contains("'reconnect_requested'", StringComparison.Ordinal) &&
             schema.Contains("strftime('%Y-%m-%dT%H:%M:%fZ','now')", StringComparison.Ordinal)))
        {
            return;
        }

        await using var migrateCmd = connection.CreateCommand();
        migrateCmd.CommandText = """
            BEGIN;

            ALTER TABLE desktop_session_events RENAME TO desktop_session_events_old;

            CREATE TABLE desktop_session_events (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id            TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
                workspace_id          TEXT REFERENCES agent_workspaces(id) ON DELETE SET NULL,
                source_instance_id    TEXT NOT NULL,
                session_id            TEXT NOT NULL,
                event_type            TEXT NOT NULL
                                      CHECK (event_type IN (
                                          'created',
                                          'discovered',
                                          'status_changed',
                                          'capabilities_changed',
                                          'attached',
                                          'detached',
                                          'input_sent',
                                          'resize_requested',
                                          'terminate_requested',
                                          'terminate_completed',
                                          'reconnect',
                                          'reconnect_requested',
                                          'reconnected',
                                          'lease_acquired',
                                          'lease_lost',
                                          'lease_conflict',
                                          'warning',
                                          'crashed',
                                          'exited',
                                          'snapshot_published',
                                          'snapshot_publish_failed'
                                      )),
                payload               TEXT
                                      CHECK (length(payload) <= 10240),
                requested_by          TEXT,
                reason                TEXT
                                      CHECK (reason IS NULL OR length(reason) <= 2000),
                observed_at           TEXT NOT NULL,
                created_at            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            INSERT INTO desktop_session_events (
                id, project_id, task_id, workspace_id, source_instance_id, session_id,
                event_type, payload, requested_by, reason, observed_at, created_at
            )
            SELECT
                id, project_id, task_id, workspace_id, source_instance_id, session_id,
                event_type, payload, requested_by, reason, observed_at, created_at
            FROM desktop_session_events_old;

            DROP TABLE desktop_session_events_old;

            COMMIT;
            """;
        try
        {
            await migrateCmd.ExecuteNonQueryAsync();
        }
        catch
        {
            try
            {
                await using var rollbackCmd = connection.CreateCommand();
                rollbackCmd.CommandText = "ROLLBACK;";
                await rollbackCmd.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Preserve the original migration exception if SQLite already ended the transaction.
            }
            throw;
        }
    }

    private static async Task EnsureGlobalProjectAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO projects (id, name, kind, visibility, description)
            VALUES ('_global', 'Global', 'system', 'hidden', 'Cross-project documents and discussions')
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureChannelSchemaAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS channels (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                slug          TEXT NOT NULL UNIQUE,
                display_name  TEXT NOT NULL,
                kind          TEXT NOT NULL
                              CHECK (kind IN ('project_default', 'project_activity', 'task_room', 'ad_hoc', 'system', 'dm', 'small_group')),
                project_id    TEXT REFERENCES projects(id) ON DELETE CASCADE,
                space_id      TEXT REFERENCES projects(id) ON DELETE SET NULL,
                created_by    TEXT NOT NULL DEFAULT 'system',
                visibility    TEXT NOT NULL DEFAULT 'normal'
                              CHECK (visibility IN ('normal', 'private', 'archived')),
                settings_json TEXT,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at    TEXT NOT NULL DEFAULT (datetime('now')),
                archived_at   TEXT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_channels_project_default_unique
                ON channels(project_id)
                WHERE project_id IS NOT NULL AND kind = 'project_default';
            CREATE INDEX IF NOT EXISTS idx_channels_project_kind
                ON channels(project_id, kind);

            CREATE TABLE IF NOT EXISTS channel_messages (
                id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id             INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type            TEXT NOT NULL
                                       CHECK (sender_type IN ('user', 'agent', 'system', 'bridge')),
                sender_identity        TEXT NOT NULL,
                body                   TEXT NOT NULL,
                message_kind           TEXT NOT NULL DEFAULT 'human_text'
                                       CHECK (message_kind IN ('human_text', 'agent_text', 'system_event', 'mirror_summary', 'command', 'command_result')),
                source_kind            TEXT
                                       CHECK (source_kind IS NULL OR source_kind IN ('task_message', 'agent_stream_entry', 'notification', 'worker_run', 'review_round', 'review_finding', 'wake_event', 'external_adapter_message')),
                source_id              TEXT,
                summary                TEXT,
                deep_link              TEXT,
                thread_root_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
                reply_to_message_id    INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
                metadata_json          TEXT,
                created_at             TEXT NOT NULL DEFAULT (datetime('now')),
                edited_at              TEXT,
                deleted_at             TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_channel_messages_channel_created
                ON channel_messages(channel_id, created_at DESC, id DESC);
            CREATE INDEX IF NOT EXISTS idx_channel_messages_source
                ON channel_messages(source_kind, source_id)
                WHERE source_kind IS NOT NULL AND source_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS channel_memberships (
                id                            INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id                    INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                member_type                   TEXT NOT NULL
                                              CHECK (member_type IN ('user', 'agent', 'role', 'group')),
                member_identity               TEXT NOT NULL,
                membership_status             TEXT NOT NULL DEFAULT 'active'
                                              CHECK (membership_status IN ('active', 'muted', 'left', 'banned')),
                wake_policy                   TEXT NOT NULL DEFAULT 'mentions_only'
                                              CHECK (wake_policy IN ('never', 'mentions_only', 'direct_questions_only', 'substantive_digest', 'all_human_messages', 'all_messages_except_self')),
                can_send                      INTEGER NOT NULL DEFAULT 1 CHECK (can_send IN (0, 1)),
                can_react                     INTEGER NOT NULL DEFAULT 1 CHECK (can_react IN (0, 1)),
                can_invite                    INTEGER NOT NULL DEFAULT 0 CHECK (can_invite IN (0, 1)),
                cooldown_seconds              INTEGER NOT NULL DEFAULT 0 CHECK (cooldown_seconds >= 0),
                max_auto_replies_per_window   INTEGER NOT NULL DEFAULT 0 CHECK (max_auto_replies_per_window >= 0),
                settings_json                 TEXT,
                created_at                    TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at                    TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(channel_id, member_type, member_identity)
            );

            CREATE INDEX IF NOT EXISTS idx_channel_memberships_member
                ON channel_memberships(member_type, member_identity, membership_status);

            CREATE TABLE IF NOT EXISTS channel_reactions (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_message_id    INTEGER NOT NULL REFERENCES channel_messages(id) ON DELETE CASCADE,
                reactor_type          TEXT NOT NULL CHECK (reactor_type IN ('user', 'agent', 'system', 'bridge')),
                reactor_identity      TEXT NOT NULL,
                reaction_key          TEXT NOT NULL,
                created_at            TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(channel_message_id, reactor_type, reactor_identity, reaction_key)
            );

            CREATE INDEX IF NOT EXISTS idx_channel_reactions_message
                ON channel_reactions(channel_message_id);

            CREATE TRIGGER IF NOT EXISTS projects_ai_default_channel
            AFTER INSERT ON projects
            WHEN new.kind = 'project'
            BEGIN
                INSERT OR IGNORE INTO channels (slug, display_name, kind, project_id, created_by, settings_json)
                VALUES ('project-' || new.id, new.name, 'project_default', new.id, 'system', '{}');
            END;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureProjectChannelsAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO channels (slug, display_name, kind, project_id, created_by, settings_json)
            SELECT 'project-' || id, name, 'project_default', id, 'system', '{}'
            FROM projects
            WHERE kind = 'project'
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureBlackboardSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS blackboard_entries (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                slug                 TEXT NOT NULL UNIQUE,
                title                TEXT NOT NULL,
                content              TEXT NOT NULL,
                tags                 TEXT,
                idle_ttl_seconds     INTEGER CHECK (idle_ttl_seconds IS NULL OR idle_ttl_seconds > 0),
                created_at           TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at           TEXT NOT NULL DEFAULT (datetime('now')),
                last_accessed_at     TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_blackboard_updated",
            "CREATE INDEX IF NOT EXISTS idx_blackboard_updated ON blackboard_entries(updated_at DESC, id DESC)");
        await EnsureIndexAsync(connection, "idx_blackboard_last_accessed",
            """
            CREATE INDEX IF NOT EXISTS idx_blackboard_last_accessed
            ON blackboard_entries(last_accessed_at ASC)
            WHERE idle_ttl_seconds IS NOT NULL
            """);
    }

    private static async Task EnsureConsolidationTopicSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS consolidation_topics (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                slug          TEXT NOT NULL UNIQUE,
                display_name  TEXT NOT NULL,
                description   TEXT,
                aliases       TEXT,
                status        TEXT NOT NULL DEFAULT 'active'
                              CHECK (status IN ('active', 'inactive', 'deprecated')),
                owning_space  TEXT REFERENCES projects(id) ON DELETE SET NULL,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_consolidation_topics_status",
            "CREATE INDEX IF NOT EXISTS idx_consolidation_topics_status ON consolidation_topics(status)");
        await EnsureIndexAsync(connection, "idx_consolidation_topics_owning_space",
            "CREATE INDEX IF NOT EXISTS idx_consolidation_topics_owning_space ON consolidation_topics(owning_space)");
        await EnsureIndexAsync(connection, "idx_consolidation_topics_slug_status",
            "CREATE INDEX IF NOT EXISTS idx_consolidation_topics_slug_status ON consolidation_topics(slug, status)");
    }

    private static async Task EnsureTopicClipQueueSchemaAsync(SqliteConnection connection)
    {
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS topic_clip_queue_items (
                id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                source_agent           TEXT NOT NULL,
                source_session_id      TEXT,
                source_conversation_id TEXT,
                source_message_id      INTEGER,
                owning_space           TEXT REFERENCES projects(id) ON DELETE SET NULL,
                canonical_topic_slugs  TEXT NOT NULL,
                raw_content            TEXT NOT NULL,
                status                 TEXT NOT NULL DEFAULT 'pending'
                                       CHECK (status IN ('pending', 'claimed', 'processed', 'discarded', 'escalated')),
                claim_key              TEXT,
                claimed_at             TEXT,
                claim_expires_at       TEXT,
                created_at             TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at             TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;
        await tableCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_topic_clips_status_created",
            "CREATE INDEX IF NOT EXISTS idx_topic_clips_status_created ON topic_clip_queue_items(status, created_at)");
        await EnsureIndexAsync(connection, "idx_topic_clips_claim_key",
            """
            CREATE INDEX IF NOT EXISTS idx_topic_clips_claim_key
            ON topic_clip_queue_items(claim_key) WHERE claim_key IS NOT NULL
            """);
        await EnsureIndexAsync(connection, "idx_topic_clips_owning_space",
            "CREATE INDEX IF NOT EXISTS idx_topic_clips_owning_space ON topic_clip_queue_items(owning_space)");
        await EnsureIndexAsync(connection, "idx_topic_clips_source_agent",
            "CREATE INDEX IF NOT EXISTS idx_topic_clips_source_agent ON topic_clip_queue_items(source_agent)");

        await using var decisionsCmd = connection.CreateCommand();
        decisionsCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS curation_decisions (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                clip_id     INTEGER NOT NULL REFERENCES topic_clip_queue_items(id) ON DELETE CASCADE,
                decision    TEXT NOT NULL
                            CHECK (decision IN ('processed', 'discarded', 'escalated')),
                reason      TEXT,
                decided_by  TEXT NOT NULL,
                decided_at  TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;
        await decisionsCmd.ExecuteNonQueryAsync();

        await EnsureIndexAsync(connection, "idx_curation_decisions_clip",
            "CREATE INDEX IF NOT EXISTS idx_curation_decisions_clip ON curation_decisions(clip_id)");
    }

    private static async Task TryAddColumnAsync(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == column)
                return; // column already exists
        }
        await reader.CloseAsync();

        await using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}";
        try
        {
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // A parallel initializer may have added the column between the PRAGMA check and ALTER TABLE.
        }
    }

    private static async Task EnsureIndexAsync(SqliteConnection connection, string indexName, string createIndexSql)
    {
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name";
        checkCmd.Parameters.AddWithValue("@name", indexName);
        var exists = await checkCmd.ExecuteScalarAsync();
        if (exists is not null)
            return;

        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = createIndexSql;
        await createCmd.ExecuteNonQueryAsync();
    }

    private static async Task BackfillMessageIntentsAsync(SqliteConnection connection)
    {
        var pendingUpdates = new List<(int Id, string Intent)>();

        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = """
                SELECT id, metadata
                FROM messages
                WHERE intent = 'general' AND metadata IS NOT NULL
                """;

            await using var reader = await selectCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var metadataJson = reader.GetString(1);

                try
                {
                    var metadata = JsonSerializer.Deserialize<JsonElement>(metadataJson);
                    var intent = MessageIntentCompatibility.DeriveFromMetadata(metadata);
                    if (intent is not null && intent != MessageIntent.General)
                        pendingUpdates.Add((id, intent.Value.ToDbValue()));
                }
                catch (JsonException)
                {
                    // Leave malformed legacy metadata at the default 'general' intent.
                }
            }
        }

        foreach (var update in pendingUpdates)
        {
            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE messages SET intent = @intent WHERE id = @id";
            updateCmd.Parameters.AddWithValue("@intent", update.Intent);
            updateCmd.Parameters.AddWithValue("@id", update.Id);
            await updateCmd.ExecuteNonQueryAsync();
        }
    }

    private async Task BackfillHistoricalDispatchCleanupAsync(SqliteConnection connection)
    {
        var expiredTerminalTaskDispatches = await ExpireHistoricalDispatchesForTerminalTasksAsync(connection);
        var expiredSupersededDispatches = await ExpireHistoricalSupersededTaskTargetDispatchesAsync(connection);
        if (expiredTerminalTaskDispatches == 0 && expiredSupersededDispatches == 0)
            return;

        _logger.LogInformation(
            "Expired {TerminalTaskDispatchCount} historical dispatches for terminal tasks and {SupersededDispatchCount} superseded task-target dispatches during startup backfill",
            expiredTerminalTaskDispatches,
            expiredSupersededDispatches);
    }

    private static async Task BackfillProjectSpaceMetadataAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE projects
            SET kind = 'system', visibility = 'hidden'
            WHERE id = '_global' AND (kind = 'project' OR visibility = 'normal')
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task BackfillAgentStreamDedupAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM agent_stream_entries
            WHERE dedup_key IS NOT NULL
              AND id NOT IN (
                  SELECT MIN(id)
                  FROM agent_stream_entries
                  WHERE dedup_key IS NOT NULL
                  GROUP BY dedup_key
              )
            """;

        var removed = await cmd.ExecuteNonQueryAsync();
        if (removed > 0)
            _logger.LogInformation("Removed {DuplicateAgentStreamCount} duplicate agent stream rows during dedup backfill", removed);
    }

    private static async Task<int> ExpireHistoricalDispatchesForTerminalTasksAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = 'expired'
            WHERE status IN ('pending', 'approved')
              AND task_id IS NOT NULL
              AND EXISTS (
                  SELECT 1
                  FROM tasks
                  WHERE tasks.id = dispatch_entries.task_id
                    AND tasks.status IN ('done', 'cancelled')
              )
            """;
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExpireHistoricalSupersededTaskTargetDispatchesAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE dispatch_entries
            SET status = 'expired'
            WHERE status IN ('pending', 'approved')
              AND task_id IS NOT NULL
              AND EXISTS (
                  SELECT 1
                  FROM dispatch_entries AS newer
                  WHERE newer.project_id = dispatch_entries.project_id
                    AND newer.task_id = dispatch_entries.task_id
                    AND newer.target_agent = dispatch_entries.target_agent
                    AND newer.status IN ('pending', 'approved')
                    AND newer.id > dispatch_entries.id
              )
            """;
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureDocumentsSummaryAndMemoryDocTypeAsync(SqliteConnection connection)
    {
        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'documents'";
        var schema = (string?)await schemaCmd.ExecuteScalarAsync();
        if (schema is not null && schema.Contains("'memory'", StringComparison.Ordinal) && schema.Contains("summary", StringComparison.Ordinal))
            return;

        // Disable foreign keys temporarily; dropping and recreating the content table
        // avoids leaving FK references pointing to an intermediate temporary name.
        await using (var fkOff = connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            await fkOff.ExecuteNonQueryAsync();
        }

        try
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = """
                BEGIN;

                DROP TRIGGER IF EXISTS documents_ai;
                DROP TRIGGER IF EXISTS documents_ad;
                DROP TRIGGER IF EXISTS documents_au;

                CREATE TABLE documents_new (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    slug        TEXT NOT NULL,
                    title       TEXT NOT NULL,
                    content     TEXT NOT NULL,
                    doc_type    TEXT NOT NULL DEFAULT 'spec'
                                CHECK (doc_type IN (
                                    'prd',
                                    'spec',
                                    'adr',
                                    'convention',
                                    'reference',
                                    'note',
                                    'memory'
                                )),
                    tags        TEXT,
                    summary     TEXT,
                    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(project_id, slug)
                );

                INSERT INTO documents_new (id, project_id, slug, title, content, doc_type, tags, created_at, updated_at)
                SELECT id, project_id, slug, title, content, doc_type, tags, created_at, updated_at
                FROM documents;

                DROP TABLE documents;

                ALTER TABLE documents_new RENAME TO documents;

                CREATE INDEX IF NOT EXISTS idx_documents_project_type ON documents(project_id, doc_type);

                CREATE TRIGGER documents_ai AFTER INSERT ON documents BEGIN
                    INSERT INTO documents_fts(rowid, title, content, tags)
                    VALUES (new.id, new.title, new.content, new.tags);
                END;

                CREATE TRIGGER documents_ad AFTER DELETE ON documents BEGIN
                    INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
                    VALUES ('delete', old.id, old.title, old.content, old.tags);
                END;

                CREATE TRIGGER documents_au AFTER UPDATE ON documents BEGIN
                    INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
                    VALUES ('delete', old.id, old.title, old.content, old.tags);
                    INSERT INTO documents_fts(rowid, title, content, tags)
                    VALUES (new.id, new.title, new.content, new.tags);
                END;

                COMMIT;
                """;
            await migrateCmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var fkOn = connection.CreateCommand();
            fkOn.CommandText = "PRAGMA foreign_keys = ON;";
            await fkOn.ExecuteNonQueryAsync();
        }
    }
}
