# Den Gateway / Core contract

Den Core owns canonical Den state, persistence, and REST contracts. `den-gateway` owns local routing, delivery state, adapter retry policy, and sentinel-local durability. This contract gives Gateway stable Core-owned surfaces without requiring Gateway to read Core internals or the old `den-mcp` SQLite layout.

## Communication-surface naming

Gateway/Core communication contracts should use the vocabulary in `docs/communication-api-surface-naming.md` and the Den document `den-core/den-communication-surfaces-concept-map`.

Core-owned durable work records are `project_message` / `task_message` records and user-facing attention items are `user_notification` records. Gateway delivery control state is separate: `delivery_request` / `delivery_attempt` records are routing/control-plane state, `delivery_activity_event` records are non-waking progress, and `gateway_delivery_final_message` is the final visible channel reply that terminalizes a delivery. Do not describe all of these as generic "messages" in new Gateway/Core contracts.

## Source summaries and event outbox

Shared with Channels via task #1362:

- `GET /api/source-summaries/{sourceKind}/{sourceId}?projectId=<optional>`
- `GET /api/events/outbox?after=<cursor>&projectId=<optional>&limit=<1..200>`

Gateway should use these instead of joining Core tables directly.

## Gateway readiness

`GET /api/gateway/readiness`

Returns a `GatewayReadinessResponse`:

- `status`: `ready`, `degraded`, or `blocked`.
- `service`: currently `den-core-gateway-contract`.
- `checked_at`: UTC timestamp.
- `checks`: named checks for:
  - `process`
  - `app`
  - `database`
  - `migrations`
  - `gateway_contract`
  - `service_auth`

`service_auth` is `degraded` when no service token is configured so local/stub deployments can run while production can distinguish unauthenticated mode.

## Binding projection

`GET /api/gateway/bindings?projectId=&status=&role=&agentIdentity=&transportKind=&timeoutMinutes=`

Returns a Gateway-compatible projection over `agent_instance_bindings`:

- `instance_id`
- `project_id`
- `agent_identity`
- `agent_family`
- `role`
- `transport_kind`
- `session_id`
- `status`
- `checked_in_at`
- `last_heartbeat`
- `metadata`

Default status filter is `active,degraded`; stale binding cleanup uses `timeoutMinutes` clamped to 1..120.

## Sentinel reconciliation intake

`POST /api/gateway/sentinel/events`

Request fields:

- `sentinel_id`
- `event_type`
- `state`
- `project_id`
- `outage_id`
- `reason`
- `observed_at`
- `cursor`
- `metadata`
- `dedupe_key`

Core stores accepted events as durable `agent_stream_entries` with `event_type = gateway_sentinel_<event_type>`. Outage-like states (`outage`, `paused`, `degraded`) use `wake` delivery mode so the shared event outbox marks them as attention-worthy. Duplicate `dedupe_key` submissions are idempotent through the existing agent stream repository.

Response fields:

- `status = accepted`
- `agent_stream_entry_id`
- `event_type`
- `dedupe_key`
- `outbox_cursor`

## Service-to-service auth

Gateway contract endpoints honor an optional shared token configured as:

```text
DenMcp:GatewayContract:ServiceToken
```

When set, callers must provide either:

```http
Authorization: Bearer <token>
```

or:

```http
X-Den-Service-Token: <token>
```

When unset, the Gateway contract endpoints are intentionally open for local/stub deployments and readiness reports `service_auth` as degraded.

## Boundaries

Core does **not** implement Gateway routing, delivery state machines, channel wake policy, adapter retry state, or sentinel local store. Missing future pieces should become explicit `den-core` or `den-gateway` tasks rather than hidden TODOs.
