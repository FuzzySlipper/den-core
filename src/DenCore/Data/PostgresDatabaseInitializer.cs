using System.Data;
using Microsoft.Extensions.Logging;

namespace DenCore.Data;

public sealed class PostgresDatabaseInitializer : IDatabaseInitializer
{
    private const string MigrationTable = "_den_core_schema_migrations";

    private static readonly IReadOnlyList<PostgresMigration> Migrations =
    [
        new(1, "den_core_non_fts_compatibility_schema", InitialSchema),
        new(2, "den_core_postgres_full_text_search", FullTextSearchSchema),
        new(3, "den_core_postgres_utc_timestamp_and_message_jsonb", UtcTimestampAndMessageJsonbSchema)
    ];

    private readonly DbConnectionFactory _db;
    private readonly ILogger<PostgresDatabaseInitializer> _logger;

    public PostgresDatabaseInitializer(DbConnectionFactory db, ILogger<PostgresDatabaseInitializer> logger)
    {
        if (db.Provider != DatabaseProviderKind.Postgres)
            throw new ArgumentException("Postgres initializer requires a Postgres connection factory.", nameof(db));

        _db = db;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await EnsureMigrationTableAsync();

        foreach (var migration in Migrations)
            await ApplyMigrationAsync(migration);

        await EnsureGlobalProjectAsync();

        _logger.LogInformation("Postgres database initialized with {MigrationCount} migration(s)", Migrations.Count);
    }

