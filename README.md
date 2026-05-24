# den-core

Den Core is the canonical Den API and state service extracted from `den-mcp`.

This first skeleton intentionally keeps low-churn `DenMcp.*` namespaces and project names while establishing the service boundary. Renaming can happen after the split is green.

## Den system map

This repo is the best starting point for understanding the Den system because Core owns the canonical state and contracts that the other Den services orbit around. The service split is useful, but it can make tasks and docs feel scattered; use this map as the first-pass filing/navigation guide.

For the detailed as-built service/port/runtime inventory, see the Den document `den-core/current-den-service-architecture-2026-05`.

| Repo / Den project | Primary role | File work here when... | Do not put here |
|---|---|---|---|
| `den-core` | Canonical Den API, DB, workflow records, project/space/task/doc/message/review/guidance/librarian state, Core REST contracts. | The work changes canonical Den state, API contracts, schema/migrations, task/review/message/document behavior, Spaces/Projects, guidance/librarian/search, or worker-run records. | Channel UI/product behavior, Hermes-specific runtime glue, Desktop-only UX, MCP adapter compatibility formatting. |
| `den-mcp` | Thin MCP-facing compatibility facade over Den Core APIs. Stable agent MCP endpoint and tool-adapter behavior. | The work changes MCP tool exposure, compatibility formatting, facade health/proxy behavior, or agent-facing MCP adapter quirks. | New canonical DB/state ownership, browser UI, Channels/Gateway/Desktop/Hermes product logic. |
| `den-channels` | Channel/chat/activity service and current Den Web/browser UI surface. Channels, channel messages, memberships, reactions, read cursors, channel activity, browser-safe Core proxy. | The work changes channel semantics, channel UI, memberships/wake policy inputs, reactions, activity rendering, or Den Web behavior. | Canonical task/doc/review state, MCP tool adapter logic, Hermes profile internals. |
| `den-gateway` | Delivery/wake routing, adapter bindings, claim/attempt state, echo suppression, outage pause/resume, local sentinel coordination. | The work changes how events become deliveries, how agents are woken, routing/claim/retry/dedupe/terminalization policy, or gateway diagnostics. | Channel data ownership, Core canonical state, Hermes implementation details beyond adapter contracts. |
| `den-desktop` | Human conductor cockpit and local desktop/sidecar ergonomics. File/diff visibility, operator UI, desktop workflow observability. | The work changes the installed desktop app, sidecar, local UX, file/diff views, or desktop-specific connection/config behavior. | Server-owned API semantics or state changes except as consumed through contracts. |
| `den-hermes-bridge` / `den-hermes` | Thin Hermes-specific adapter layer: profile identity mapping, Hermes memory/provider/skill/profile/runtime glue, wake delivery, status reporting. | The work is specific to Hermes profiles, Hermes Gateway/plugin behavior, spawned-Hermes runtime glue, or Hermes status/wake adaptation. | Den-owned channel semantics, Gateway routing policy, canonical Den state. |
| `den-worker-runtime` | Intended host-local process/session runtime boundary for tmux/Docker/Pi/worker lifecycle mechanics. | The work changes server-side worker/session process lifecycle or runtime host mechanics. | Workflow state authority, review policy, task records, or Hermes-specific agent behavior. |
| `den-pi` | Pi-side helper/extensions/skills for agent worker flows. | The work changes Pi-side worker helpers or extension packaging. | Core worker-run state or future server-side worker runtime service behavior. |
| `den-network` | Operational/infrastructure facts and sysadmin notes for Den deployment, machines, Docker/NFS, service accounts, and fleet operations. | The work is mainly deployment/infrastructure documentation, machine setup, systemd/network/storage operations, or service-account policy. | Product/API implementation unless paired with a concrete owning repo task. |

### Filing rules of thumb

1. **Implementation tasks usually go in the repo that owns the runtime behavior.** If code changes live in one service, file the task in that service's Den project.
2. **Cross-service architecture docs can live in the highest relevant owning project or `_global`, but must name affected repos explicitly.** Link follow-up implementation tasks in the owning projects.
3. **Core is the source of truth, not the dumping ground.** File in `den-core` only when canonical state/contracts are involved.
4. **MCP facade work is not Den-system work by default.** File in `den-mcp` only when the MCP adapter/tool surface itself changes.
5. **When unsure, file a short planning/inventory task before implementation.** The planning task should choose an owning repo, list affected repos, and split follow-ups rather than letting agents guess.

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
