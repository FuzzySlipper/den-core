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
| Visible channel transcript post | Channels | `channel_message` | `PostChannelMessageRequest` | `POST /api/channels/{channelId}/messages` | Subject to channel membership wake policy |
| Direct agent wakeable channel request | Channels | `direct_agent_event` / `direct_agent_message` | source kind `wake_event` in the backing channel message; retired Gateway-shaped aliases `POST /api/gateway/direct-agent-messages` and `GET /api/gateway/events` | `POST /api/direct-agent-events`; readback/list via `GET /api/direct-agent-events` and `GET /api/direct-agent-events/{eventId}` | Yes: target-member wake primitive |
| Retired Gateway delivery control record | Historical Gateway seam | `delivery_request` / `delivery_attempt` | historical delivery/wake records only | Archived Gateway evidence and compatibility readback where retained | Not a green-path producer |
| Final visible delivery reply | Channels | `gateway_delivery_final_message` | `sourceKind=gateway_delivery`, `dedupeKey=gateway-delivery:{id}:final`; retired Gateway-shaped alias `POST /api/gateway/system-messages` | A normal `channel_message` with terminal delivery metadata | No additional wake for peer-agent fanout; it terminalizes a delivery |
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

### Post a visible channel message

Use Channels `channel_message` APIs for normal channel transcript posts.

```http
POST /api/channels/3/messages
Content-Type: application/json

{
  "senderType": "agent",
  "senderIdentity": "den-mcp-runner",
  "body": "Visible channel reply",
  "messageKind": "agent_text",
  "sourceKind": "manual_agent_message"
}
```

Do not use this as the only durable task handoff when the content is task state; mirror or link the relevant Core task message.

### Wake a specific agent

Use the Channels-owned direct-agent event surface. The backing row is still a channel message with `sourceKind=wake_event`, but the public contract name is `direct_agent_event` / `direct_agent_message` because it targets a member and is wakeable. Use `POST /api/direct-agent-events` to create events, `GET /api/direct-agent-events` to list/subscription-read events, and `GET /api/direct-agent-events/{eventId}` for single-event readback. Do not document `/api/gateway/direct-agent-messages` or `/api/gateway/events` as green paths; both are retired Gateway-shaped compatibility aliases retained only as historical/migration evidence where still present.

```http
POST /api/direct-agent-events
Content-Type: application/json

{
  "projectId": "den-core",
  "memberIdentity": "den-mcp-planner",
  "senderIdentity": "den-mcp-runner",
  "body": "Please review the #1555 contract summary in the task thread."
}
```

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
- Channels `PostChannelMessageRequest` is already surface-qualified enough because it is in the Channels namespace and route.
- Channels `direct_agent_event` / `direct_agent_message` is the wakeable target-agent contract name. The green-path creation route is `POST /api/direct-agent-events`; readback/list routes are `GET /api/direct-agent-events` and `GET /api/direct-agent-events/{eventId}`. `/api/gateway/direct-agent-messages` and `/api/gateway/events` are retired Gateway-shaped aliases/tombstones and must not be used as new guidance.
- Gateway-shaped `system-messages` is a retired historical route name. Docs should describe its final-reply artifact as `gateway_delivery_final_message` when `sourceKind=gateway_delivery`/`dedupeKey=gateway-delivery:{id}:final` are present.
- `channel_activity_event` / `delivery_activity_event` is the canonical non-waking progress vocabulary. The preferred green-path route is `POST /api/channels/{channelId}/activity-events`; the supported resolver route is `POST /api/channel-activity-events`. The retired `/api/gateway/channel-activity-events` spelling should appear only in migration/tombstone context. Progress must never use `gateway-delivery:{id}:final`.
- Retired `/api/gateway/events` and `/api/gateway/test-wakes` references are historical/tombstone aliases for Channels-owned direct-agent/event readback and controlled wake-test recording. New public examples should use Channels-owned routes or explicitly label Gateway-shaped names as retired.
- Legacy dispatch and Pi/publisher MCP tools are quarantined under `legacy_*` in the live Core MCP schema after #1610; do not use them in new contract examples.
