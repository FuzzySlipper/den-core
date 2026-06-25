# Den Core SQL Dialect Deferred Runtime SQL

Task #3321 introduced provider-neutral SQL dialect helpers and database exception translation while keeping SQLite as the only active runtime provider. This note records the raw SQL forms that remain intentionally deferred instead of being swept inside #3321.

## Deferred To #3323

- `datetime('now')` remains in runtime repository updates and SQLite schema defaults. `DbSqlDialect.CurrentTimestamp` exists for new/converted code, but a repo-wide timestamp rewrite has a larger behavioral surface and belongs with the full SQL hazard translation task.
- `sqlite_master` remains in `DatabaseInitializer` schema inspection and migration probes. The initializer is still SQLite-specific until the Postgres schema path is introduced.
- SQLite migration SQL in `DatabaseInitializer`, including `INSERT OR IGNORE` and `INSERT OR REPLACE`, stays SQLite-specific while SQLite bootstrapping remains the only active initializer path.

## Deferred To #3324

- SQLite FTS command text remains available through `DbSqlDialect.KnowledgeFtsUpsertCommandText` for the current runtime. The Postgres branch is only a transitional placeholder until the dedicated FTS replacement task lands.

## Converted In #3321

- Runtime route/repository constraint handling now uses `DbExceptionTranslator` instead of checking `SqliteErrorCode == 19`.
- Runtime JSON scalar and JSON-array filters touched in #3321 now go through `DbSqlDialect.JsonText` or `DbSqlDialect.JsonArrayContains`.
- Runtime `last_insert_rowid()` reads touched in #3321 now go through SQLite-only `DbSqlDialect.LastInsertedIdSelect`; Postgres identity reads must use `DbSqlDialect.ReturningIdClause`.
- Runtime insert-ignore patterns touched in #3321 now go through `DbSqlDialect.InsertIgnoreInto` plus `OnConflictDoNothing`.
