# Task 3165 Core legacy route cleanup audit

Date: 2026-06-22

## Scope

Audited active Core docs, deploy smokes, and integration references for legacy den-channels/Gateway compatibility paths before den-channels retirement.

## Decisions

- Core deploy correctness is verified through Core's explicit service boundary (`127.0.0.1:5299`) rather than the old Den Web `/den-core-api/*` proxy.
- Agent-facing MCP compatibility remains through the Den MCP facade at `192.168.1.10:5199/mcp`, but deploy smokes use Core loopback MCP as the service correctness oracle.
- Communication-surface docs now route:
  - executable wakes through Delivery successor `/v1/delivery/intents`;
  - visible channel transcript/final replies through Conversation successor `/v1/conversation/...`;
  - non-waking progress breadcrumbs through Observation successor `/v1/observation/activity-events`.
- Legacy den-channels and Gateway-shaped route names remain only as compatibility/archive/tombstone vocabulary.

## Sweep evidence

Active files were swept for:

```text
/den-core-api
DEN_CHANNELS
18081
/api/direct-agent-events
/api/channel-subscriptions
/api/gateway/direct-agent-messages
/api/gateway/events
/api/gateway/system-messages
/api/gateway/channel-activity-events
/api/channels/{channelId}/messages
/api/channels/{channelId}/activity-events
/api/channel-activity-events
```

Expected remaining references after cleanup:

- Core-owned `/api/gateway/readiness`, `/api/gateway/bindings`, and `/api/gateway/sentinel/events` contract/tests are **not** den-channels compatibility paths; they are Core's Gateway contract surface.
- `docs/communication-api-surface-naming.md` keeps legacy route names only in compatibility/archive notes to warn callers away from them.
- `src/DenCore/Models/DirectDeliveryContract.cs` and `tests/DenCore.Service.Tests/*Gateway*` keep `/api/gateway/bindings` as Core-owned compatibility for Gateway contract consumers, not a den-channels catch-all.
