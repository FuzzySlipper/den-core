# Den communication API surface naming

This document is the task #1555 cross-service contract alignment companion to the Den document `den-core/den-communication-surfaces-concept-map`.

Den APIs intentionally expose several different communication surfaces. The word "message" is acceptable only when the route/DTO namespace already identifies the surface (for example `ChannelMessage` or `ProjectMessage`). New contracts should prefer the explicit surface names below and should document compatibility aliases when older names remain.

## Surface vocabulary

| Surface | Owning service | Preferred public contract name | Existing compatibility names | Primary route/tool shape | Wakes agents? |
| --- | --- | --- | --- | --- | --- |
| Durable project/task work record | Core | `project_message` / `task_message` | MCP `send_message`, REST `SendMessageRequest` | `POST /api/projects/{projectId}/messages` with optional `task_id`/thread | No, unless a separate watcher/notification is configured |
| User attention item | Core | `user_notification` | `notification` source kind in summaries | `POST /api/projects/{projectId}/user-notifications` / MCP `send_user_notification` | Human attention path, not generic agent wake |
| Agent ops/attention item | Core | `agent_stream_entry` | agent-stream note/question/nudge | `POST /api/agent-stream` / MCP `send_agent_stream_message` | Optional, via delivery mode/bindings |
| Structured workflow artifact | Core | `worker_completion_packet`, `review_request`, `review_findings_packet`, `validation_packet` | worker/review packet tools | Review/worker packet APIs and MCP tools | Workflow-state dependent |
| Visible channel transcript post | Channels | `channel_message` | `PostChannelMessageRequest` | `POST /api/channels/{channelId}/messages` | Subject to channel membership wake policy |
| Direct agent wakeable channel request | Channels + Gateway | `direct_agent_message` | source kind `wake_event` in the backing channel message | `POST /api/gateway/direct-agent-messages` | Yes: target-member wake primitive |
| Gateway delivery control record | Gateway | `delivery_request` / `delivery_attempt` | delivery/wake records | Gateway internal/API delivery state | Yes: control plane |
| Final visible delivery reply | Channels + Gateway | `gateway_delivery_final_message` | `sourceKind=gateway_delivery`, `dedupeKey=gateway-delivery:{id}:final`; Gateway compatibility route `POST /api/gateway/system-messages` | A normal `channel_message` with terminal delivery metadata | No additional wake for peer-agent fanout; it terminalizes a delivery |
| Interim delivery progress | Channels + Gateway | `delivery_activity_event` / `channel_activity_event` | `channel_activity_events` table | `POST /api/gateway/channel-activity-events` and `POST /api/channels/{channelId}/activity-events` | No; explicitly non-waking and non-terminal |
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

Use the Channels/Gateway direct-agent request surface. The backing row is still a channel message with `sourceKind=wake_event`, but the public contract name is `direct_agent_message` because it targets a member and is wakeable.

```http
POST /api/gateway/direct-agent-messages
Content-Type: application/json

{
  "projectId": "den-core",
  "memberIdentity": "den-mcp-planner",
  "senderIdentity": "den-mcp-runner",
  "body": "Please review the #1555 contract summary in the task thread."
}
```

### Append delivery progress

Use delivery/channel activity events. These are observability records, not transcript messages and not final replies.

```http
POST /api/gateway/channel-activity-events?projectId=den-core
Content-Type: application/json

{
  "agentIdentity": "den-mcp-runner",
  "deliveryRequestId": "90",
  "eventType": "tool_call",
  "deliveryStage": "tool",
  "terminal": false,
  "summary": "Read Core/Channels/Gateway contract docs"
}
```

### Post final delivery reply

The final visible response is a `channel_message` with Gateway delivery metadata. Reserve the final dedupe key for the true terminal response only.

```http
POST /api/gateway/system-messages
Content-Type: application/json

{
  "projectId": "den-core",
  "senderIdentity": "den-gateway",
  "messageKind": "agent_text",
  "body": "Done: contract names aligned and linked from task #1555.",
  "sourceKind": "gateway_delivery",
  "sourceId": "90",
  "deliveryRequestId": "90",
  "dedupeKey": "gateway-delivery:90:final"
}
```

`POST /api/gateway/system-messages` is a compatibility route name from the Gateway-to-Channels seam. New docs should call the resulting artifact a `gateway_delivery_final_message` or `gateway delivery final channel message`, not a generic system message.

## Compatibility and deprecation notes

- Core MCP `send_message` and REST `SendMessageRequest` remain compatibility names for durable Core project/task messages. Renaming them would be a broader client migration; descriptions and examples carry the surface qualifier instead.
- Channels `PostChannelMessageRequest` is already surface-qualified enough because it is in the Channels namespace and route.
- Channels/Gateway `direct-agent-messages` is already explicit and should remain the wakeable target-agent contract name.
- Gateway `system-messages` is retained as a compatibility route, but docs should describe its final-reply use as `gateway_delivery_final_message` when `sourceKind=gateway_delivery`/`dedupeKey=gateway-delivery:{id}:final` are present.
- `channel_activity_event` / `delivery_activity_event` is the canonical non-waking progress vocabulary. Progress must never use `gateway-delivery:{id}:final`.
- Legacy dispatch and Pi/publisher MCP tools are quarantined under `legacy_*` in the live Core MCP schema after #1610; do not use them in new contract examples.
