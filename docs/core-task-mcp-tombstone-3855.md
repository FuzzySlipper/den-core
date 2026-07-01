# Core Task MCP Tombstones

Status: task #3855 follow-up after the den-services tasks lifeboat route
cutover.

## Tombstoned MCP Writes

The stable agent-facing MCP endpoint now routes the task family through
`den-services/mcp` to the `tasks` service. Core keeps the legacy MCP tool
definitions for compatibility/profile visibility, but these tools reject before
touching `ITaskRepository`:

- `create_task`
- `update_task`
- `add_dependency`
- `remove_dependency`

The rejection is intentionally loud. It prevents accidental split writes into
Core task, dependency, and task-history tables while telling the operator to use
the den-services MCP endpoint or the tasks service API.

## Remaining Core Surfaces

Core read tools (`get_task`, `get_task_workflow_summary`, `list_tasks`,
`next_task`) remain readable for archive/rollback inspection until the broader
Core-off canary removes or hides them.

Direct Core HTTP task routes under `/api/projects/{projectId}/tasks` are not
tombstoned by this MCP compatibility change. Treat them as rollback/admin
compatibility only; supported task writers should use the den-services tasks
API. Tombstoning those HTTP routes is a wider API-removal decision because older
Core API tests and operator workflows still exercise them directly.

Review follow-up splitting can still create a Core task through review workflow
internals. That path is not part of the flipped MCP task-family write surface;
it should be retired or moved with the remaining review/Core-off cleanup rather
than hidden inside the task MCP tombstone patch.
