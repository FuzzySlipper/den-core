# Notification Feed API

## Overview

The canonical Core-backed user notification feed API provides server-side notification listing, filtering, and read-state management. Notifications are stored as Core `Message` rows with `intent='notification'`, using the existing `message_reads` table for read state.

This replaces Den Web's frontend-only notification aggregation with a server-side canonical feed.

## Endpoints

### List notifications (cross-project)

```
GET /api/user-notifications
```

Returns notifications across all projects, newest first.

#### Query parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectId` | string | null | Filter to a specific project/space ID |
| `taskId` | int | null | Filter to a specific task ID |
| `sender` | string | null | Filter by sender/agent identity |
| `metadataType` | string | null | Filter by metadata type (e.g. `agent_work_complete`) |
| `urgency` | string | null | Filter by urgency: `low`, `normal`, or `high` |
| `isRead` | bool | null | Filter by read state. **Requires `readFor`** |
| `readFor` | string | null | Agent identity for read-state derivation |
| `limit` | int | 20 | Max results (1-100) |
| `offset` | int | 0 | Pagination offset |

### List notifications (project-scoped)

```
GET /api/projects/{projectId}/user-notifications
```

Same query parameters as above except `projectId` comes from the route.

### Mark notifications read

```
POST /api/user-notifications/mark-read
```

**Body:**
```json
{
  "agent": "patch",
  "notification_ids": [1, 2, 3]
}
```

Only marks messages with `intent='notification'` as read. Returns `{ "marked": <count> }`.

## MCP Tools

### `get_user_notifications`

Lists notifications with the same filters as the REST endpoint. Parameters: `project_id`, `task_id`, `sender`, `metadata_type`, `urgency`, `is_read`, `read_for_agent`, `limit`, `offset`.

### `mark_notifications_read`

Marks notifications read for an agent. Parameters: `agent`, `notification_ids` (comma-separated).

## Response shape

Each `NotificationFeedItem`:

```json
{
  "id": 42,
  "project_id": "den-core",
  "task_id": 1789,
  "thread_id": null,
  "sender": "den-mcp-runner",
  "content": "Runner completed assigned queue for den-core",
  "metadata": {
    "type": "agent_work_complete",
    "urgency": "normal",
    "source_sender": "den-mcp-runner"
  },
  "urgency": "normal",
  "is_read": false,
  "created_at": "2026-05-31T01:30:00"
}
```

## `agent_work_complete` metadata contract

When an agent/orchestrator completes its assigned work, it should emit a notification with `metadata.type = "agent_work_complete"`. The metadata should include:

| Field | Type | Description |
|---|---|---|
| `type` | string | `"agent_work_complete"` |
| `notification_class` | string | `"operator_attention"` |
| `agent_identity` | string | Identity of the completing agent |
| `completion_scope` | string | e.g. `"assigned_queue"` |
| `final_status` | string | `"completed"`, `"blocked"`, or `"failed"` |
| `project_ids` | string[] | Projects with completed work |
| `task_ids` | int[] | Tasks in scope |
| `completed_task_ids` | int[] | Successfully completed tasks |
| `blocked_task_ids` | int[] | Blocked tasks needing attention |
| `run_ids` | string[] | Worker run IDs |

**Suggested severities:**
- `normal`: all assigned work completed cleanly
- `high`: blocked/failed and needs user/planner action
- `low`: FYI completion with no action needed

### Example

```json
{
  "type": "agent_work_complete",
  "notification_class": "operator_attention",
  "agent_identity": "den-mcp-runner",
  "completion_scope": "assigned_queue",
  "final_status": "completed",
  "project_ids": ["den-core"],
  "task_ids": [1788, 1789],
  "completed_task_ids": [1788, 1789],
  "blocked_task_ids": [],
  "run_ids": ["t1789-pool-coder-20260531022030"]
}
```

## Backward compatibility

- The existing `send_user_notification` MCP tool is unchanged.
- The existing `POST /api/projects/{projectId}/messages` with `intent=notification` continues to work.
- The new feed endpoints are additive — they project over the same `messages` table.

## Design notes

- **Projection, not duplication**: Notifications are not stored in a separate table. The feed queries `messages WHERE intent='notification'`.
- **Read state**: Uses the existing `message_reads` table. The `is_read` field is derived per agent identity.
- **Urgency**: Stored in the `metadata` JSON column. Filtering uses the active provider's JSON operator (`jsonb` on live Postgres).
- **Metadata type**: Stored in `metadata.type`. Filtering uses the active provider's JSON operator (`jsonb` on live Postgres).
