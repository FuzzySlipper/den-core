# den-core Postgres migrations (#3323)

Task #3323 adds the first den-core Postgres initializer path for Phase 0C.
This is a compatibility migration for the current `den_core` service shape; it
does not move ownership into den-services schemas and it does not implement the
Postgres FTS replacement owned by #3324.

## Startup path

When `DenCore:Provider=Postgres`, `Program.cs` creates a
`PostgresDatabaseInitializer`. The
Postgres initializer:

- opens only the configured `DenCore:ConnectionString`;
- creates `_den_core_schema_migrations`;
- applies versioned migrations under a serializable transaction with the shared
  retry helper;
- seeds the `_global` project idempotently.

The SQLite initializer is retained only for legacy tests and rollback
archaeology after the #3326 cutover. Production validation now fails closed on
any non-Postgres provider.

## Phase 0C schema coverage

Migration `1 / den_core_non_fts_compatibility_schema` creates the tables needed
for representative non-FTS repository and route coverage:

- projects, tasks, task dependencies, task history;
- messages and message_reads;
- documents plus a plain `documents_fts` shadow table only to keep write paths
  separated from the #3324 FTS replacement;
- review rounds/findings used by task detail projections;
- agent guidance metadata needed by document archive preflight;
- worker pool, assignments, checkpoints, no-capacity diagnostics, lanes, and
  orchestrator leases;
- usage events and pricing snapshots;
- minimal compatibility tables referenced by dependent-count and startup-safe
  projections.

## SQL hazard handling

Usage-cost inserts now use `INSERT ... RETURNING id` for both SQLite and
Postgres instead of SQLite-only `last_insert_rowid()`.

Most remaining `datetime('now')` occurrences are still visible in runtime source.
For this phase they are quarantined by the Postgres migration through a small
`datetime(text)` / `datetime(text, text)` compatibility function. That keeps the
Postgres startup and representative repository tests working without widening
this task into a broad repository rewrite. Follow-up cleanup should continue
moving call sites to `DbSqlDialect.CurrentTimestamp` and `DbSqlDialect.AddSeconds`.

## Verification

The Postgres provider harness is still explicitly opt-in:

```bash
DEN_CORE_TEST_POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Database=den_core_test;Username=den;Password=..." \
  dotnet test tests/DenCore.Tests/DenCore.Tests.csproj \
  -p:NuGetAudit=false \
  --filter "Category=PostgresProvider"
```

When the environment variable is unset, provider tests return without opening a
connection.