    private async Task EnsureMigrationTableAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            CREATE TABLE IF NOT EXISTS {{MigrationTable}} (
                version    INTEGER PRIMARY KEY,
                name       TEXT NOT NULL,
                applied_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ApplyMigrationAsync(PostgresMigration migration)
    {
        await SerializableTransactionRetry.ExecuteAsync(async () =>
        {
            await using var conn = await _db.CreateConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable);

            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = tx;
                lockCmd.CommandText = $"LOCK TABLE {MigrationTable} IN EXCLUSIVE MODE";
                await lockCmd.ExecuteNonQueryAsync();
            }

            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandText = $"SELECT 1 FROM {MigrationTable} WHERE version = @version";
                checkCmd.AddParameterWithValue("@version", migration.Version);
                if (await checkCmd.ExecuteScalarAsync() is not null)
                {
                    await tx.CommitAsync();
                    return true;
                }
            }

            await using (var migrationCmd = conn.CreateCommand())
            {
                migrationCmd.Transaction = tx;
                migrationCmd.CommandText = migration.Sql;
                await migrationCmd.ExecuteNonQueryAsync();
            }

            await using (var recordCmd = conn.CreateCommand())
            {
                recordCmd.Transaction = tx;
                recordCmd.CommandText = $"INSERT INTO {MigrationTable} (version, name) VALUES (@version, @name)";
                recordCmd.AddParameterWithValue("@version", migration.Version);
                recordCmd.AddParameterWithValue("@name", migration.Name);
                await recordCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            _logger.LogInformation("Applied Postgres migration {Version}: {Name}", migration.Version, migration.Name);
            return true;
        });
    }

    private async Task EnsureGlobalProjectAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, name, kind, visibility, description)
            VALUES ('_global', 'Global', 'system', 'normal', 'Cross-project shared documents and configuration')
            ON CONFLICT (id) DO NOTHING
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record PostgresMigration(int Version, string Name, string Sql);

    internal const string InitialSchema = """
        ------------------------------------------------------------
        -- Legacy timestamp compatibility quarantine.
        -- Existing runtime SQL is being moved to DbSqlDialect helpers. These
        -- functions keep remaining datetime(...) expressions from leaking
        -- historical legacy timestamp expressions into the Postgres provider
        -- tests during Phase 0C.
        ------------------------------------------------------------
        CREATE OR REPLACE FUNCTION datetime(value text)
        RETURNS text
        LANGUAGE sql
        STABLE
        AS $$
            SELECT CASE
                WHEN lower(value) = 'now' THEN to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')
                ELSE to_char(value::timestamp, 'YYYY-MM-DD HH24:MI:SS')
            END
        $$;

        CREATE OR REPLACE FUNCTION datetime(value text, modifier text)
        RETURNS text
        LANGUAGE sql
        STABLE
        AS $$
            SELECT to_char(
                (
                    CASE
                        WHEN lower(value) = 'now' THEN CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
                        ELSE value::timestamp
                    END + modifier::interval
                ),
                'YYYY-MM-DD HH24:MI:SS')
        $$;

        ------------------------------------------------------------
        -- PROJECTS / TASKS / MESSAGES
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
            created_at    TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at    TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE TABLE IF NOT EXISTS tasks (
            id          INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            parent_id   INTEGER REFERENCES tasks(id) ON DELETE CASCADE,
            title       TEXT NOT NULL,
            description TEXT,
            status      TEXT NOT NULL DEFAULT 'planned'
                        CHECK (status IN ('planned', 'in_progress', 'review', 'blocked', 'done', 'cancelled')),
            priority    INTEGER NOT NULL DEFAULT 3 CHECK (priority BETWEEN 1 AND 5),
            assigned_to TEXT,
            tags        JSONB,
            created_at  TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at  TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_tasks_project_status ON tasks(project_id, status);
        CREATE INDEX IF NOT EXISTS idx_tasks_assigned ON tasks(assigned_to);
        CREATE INDEX IF NOT EXISTS idx_tasks_parent ON tasks(parent_id);

        CREATE TABLE IF NOT EXISTS task_dependencies (
            task_id    INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            depends_on INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            PRIMARY KEY (task_id, depends_on),
            CHECK (task_id != depends_on)
        );

        CREATE TABLE IF NOT EXISTS task_history (
            id         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            task_id    INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            field      TEXT NOT NULL,
            old_value  TEXT,
            new_value  TEXT,
            changed_by TEXT,
            changed_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_task_history_task ON task_history(task_id);

        CREATE TABLE IF NOT EXISTS messages (
            id         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id    INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            thread_id  INTEGER REFERENCES messages(id) ON DELETE SET NULL,
            sender     TEXT NOT NULL,
            content    TEXT NOT NULL,
            intent     TEXT NOT NULL DEFAULT 'general'
                       CHECK (intent IN (
                           'general', 'note', 'status_update', 'question', 'answer',
                           'handoff', 'review_request', 'review_feedback', 'review_approval',
                           'task_ready', 'task_blocked', 'notification'
                       )),
            metadata   JSONB,
            created_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_messages_project_task ON messages(project_id, task_id);
        CREATE INDEX IF NOT EXISTS idx_messages_thread ON messages(thread_id);
        CREATE INDEX IF NOT EXISTS idx_messages_project_intent ON messages(project_id, intent);

        CREATE TABLE IF NOT EXISTS message_reads (
            message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            agent      TEXT NOT NULL,
            read_at    TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            PRIMARY KEY (message_id, agent)
        );

        ------------------------------------------------------------
        -- DOCUMENTS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS documents (
            id         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            slug       TEXT NOT NULL,
            title      TEXT NOT NULL,
            content    TEXT NOT NULL,
            doc_type   TEXT NOT NULL DEFAULT 'spec'
                       CHECK (doc_type IN ('prd', 'spec', 'adr', 'convention', 'reference', 'note', 'memory')),
            visibility TEXT NOT NULL DEFAULT 'normal'
                       CHECK (visibility IN ('normal', 'hidden', 'archived')),
            tags       JSONB,
            summary    TEXT,
            created_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            UNIQUE (project_id, slug)
        );

        CREATE INDEX IF NOT EXISTS idx_documents_project_type ON documents(project_id, doc_type);

        -- Plain shadow table only. Full Postgres text search is task #3324.
        CREATE TABLE IF NOT EXISTS documents_fts (
            rowid   INTEGER PRIMARY KEY,
            title   TEXT,
            content TEXT,
            summary TEXT
        );

        ------------------------------------------------------------
        -- REVIEW TABLES USED BY TASK DETAIL PROJECTIONS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS review_rounds (
            id                         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            task_id                    INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            round_number               INTEGER NOT NULL,
            requested_by               TEXT NOT NULL,
            branch                     TEXT NOT NULL,
            base_branch                TEXT,
            base_commit                TEXT,
            head_commit                TEXT,
            last_reviewed_head_commit  TEXT,
            commits_since_last_review  INTEGER,
            tests_run                  TEXT,
            notes                      TEXT,
            preferred_diff_base_ref    TEXT,
            preferred_diff_base_commit TEXT,
            preferred_diff_head_ref    TEXT,
            preferred_diff_head_commit TEXT,
            alternate_diff_base_ref    TEXT,
            alternate_diff_base_commit TEXT,
            alternate_diff_head_ref    TEXT,
            alternate_diff_head_commit TEXT,
            delta_base_commit          TEXT,
            inherited_commit_count     INTEGER,
            task_local_commit_count    INTEGER,
            verdict                    TEXT,
            verdict_by                 TEXT,
            verdict_notes              TEXT,
            requested_at               TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            verdict_at                 TEXT,
            UNIQUE (task_id, round_number)
        );

        CREATE INDEX IF NOT EXISTS idx_review_rounds_task ON review_rounds(task_id, round_number);

        CREATE TABLE IF NOT EXISTS review_findings (
            id                INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            finding_key       TEXT NOT NULL UNIQUE,
            task_id           INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            review_round_id   INTEGER NOT NULL REFERENCES review_rounds(id) ON DELETE CASCADE,
            finding_number    INTEGER NOT NULL,
            created_by        TEXT NOT NULL,
            category          TEXT NOT NULL,
            summary           TEXT NOT NULL,
            notes             TEXT,
            file_references   TEXT,
            test_commands     TEXT,
            status            TEXT NOT NULL DEFAULT 'open',
            status_updated_by TEXT,
            status_notes      TEXT,
            status_updated_at TEXT,
            response_by       TEXT,
            response_notes    TEXT,
            response_at       TEXT,
            follow_up_task_id INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            created_at        TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at        TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_review_findings_task_status ON review_findings(task_id, status);
        CREATE INDEX IF NOT EXISTS idx_review_findings_round ON review_findings(review_round_id, finding_number);

        ------------------------------------------------------------
        -- AGENT GUIDANCE METADATA REFERENCED BY DOCUMENT PREFLIGHT
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS agent_guidance_entries (
            id                  INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id          TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            document_project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            document_slug       TEXT NOT NULL,
            importance          TEXT NOT NULL DEFAULT 'important'
                                CHECK (importance IN ('required', 'important')),
            audience            TEXT,
            sort_order          INTEGER NOT NULL DEFAULT 0,
            notes               TEXT,
            created_at          TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at          TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            UNIQUE (project_id, document_project_id, document_slug, audience)
        );

        CREATE INDEX IF NOT EXISTS idx_agent_guidance_scope_order
            ON agent_guidance_entries(project_id, importance, sort_order, id);
        CREATE INDEX IF NOT EXISTS idx_agent_guidance_document
            ON agent_guidance_entries(document_project_id, document_slug);

        ------------------------------------------------------------
        -- WORKER / ORCHESTRATOR
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS worker_pool_members (
            worker_identity      TEXT PRIMARY KEY,
            profile_identity     TEXT NOT NULL DEFAULT '',
            worker_role          TEXT,
            display_name         TEXT,
            capabilities         TEXT,
            status               TEXT NOT NULL DEFAULT 'available'
                                 CHECK (status IN ('available', 'busy', 'quarantined', 'offboarded')),
            last_heartbeat       TEXT,
            agent_instance_id    TEXT,
            channel_id           TEXT,
            session_id           TEXT,
            adapter_instance_id  TEXT,
            log_pointer          TEXT,
            stale_after_seconds  INTEGER,
            metadata             TEXT,
            created_at           TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at           TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_worker_pool_members_status
            ON worker_pool_members(status, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_worker_pool_members_profile
            ON worker_pool_members(profile_identity, status, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_worker_pool_members_role
            ON worker_pool_members(worker_role, status, updated_at DESC) WHERE worker_role IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_worker_pool_members_stale
            ON worker_pool_members(stale_after_seconds, last_heartbeat, status) WHERE stale_after_seconds IS NOT NULL;

        CREATE TABLE IF NOT EXISTS worker_assignments (
            id                  INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            worker_identity     TEXT NOT NULL REFERENCES worker_pool_members(worker_identity),
            run_id              TEXT NOT NULL,
            project_id          TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id             INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            role                TEXT NOT NULL,
            assigned_by         TEXT NOT NULL,
            state               TEXT NOT NULL DEFAULT 'ack'
                                CHECK (state IN ('ack', 'running', 'checkpoint_waiting', 'blocked', 'completed', 'failed', 'expired')),
            lease_id            TEXT,
            profile_identity    TEXT NOT NULL DEFAULT '',
            latest_checkpoint_id INTEGER,
            cleanup_evidence    TEXT,
            cleanup_recorded_at TEXT,
            acquired_at         TEXT,
            released_at         TEXT,
            created_at          TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at          TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_worker_assignments_worker_state
            ON worker_assignments(worker_identity, state, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_worker_assignments_project_state
            ON worker_assignments(project_id, state, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_worker_assignments_task_state
            ON worker_assignments(task_id, state, updated_at DESC) WHERE task_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_worker_assignments_state_updated
            ON worker_assignments(state, updated_at DESC);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_worker_assignments_lease_id_unique
            ON worker_assignments(lease_id) WHERE lease_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS worker_checkpoints (
            id              INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            assignment_id   INTEGER NOT NULL REFERENCES worker_assignments(id),
            run_id          TEXT NOT NULL,
            checkpoint_type TEXT NOT NULL
                            CHECK (checkpoint_type IN ('checkpoint', 'progress', 'completion', 'failure', 'state_snapshot')),
            payload         TEXT NOT NULL,
            created_at      TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_worker_checkpoints_assignment
            ON worker_checkpoints(assignment_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_worker_checkpoints_run
            ON worker_checkpoints(run_id, created_at DESC, id DESC);

        CREATE TABLE IF NOT EXISTS checkpoint_responses (
            id            INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            checkpoint_id INTEGER NOT NULL REFERENCES worker_checkpoints(id),
            assignment_id INTEGER REFERENCES worker_assignments(id),
            run_id        TEXT NOT NULL,
            response_type TEXT NOT NULL
                          CHECK (response_type IN ('ack', 'guidance', 'redirect', 'abort', 'checkpoint_request')),
            payload       TEXT NOT NULL,
            created_at    TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_checkpoint_responses_checkpoint
            ON checkpoint_responses(checkpoint_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_checkpoint_responses_run
            ON checkpoint_responses(run_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_checkpoint_responses_assignment
            ON checkpoint_responses(assignment_id, created_at DESC, id DESC) WHERE assignment_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS worker_no_capacity_requests (
            id                        INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id                TEXT NOT NULL,
            task_id                   INTEGER,
            role                      TEXT NOT NULL,
            assigned_by               TEXT NOT NULL,
            run_id                    TEXT NOT NULL,
            profile_identity          TEXT,
            worker_role               TEXT,
            required_capabilities     TEXT,
            preferred_worker_identity TEXT,
            reason_code               TEXT NOT NULL
                                      CHECK (reason_code IN (
                                          'no_matching_worker', 'all_busy', 'all_quarantined_or_offline',
                                          'ambiguous', 'preferred_not_found_or_busy', 'hard_selector_mismatch'
                                      )),
            candidate_details         TEXT NOT NULL DEFAULT '{}',
            diagnostic_message        TEXT,
            created_at                TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_no_capacity_project
            ON worker_no_capacity_requests(project_id, created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_no_capacity_run
            ON worker_no_capacity_requests(run_id, created_at DESC);

        CREATE TABLE IF NOT EXISTS worker_pool_lanes (
            profile_identity TEXT NOT NULL,
            worker_role      TEXT NOT NULL,
            capacity         INTEGER NOT NULL DEFAULT 4 CHECK (capacity > 0),
            status           TEXT NOT NULL DEFAULT 'active'
                             CHECK (status IN ('active', 'quarantined', 'disabled')),
            metadata         TEXT,
            created_at       TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at       TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            PRIMARY KEY (profile_identity, worker_role)
        );

        CREATE INDEX IF NOT EXISTS idx_worker_pool_lanes_status
            ON worker_pool_lanes(status, updated_at DESC);

        CREATE TABLE IF NOT EXISTS orchestrator_leases (
            id                         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            lease_id                   TEXT NOT NULL UNIQUE,
            lease_kind                 TEXT NOT NULL DEFAULT 'project_orchestrator'
                                       CHECK (lease_kind IN ('task_worker', 'project_orchestrator')),
            scope_type                 TEXT NOT NULL DEFAULT 'project'
                                       CHECK (scope_type IN ('project', 'channel', 'task', 'workstream')),
            project_id                 TEXT REFERENCES projects(id) ON DELETE SET NULL,
            channel_id                 TEXT,
            task_id                    INTEGER,
            workstream_handle          TEXT,
            objective                  TEXT,
            lease_owner                TEXT NOT NULL,
            orchestrator_identity      TEXT NOT NULL REFERENCES worker_pool_members(worker_identity),
            profile_identity           TEXT NOT NULL DEFAULT '',
            display_name               TEXT,
            capability_metadata        TEXT,
            state                      TEXT NOT NULL DEFAULT 'leased'
                                       CHECK (state IN (
                                           'proposed', 'leased', 'active', 'checkpoint_waiting',
                                           'draining', 'released', 'quarantined', 'expired', 'degraded'
                                       )),
            requested_duration_seconds INTEGER,
            actual_duration_seconds    INTEGER,
            lease_expires_at           TEXT,
            renewal_policy             TEXT NOT NULL DEFAULT 'deny'
                                       CHECK (renewal_policy IN ('allow', 'deny', 'auto')),
            drain_policy               TEXT NOT NULL DEFAULT 'graceful'
                                       CHECK (drain_policy IN ('graceful', 'immediate')),
            agent_instance_id          TEXT,
            adapter_instance_id        TEXT,
            session_id                 TEXT,
            run_id                     TEXT,
            last_seen_at               TEXT,
            latest_checkpoint_id       INTEGER,
            cleanup_evidence           TEXT,
            cleanup_recorded_at        TEXT,
            metadata                   TEXT,
            created_at                 TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at                 TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_orchestrator_leases_project
            ON orchestrator_leases(project_id, state, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_orchestrator_leases_orchestrator
            ON orchestrator_leases(orchestrator_identity, state, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_orchestrator_leases_state
            ON orchestrator_leases(state, updated_at DESC);
        CREATE INDEX IF NOT EXISTS idx_orchestrator_leases_expires
            ON orchestrator_leases(lease_expires_at, state) WHERE lease_expires_at IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_orchestrator_leases_lease_kind
            ON orchestrator_leases(lease_kind, project_id, state);

        ------------------------------------------------------------
        -- USAGE COST
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS pricing_snapshots (
            id               INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            snapshot_label   TEXT NOT NULL,
            snapshot_version TEXT NOT NULL,
            effective_at     TEXT,
            entries_json     TEXT NOT NULL,
            created_by       TEXT,
            notes            TEXT,
            created_at       TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_pricing_snapshots_version
            ON pricing_snapshots(snapshot_version, id DESC);

        CREATE TABLE IF NOT EXISTS usage_events (
            id                           INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            occurred_at                  TEXT NOT NULL,
            project_id                   TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id                      INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            assignment_id                INTEGER,
            run_id                       TEXT,
            session_id                   TEXT,
            agent_identity               TEXT,
            profile_identity             TEXT,
            worker_role                  TEXT,
            worker_identity              TEXT,
            operation_kind               TEXT NOT NULL,
            provider                     TEXT NOT NULL,
            model                        TEXT NOT NULL,
            model_alias                  TEXT,
            resolved_model               TEXT,
            endpoint_kind                TEXT,
            input_tokens                 INTEGER,
            output_tokens                INTEGER,
            cache_read_tokens            INTEGER,
            cache_write_tokens           INTEGER,
            reasoning_tokens             INTEGER,
            tool_result_tokens           INTEGER,
            request_count                INTEGER NOT NULL DEFAULT 1,
            retry_count                  INTEGER NOT NULL DEFAULT 0,
            streaming                    INTEGER NOT NULL DEFAULT 0,
            error_kind                   TEXT,
            pricing_snapshot_id          INTEGER REFERENCES pricing_snapshots(id) ON DELETE SET NULL,
            approximate_cost_micro_cents BIGINT,
            provenance                   TEXT,
            adapter_version              TEXT,
            raw_usage_source             TEXT,
            request_id_hint              TEXT,
            created_at                   TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_usage_events_project_occurred
            ON usage_events(project_id, occurred_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_usage_events_task_occurred
            ON usage_events(task_id, occurred_at DESC, id DESC) WHERE task_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_usage_events_role_occurred
            ON usage_events(worker_role, occurred_at DESC, id DESC) WHERE worker_role IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_usage_events_provider_model
            ON usage_events(provider, model, occurred_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_usage_events_run
            ON usage_events(run_id, occurred_at DESC, id DESC) WHERE run_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_usage_events_pricing_snapshot
            ON usage_events(pricing_snapshot_id) WHERE pricing_snapshot_id IS NOT NULL;

        ------------------------------------------------------------
        -- Minimal compatibility tables referenced by dependent-count and
        -- startup-safe route projections. Full domain extraction remains
        -- owned by later den-services waves.
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS channels (
            id            INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            slug          TEXT NOT NULL UNIQUE,
            display_name  TEXT NOT NULL,
            kind          TEXT NOT NULL DEFAULT 'project',
            project_id    TEXT REFERENCES projects(id) ON DELETE CASCADE,
            created_by    TEXT,
            settings_json TEXT,
            created_at    TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at    TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE TABLE IF NOT EXISTS agent_sessions (
            agent_identity TEXT PRIMARY KEY,
            project_id     TEXT REFERENCES projects(id) ON DELETE SET NULL,
            session_id     TEXT,
            status         TEXT NOT NULL DEFAULT 'active',
            checked_in_at  TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            last_heartbeat TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            metadata       TEXT
        );

        CREATE TABLE IF NOT EXISTS agent_instance_bindings (
            instance_id    TEXT PRIMARY KEY,
            project_id     TEXT REFERENCES projects(id) ON DELETE SET NULL,
            agent_identity TEXT NOT NULL,
            agent_family   TEXT NOT NULL,
            role           TEXT,
            transport_kind TEXT NOT NULL,
            session_id     TEXT,
            status         TEXT NOT NULL DEFAULT 'active'
                           CHECK (status IN ('active', 'inactive', 'degraded')),
            metadata       TEXT,
            checked_in_at  TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            last_heartbeat TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
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

        CREATE TABLE IF NOT EXISTS dispatch_entries (
            id             INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id     TEXT REFERENCES projects(id) ON DELETE SET NULL,
            target_agent   TEXT NOT NULL,
            status         TEXT NOT NULL DEFAULT 'pending'
                           CHECK (status IN ('pending', 'approved', 'rejected', 'completed', 'expired')),
            trigger_type   TEXT NOT NULL
                           CHECK (trigger_type IN ('message', 'task_status')),
            trigger_id     INTEGER NOT NULL,
            task_id        INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            summary        TEXT,
            context_prompt TEXT,
            context_json   TEXT,
            dedup_key      TEXT NOT NULL,
            created_at     TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            expires_at     TEXT NOT NULL,
            decided_at     TEXT,
            completed_at   TEXT,
            decided_by     TEXT,
            completed_by   TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_dispatch_status
            ON dispatch_entries(status);
        CREATE INDEX IF NOT EXISTS idx_dispatch_project_status
            ON dispatch_entries(project_id, status);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_dispatch_dedup
            ON dispatch_entries(dedup_key) WHERE status = 'pending';

        CREATE TABLE IF NOT EXISTS agent_stream_entries (
            id                    INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            stream_kind           TEXT NOT NULL
                                  CHECK (stream_kind IN ('ops', 'message')),
            event_type            TEXT NOT NULL,
            project_id            TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            thread_id             INTEGER REFERENCES messages(id) ON DELETE SET NULL,
            dispatch_id           INTEGER REFERENCES dispatch_entries(id) ON DELETE SET NULL,
            sender                TEXT NOT NULL,
            sender_instance_id    TEXT,
            recipient_agent       TEXT,
            recipient_role        TEXT,
            recipient_instance_id TEXT,
            delivery_mode         TEXT NOT NULL
                                  CHECK (delivery_mode IN ('record_only', 'notify', 'wake')),
            body                  TEXT,
            metadata              TEXT,
            dedup_key             TEXT,
            created_at            TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
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
        CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_stream_dedup
            ON agent_stream_entries(dedup_key) WHERE dedup_key IS NOT NULL;

        CREATE TABLE IF NOT EXISTS agent_runs (
            id                     INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            run_id                 TEXT NOT NULL UNIQUE,
            project_id             TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id                INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            state                  TEXT NOT NULL DEFAULT 'running',
            latest_stream_entry_id INTEGER,
            created_at             TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at             TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE TABLE IF NOT EXISTS agent_workspaces (
            id             INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id     TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id        INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            created_by_run TEXT,
            state          TEXT NOT NULL DEFAULT 'active',
            metadata       TEXT,
            created_at     TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at     TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE TABLE IF NOT EXISTS desktop_git_snapshots (
            id         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id    INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            observed_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            payload    TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS desktop_diff_snapshots (
            id         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
            observed_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            payload    TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS desktop_session_events (
            id                 INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id         TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id            INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            source_instance_id TEXT,
            session_id         TEXT,
            event_type         TEXT,
            payload            TEXT NOT NULL DEFAULT '{}',
            created_at         TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE TABLE IF NOT EXISTS collaboration_sessions (
            id         INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
            task_id    INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            message_id INTEGER REFERENCES messages(id) ON DELETE SET NULL,
            state      TEXT NOT NULL DEFAULT 'active',
            metadata   TEXT,
            created_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );
        """;

    internal const string FullTextSearchSchema = """
        ------------------------------------------------------------
        -- POSTGRES FULL-TEXT SEARCH
        ------------------------------------------------------------
        DROP TABLE IF EXISTS documents_fts;
        DROP TABLE IF EXISTS knowledge_entries_fts;

        CREATE INDEX IF NOT EXISTS idx_documents_search_gin
            ON documents USING GIN ((
                setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
                setweight(to_tsvector('english', coalesce(summary, '')), 'B') ||
                setweight(to_tsvector('english', coalesce(content, '')), 'C') ||
                setweight(to_tsvector('english', coalesce(tags::text, '')), 'D')
            ));

        ------------------------------------------------------------
        -- KNOWLEDGE LIBRARY
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS knowledge_entries (
            id                 INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            slug               TEXT NOT NULL UNIQUE,
            title              TEXT NOT NULL,
            summary            TEXT,
            body_markdown      TEXT NOT NULL,
            kind               TEXT NOT NULL DEFAULT 'reference'
                               CHECK (kind IN (
                                   'concept', 'reference', 'glossary', 'convention',
                                   'service_map', 'tool_notes', 'gotcha',
                                   'architecture_note', 'migration_note'
                               )),
            status             TEXT NOT NULL DEFAULT 'draft'
                               CHECK (status IN ('draft', 'reviewed', 'needs_review', 'deprecated', 'archived')),
            curation_state     TEXT NOT NULL DEFAULT 'unreviewed_import'
                               CHECK (curation_state IN ('unreviewed_import', 'human_curated', 'agent_curated', 'needs_recheck')),
            audience_json      TEXT,
            aliases_json       TEXT,
            source_refs_json   TEXT,
            accuracy_notes     TEXT,
            replacement_slug   TEXT REFERENCES knowledge_entries(slug) ON DELETE SET NULL,
            last_reviewed_at   TEXT,
            review_due_at      TEXT,
            created_by         TEXT,
            updated_by         TEXT,
            created_at         TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            updated_at         TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
        );

        CREATE INDEX IF NOT EXISTS idx_knowledge_entries_status_kind
            ON knowledge_entries(status, kind, updated_at DESC);

        CREATE INDEX IF NOT EXISTS idx_knowledge_entries_review_due
            ON knowledge_entries(review_due_at)
            WHERE review_due_at IS NOT NULL AND status IN ('reviewed', 'needs_review');

        CREATE INDEX IF NOT EXISTS idx_knowledge_entries_search_gin
            ON knowledge_entries USING GIN ((
                setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
                setweight(to_tsvector('english', coalesce(summary, '')), 'B') ||
                setweight(to_tsvector('english', coalesce(body_markdown, '')), 'C') ||
                setweight(to_tsvector('english', coalesce(slug, '')), 'D')
            ));

        CREATE TABLE IF NOT EXISTS knowledge_entry_tags (
            entry_id INTEGER NOT NULL REFERENCES knowledge_entries(id) ON DELETE CASCADE,
            tag      TEXT NOT NULL,
            PRIMARY KEY (entry_id, tag)
        );

        CREATE INDEX IF NOT EXISTS idx_knowledge_entry_tags_tag
            ON knowledge_entry_tags(tag, entry_id);

        CREATE TABLE IF NOT EXISTS knowledge_entry_revisions (
            id               INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            entry_id         INTEGER NOT NULL REFERENCES knowledge_entries(id) ON DELETE CASCADE,
            revision_number  INTEGER NOT NULL,
            title            TEXT NOT NULL,
            summary          TEXT,
            body_markdown    TEXT NOT NULL,
            kind             TEXT NOT NULL,
            status           TEXT NOT NULL,
            curation_state   TEXT NOT NULL,
            tags_json        TEXT,
            audience_json    TEXT,
            aliases_json     TEXT,
            source_refs_json TEXT,
            accuracy_notes   TEXT,
            replacement_slug TEXT,
            changed_by       TEXT,
            change_note      TEXT,
            created_at       TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            UNIQUE(entry_id, revision_number)
        );

        CREATE TABLE IF NOT EXISTS knowledge_entry_links (
            id              INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            from_entry_id   INTEGER NOT NULL REFERENCES knowledge_entries(id) ON DELETE CASCADE,
            to_entry_slug   TEXT NOT NULL,
            link_kind       TEXT NOT NULL DEFAULT 'related'
                            CHECK (link_kind IN ('related', 'supersedes', 'superseded_by', 'see_also', 'depends_on')),
            description     TEXT,
            created_at      TEXT NOT NULL DEFAULT (to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')),
            UNIQUE(from_entry_id, to_entry_slug, link_kind)
        );
        """;

    internal const string UtcTimestampAndMessageJsonbSchema = """
        ------------------------------------------------------------
        -- UTC timestamp text contract and message JSONB compatibility.
        ------------------------------------------------------------
        CREATE OR REPLACE FUNCTION datetime(value text)
        RETURNS text
        LANGUAGE sql
        STABLE
        AS $$
            SELECT CASE
                WHEN lower(value) = 'now' THEN to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')
                ELSE to_char(value::timestamp, 'YYYY-MM-DD HH24:MI:SS')
            END
        $$;

        CREATE OR REPLACE FUNCTION datetime(value text, modifier text)
        RETURNS text
        LANGUAGE sql
        STABLE
        AS $$
            SELECT to_char(
                (
                    CASE
                        WHEN lower(value) = 'now' THEN CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
                        ELSE value::timestamp
                    END + modifier::interval
                ),
                'YYYY-MM-DD HH24:MI:SS')
        $$;

        ALTER TABLE messages
            ALTER COLUMN metadata TYPE JSONB USING metadata::jsonb;

        ALTER TABLE tasks
            ALTER COLUMN tags TYPE JSONB USING tags::jsonb;

        DO $$
        DECLARE
            column_record record;
        BEGIN
            FOR column_record IN
                SELECT table_name, column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND data_type = 'text'
                  AND column_default IS NOT NULL
                  AND (
                      column_name ~ '(^|_)(created|updated|applied|changed|read|requested|observed|received|checked_in|last_accessed|decided)_at$'
                      OR column_name = 'last_heartbeat'
                  )
            LOOP
                EXECUTE format(
                    'ALTER TABLE %I ALTER COLUMN %I SET DEFAULT to_char(CURRENT_TIMESTAMP AT TIME ZONE ''UTC'', ''YYYY-MM-DD HH24:MI:SS'')',
                    column_record.table_name,
                    column_record.column_name);
            END LOOP;
        END
        $$;
        """;
}
