# Den Core SQL Dialect Deferred Runtime SQL

Task #3321 introduced provider-neutral SQL dialect helpers and database
exception translation while SQLite was still the only active runtime provider.
After the #3326 cutover, Postgres is live; after #3327, the source no longer
ships SQLite provider/package support. This note is historical evidence for what
#3323/#3324 finished or quarantined before final removal.

## Deferred To #3323

- `datetime('now')` compatibility was quarantined during Phase 0C and should be
  treated as historical migration context, not a live provider contract.
- `sqlite_master` and SQLite table-rebuild migrations belonged to the retired
  initializer and were removed with #3327.

## Deferred To #3324

- #3324 replaced the live Postgres document/knowledge search path.

## Converted In #3321

- Runtime route/repository constraint handling now uses `DbExceptionTranslator`
  and Postgres SQLSTATE mapping.
- Runtime JSON scalar and JSON-array filters touched in #3321 now go through `DbSqlDialect.JsonText` or `DbSqlDialect.JsonArrayContains`.
- Retired `last_insert_rowid()` reads were replaced by Postgres `RETURNING`
  paths.
- Runtime insert-ignore patterns touched in #3321 now go through `DbSqlDialect.InsertIgnoreInto` plus `OnConflictDoNothing`.
