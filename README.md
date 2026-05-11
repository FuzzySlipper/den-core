# den-core

Den Core is the canonical Den API and state service extracted from `den-mcp`.

This first skeleton intentionally keeps low-churn `DenMcp.*` namespaces and project names while establishing the service boundary. Renaming can happen after the split is green.

## Owns

- SQLite database path/config, schema initialization, repositories, and domain services.
- REST APIs for projects, spaces, topics, tasks, messages, documents, reviews, agent stream, worker/session records, librarian/search, guidance, and related Den state.
- Static Den web/admin assets for the Core API surface.
- `/health` reporting version/commit and Core service status.

## Does not own

- MCP transport or `/mcp` endpoint.
- MCP tool classes, tool descriptions, or agent-facing compatibility formatting.
- Den Desktop sidecar/UI runtime.

## Build and test

```bash
dotnet restore den-core.slnx
dotnet build den-core.slnx --no-restore
TMPDIR=/tmp/den-core-runner-tmp dotnet test den-core.slnx --no-build
```

The dashboard asset smoke test requires built frontend assets. Build them with:

```bash
npm --prefix src/DenMcp.Server/ClientApp ci
npm --prefix src/DenMcp.Server/ClientApp run build
```

## Run

```bash
dotnet run --project src/DenMcp.Server -- --port 5199 --db-path /tmp/den-core/dev.db
```

Default server URL remains `http://localhost:5199` for compatibility during extraction. Production cutover should assign a dedicated Den Core URL/port and point the slim `den-mcp` adapter at it through `DenCore:BaseUrl`.
