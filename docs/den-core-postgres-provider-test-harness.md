# Den Core Postgres Provider Test Harness

Task #3322 adds the Npgsql provider path behind configuration while keeping SQLite as the default runtime provider.

## Default SQLite Test Run

The normal test path still uses SQLite:

```bash
dotnet test tests/DenCore.Tests/DenCore.Tests.csproj -p:NuGetAudit=false
dotnet test tests/DenCore.Service.Tests/DenCore.Service.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName!~McpToolProfileAnnotationTests.AssemblyAnnotations_MatchRegistry"
```

## Optional Postgres Provider Harness

Set `DEN_CORE_TEST_POSTGRES_CONNECTION_STRING` to a disposable/non-production Postgres database. The harness creates a unique temporary schema named `den_core_test_<guid>`, points `DbConnectionFactory` at that schema through the connection search path, and drops the schema after the test.

```bash
DEN_CORE_TEST_POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Database=den_core_test;Username=den;Password=..." \
  dotnet test tests/DenCore.Tests/DenCore.Tests.csproj \
  -p:NuGetAudit=false \
  --filter "Category=PostgresProvider"
```

If the environment variable is not set, the harness test returns without opening a Postgres connection. It never falls back to SQLite after `DatabaseProviderKind.Postgres` is selected.

## Current Boundary

#3322 does not translate or apply the full `den_core` schema. #3323 owns Postgres migrations and broader SQL hazard translation. Until those migrations land, Postgres-provider tests should stay focused on provider/configuration behavior and disposable schema lifecycle.
