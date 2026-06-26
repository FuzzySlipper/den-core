# Den Core Postgres Provider Test Harness

Task #3322 added the Npgsql provider path behind configuration. After the #3326
live cutover, Postgres is the live runtime provider. After #3327, repository and
service fixtures use Postgres-backed schemas; SQLite remains only in archived
rollback/import evidence.

## Default Test Run

The default test run builds against the Postgres-only Core source:

```bash
dotnet test tests/DenCore.Tests/DenCore.Tests.csproj -p:NuGetAudit=false
dotnet test tests/DenCore.Service.Tests/DenCore.Service.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName!~McpToolProfileAnnotationTests.AssemblyAnnotations_MatchRegistry"
```

## Postgres Provider Harness

Set `DEN_CORE_TEST_POSTGRES_CONNECTION_STRING` to a disposable/non-production Postgres database. The harness creates a unique temporary schema named `den_core_test_<guid>`, points `DbConnectionFactory` at that schema through the connection search path, and drops the schema after the test.

```bash
DEN_CORE_TEST_POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Database=den_core_test;Username=den;Password=..." \
  dotnet test tests/DenCore.Tests/DenCore.Tests.csproj \
  -p:NuGetAudit=false \
  --filter "Category=PostgresProvider"
```

If the environment variable is not set, the harness test returns without opening a Postgres connection. It never falls back to SQLite after `DatabaseProviderKind.Postgres` is selected.

## Current Boundary

#3323 translated the Phase 0 `den_core` startup schema and #3324 replaced the
document/knowledge FTS paths. Post-cutover work should prefer Postgres-provider
tests for new behavior.
