# Den communication API surface naming

This document is the task #1555 cross-service contract alignment companion to the Den document `den-core/den-communication-surfaces-concept-map`.

Den APIs intentionally expose several different communication surfaces. The word "message" is acceptable only when the route/DTO namespace already identifies the surface (for example `ChannelMessage` or `ProjectMessage`). New contracts should prefer the explicit surface names below and should distinguish active aliases from retired historical route names/tombstones when older names remain in code, archived docs, or migration evidence.

## Surface vocabulary

| Surface | Owning service | Preferred public contract name | Active aliases / retired historical names | Primary route/tool shape | Wakes agents? |
| --- | --- | --- | --- | --- | --- |
| Durable project/task work record | Core | `project_message` / `task_message` | MCP `send_message`, REST `SendMessageRequest` | `POST /api/projects/{projectId}/messages` with optional `task_id`/thread | No, unless a separate watcher/notification is configured |
| User attention item | Core | `user_notification` | `notification` source kind in summaries | `POST /api/projects/{projectId}/user-notifications` / MCP `send_user_notification` | Human attention path, not generic agent wake |
| Agent ops/attention item | Core | `agent_stream_entry` | agent-stream note/question/nudge | `POST /api/agent-stream` / MCP `send_agent_stream_message` | Optional, via delivery mode/bindings |
| Structured workflow artifact | Core | `worker_completion_packet`, `review_request`, `review_findings_packet`, `validation_packet` | worker/review packet tools | Review/worker packet APIs and MCP tools | Workflow-state dependent |
| Visible channel transcript post | Conversation successor | `channel_message` / `conversation_message` | legacy Channels `PostChannelMessageRequest`; legacy `POST /api/channels/{channelId}/messages` compatibility | Green path: `POST /v1/conversation/channels/{channel_id}/messages` through Gateway; legacy readback/write aliases only during migration | No by itself; transcript rows do not own executable wake authority |
| Direct agent executable wake intent | Delivery successor | `delivery_intent` / direct-agent wake intent | legacy Channels `direct_agent_event` / `direct_agent_message`; source kind `wake_event`; legacy `POST /api/direct-agent-events`; retired Gateway aliases `POST /api/gateway/direct-agent-messages` and `GET /api/gateway/events` | Green path: `POST /v1/delivery/intents` through Gateway; read/list via `GET /v1/delivery/intents`; legacy direct-agent routes are compatibility/readback history until producer migration/tombstone completes | Yes: Delivery owns executable wake lifecycle |
| Legacy direct-agent transcript/readback evidence | Channels compatibility/archive | `legacy_direct_agent_event` / `legacy_direct_conversation_entry` | legacy `GET /api/direct-agent-events`, `GET /api/direct-agent-events/{eventId}`, direct conversation routes, backing `channel_messages.source_kind=wake_event` | Readback/display only where retained for old evidence; no new green-path wake production | No new workflow use; old rows may describe historical wakes |
| Retired Gateway delivery control record | Historical Gateway seam | `delivery_request` / `delivery_attempt` | historical delivery/wake records only | Archived Gateway evidence and compatibility readback where retained | Not a green-path producer |
| Final visible delivery reply | Conversation successor | `gateway_delivery_final_message` | legacy Channels final `channel_message`; `sourceKind=gateway_delivery`, `dedupeKey=gateway-delivery:{id}:final`; retired Gateway-shaped alias `POST /api/gateway/system-messages` | A normal Conversation successor message with terminal delivery metadata; legacy channel-message alias only where retained for migration/readback | No additional wake for peer-agent fanout; it terminalizes a delivery |
| Interim delivery progress | Channels | `delivery_activity_event` / `channel_activity_event` | `channel_activity_events` table; retired Gateway-shaped alias `POST /api/gateway/channel-activity-events` | Canonical scoped route `POST /api/channels/{channelId}/activity-events`; supported resolver route `POST /api/channel-activity-events` | No; explicitly non-waking and non-terminal |
| Legacy dispatch archive | Core/MCP compatibility | `legacy_dispatch_*` | old dispatch route/tool names only in archives | read-only archive/debug where retained | No new workflow use |

## Scenario-to-API examples

### Post task status update

Use Core durable messages because future agents must find the handoff in the task thread.

```http
POST /api/projects/den-core/messages
Content-Type: application/json

{
  "sender": "den-mcp-runner",
  "content": "Implementation packet...",
  "task_id": 1555,
  "intent": "status_update",
  "metadata": { "type": "implementation_packet" }
}
```

MCP-facing compatibility: `send_message` remains the Core project/task-message tool. Descriptions should continue to say it writes durable Core project/task messages, not channel transcript posts or direct-agent wakes.

### Notify Patch

Use the Core user-notification surface when the goal is human attention rather than durable task history alone.

```http
POST /api/projects/den-core/user-notifications
Content-Type: application/json

{
  "sender": "den-mcp-runner",
  "content": "Live deployment needs a service restart decision.",
  "task_id": 1555,
  "urgency": "normal"
}
```

Also post a task message when the notification contains task-state evidence that future agents must see.

### Post a visible channel/conversation message

Use the Conversation successor for normal human-facing channel transcript posts. Legacy Channels `POST /api/channels/{channelId}/messages` may remain reachable for compatibility/readback during the den-channels slimming wave, but it is no longer the green-path producer for new guidance.

