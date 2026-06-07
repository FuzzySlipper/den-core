# Retry-Cap Calibration Policy

*Core-owned guidance for evidence-driven cap tuning. Updated per task #2074 and #2078.*

## Default cap

The global retry cap is **4 attempts per role** in `determine_orchestrator_next_action` (raised from 3 in #2078).
When a role's attempts reach `max_attempts`, the orchestrator escalates rather than launching another worker.

## When to raise the cap (4 → 5)

Raise the global cap from 4 to 5 if the `retry_cap_report` shows **all** of:

1. A material share of cap-hit tasks (≥30% of evaluated tasks in a representative window) complete successfully after a Planner-authorized 5th retry.
2. Planner authorization is routine rubber-stamping — the extra retry is narrow and scoped to the specific gap.
3. 5th-attempt failures are rare and their failure categories are the same as the preceding 4th-attempt failures (not new scope creep).

**Example:** `den-hermes-bridge #2071` hit the 3-attempt cap (pre-#2078), Planner authorized one narrow retry, and the 4th attempt produced completed implementation/validation/drift/packet-audit packets.

## When to keep the cap at 4

Keep the cap at 4 if:

1. Cap-hit tasks that get an extra retry still fail frequently.
2. Extra retries widen scope (new gaps discovered rather than fixing existing ones).
3. Tasks genuinely need Planner attention at the cap — e.g., ambiguous acceptance criteria, dependency resolution, or architecture decisions.
4. Few tasks hit the cap (low cap pressure).

## Operational blockers are not cap pressure

Blockers from deployment unavailability, auth failures, routing problems, or membership gaps are operational issues, not retry-cap issues. The `retry_cap_report` excludes tasks with only 1-2 attempts (below cap). These should be tracked separately via `blocked_task` notifications.

## Using the report

```json
// MCP tool call
retry_cap_report(project_id="den-core", since="2026-06-01", max_attempts=4)
```

Key fields in output:
- `tasks_hitting_cap` — count of tasks where any role reached `max_attempts`
- `completed_after_extra_retry` — cap-hit tasks that Planner authorized and succeeded
- `blocked_at_cap` — cap-hit tasks with no Planner authorization
- `blocked_after_extra_retry` — cap-hit tasks that still failed after extra retry
- `in_progress` — cap-hit tasks still in progress after Planner authorization
- `cancelled` — cap-hit tasks that were cancelled
- `calibration_guidance` — evidence-driven recommendation

## Per-project caps (future)

The report supports per-project analysis. If only one project shows cap pressure while others are calm, consider a per-project override rather than a global cap change.