```http
POST /v1/conversation/channels/3/messages
Content-Type: application/json
Idempotency-Key: conversation-message:example-1555

{
  "sender_type": "agent",
  "sender_identity": "den-mcp-runner",
  "body": "Visible channel reply",
  "message_kind": "agent_text",
  "source_kind": "manual_agent_message"
}
```

Do not use this as the only durable task handoff when the content is task state; mirror or link the relevant Core task message.

### Wake a specific agent

Use Delivery successor for executable direct-agent wake intent lifecycle. If the wake also needs a human-facing transcript row, create/link that display artifact through the Conversation successor; do not make the transcript row the executable wake authority.

```http
POST /v1/delivery/intents
Content-Type: application/json

{
  "target_identity": {
    "profile": "den-mcp-planner",
    "instance_id": "den-mcp-planner@den-srv"
  },
  "idempotency_key": "wake:den-core:den-mcp-planner:review-1555",
  "source_ref": "core-task:1555"
}
```

Legacy den-channels `POST /api/direct-agent-events` and `POST /api/direct-conversations/{conversationId}/send` combined transcript/evidence rows with executable wake behavior. They are compatibility/history routes during producer migration and tombstone work, not the green path. Preserve `GET /api/direct-agent-events` and `GET /api/direct-agent-events/{eventId}` guidance only as legacy readback for old evidence where still needed. Do not document `/api/gateway/direct-agent-messages` or `/api/gateway/events` as green paths; both are retired Gateway-shaped compatibility aliases/tombstones.

### Append delivery progress

Use Channels activity events. These are observability records, not transcript messages and not final replies. Prefer the channel-scoped canonical route `POST /api/channels/{channelId}/activity-events`; the resolver route `POST /api/channel-activity-events` is also supported when the caller supplies enough project/channel context for Channels to resolve the target. Do not use the retired Gateway-shaped `/api/gateway/channel-activity-events` alias in new examples.

```http
POST /api/channels/3/activity-events
Content-Type: application/json

{
  "agentIdentity": "den-mcp-runner",
  "deliveryRequestId": "90",
  "eventType": "tool_call",
  "deliveryStage": "tool",
  "terminal": false,
  "summary": "Read Core/Channels contract docs"
}
```

### Post final delivery reply

The final visible response is a `channel_message` with terminal delivery metadata. Reserve the final dedupe key for the true terminal response only. Gateway-shaped `system-messages` wording is historical; new docs should describe the artifact, not a Gateway-owned producer.

```http
POST /api/channels/3/messages
Content-Type: application/json

{
  "senderType": "system",
  "senderIdentity": "den-channels",
  "messageKind": "agent_text",
  "body": "Done: contract names aligned and linked from task #1555.",
  "sourceKind": "gateway_delivery",
  "sourceId": "90",
  "deliveryRequestId": "90",
  "dedupeKey": "gateway-delivery:90:final"
}
```

`POST /api/gateway/system-messages` was a Gateway-to-Channels seam route name. Treat it as a retired historical alias/tombstone in new documentation. The resulting artifact is a `gateway_delivery_final_message` or `gateway delivery final channel message`, not a generic system message and not evidence that Gateway owns the active API surface.

## Compatibility and deprecation notes

- Core MCP `send_message` and REST `SendMessageRequest` remain compatibility names for durable Core project/task messages. Renaming them would be a broader client migration; descriptions and examples carry the surface qualifier instead.
- Conversation successor owns new human-facing channel transcript rows. Legacy Channels `PostChannelMessageRequest` and `POST /api/channels/{channelId}/messages` are compatibility names/routes during the den-channels slimming wave, not the preferred contract for new guidance.
- Delivery successor owns direct-agent executable wake intent lifecycle. Legacy Channels `direct_agent_event` / `direct_agent_message`, `POST /api/direct-agent-events`, and `POST /api/direct-conversations/{conversationId}/send` are compatibility/history routes until active producers migrate and den-channels tombstone tasks can complete. Legacy readback routes such as `GET /api/direct-agent-events` and `GET /api/direct-agent-events/{eventId}` may remain for old evidence only.
- Gateway-shaped `system-messages` is a retired historical route name. Docs should describe its final-reply artifact as `gateway_delivery_final_message` when `sourceKind=gateway_delivery`/`dedupeKey=gateway-delivery:{id}:final` are present, and should route new final visible replies through the Conversation successor where available.
- `channel_activity_event` / `delivery_activity_event` is the canonical non-waking progress vocabulary. The preferred long-term owner is Observation/Timeline successor surfaces; while legacy Channels compatibility remains, prefer the channel-scoped route `POST /api/channels/{channelId}/activity-events` or supported resolver route `POST /api/channel-activity-events` over retired Gateway-shaped `/api/gateway/channel-activity-events`. Progress must never use `gateway-delivery:{id}:final`.
- Retired `/api/gateway/events`, `/api/gateway/direct-agent-messages`, and `/api/gateway/test-wakes` references are historical/tombstone aliases for old den-channels direct-agent/event readback or controlled wake-test recording. New public examples should use Delivery successor for executable wakes and Conversation successor for transcript rows, or explicitly label Gateway-shaped names as retired.
- Legacy dispatch and Pi/publisher MCP tools are quarantined under `legacy_*` in the live Core MCP schema after #1610; do not use them in new contract examples.
