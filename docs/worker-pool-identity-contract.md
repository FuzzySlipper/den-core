# Worker Pool Identity Contract (v2)

**Canonical identifier contract for Core worker pool rows and assignment payloads.**
Downstream consumers (Den Channels #1769, current gateway/proxy in `den-services/gateway` — legacy Den Gateway #1770 is historical, Den Hermes Bridge #1767, Den Web) MUST read
and respect this contract when consuming Core worker pool APIs.

> **Gateway retirement note (2026-06):** this contract predates the `den-gateway` decommission. Treat `Den Gateway` task references as historical implementation context; new gateway/proxy work belongs in `den-services/gateway`.

---

## Identifier Fields

| Field | Model | Type | Required | Description |
|-------|-------|------|----------|-------------|
| `worker_identity` | `WorkerPoolMember`, `WorkerAssignment` | `string` | Always | **Concrete lifecycle identity / primary key.** The unique, concrete pool member identifier. All lifecycle mutations (lease, release, quarantine, status change) key on this field. Never use `profile_identity` alone for mutations. |
| `pool_member_id` | `WorkerPoolMember`, `WorkerAssignment` | `string?` | No | Alias for `worker_identity`. Defaults to `worker_identity` when not supplied. Provided for downstream consumers that prefer explicit "pool member id" naming. |
| `profile_identity` | `WorkerPoolMember`, `WorkerAssignment` | `string?` | No | **Shared role/profile identity** (e.g. `"spawned-coder"`, `"spawned-reviewer"`). Multiple concrete pool members can share the same `profile_identity`. Core uses this for pool-wide filtering and routing. **NOT a lifecycle key.** |
| `worker_role` | `WorkerPoolMember`, `WorkerAssignment` | `string?` | No | Role category (e.g. `"coder"`, `"reviewer"`, `"validator"`, `"drift_checker"`, `"packet_auditor"`). Separate from assignment `role`. |
| `agent_instance_id` | `WorkerPoolMember`, `WorkerAssignment` | `string?` | No | Concrete Gateway/Core agent instance binding id. Populated by Gateway on check-in. |
| `channel_id` | `WorkerPoolMember`, `WorkerAssignment` | `string?` | No | Den channel id for correlation with channel membership. Populated by Channels. |
| `session_id` | `WorkerPoolMember` | `string?` | No | Hermes/worker session id for correlation with active sessions. Populated by Gateway or Hermes Bridge. |
| `assignment_id` (id) | `WorkerAssignment` | `int` | Always | Auto-increment assignment primary key. Direct lifecycle operations (transition, cleanup, release) key on this. |
| `run_id` | `WorkerAssignment` | `string` | Always | Worker run id tracking execution (e.g. spawned-Hermes `run_id`). |

---

## Lifecycle Rules

1. **Lease** — keyed by concrete `worker_identity`. Optional `profile_identity` and `worker_role` filters
   select which available workers to consider, but the lease itself binds the concrete `worker_identity`.
2. **Assign** — creates a `WorkerAssignment` with concrete `worker_identity`. Profile fields are
   denormalized from the pool member for readback convenience.
3. **Transition** — keyed by `assignment_id`. When terminal, sets member back to `available`.
4. **Quarantine** — keyed by concrete `worker_identity`. Quarantining one member with a shared
   `profile_identity` does NOT affect other members sharing the same profile.
5. **Release** — keyed by `assignment_id`. Requires terminal state + cleanup evidence.
6. **Status changes** — keyed by concrete `worker_identity`.

---

## Compatibility Notes

- `worker_identity` is the canonical primary key. It is the concrete member identity.
- `pool_member_id` is an alias. When not supplied on upsert, defaults to `worker_identity`.
- Existing consumers using `worker_identity` for lifecycle operations continue to work unchanged.
- New consumers MAY use `pool_member_id` or `worker_identity` interchangeably for lifecycle ops.
- **Never use `profile_identity` alone for mutation** — it is a shared/group identity.
- For readback/display, use `profile_identity` + `worker_role` + `worker_identity` to disambiguate.

---

## SQLite Schema (worker_pool_members)

```sql
CREATE TABLE IF NOT EXISTS worker_pool_members (
    worker_identity      TEXT PRIMARY KEY,
    profile_identity     TEXT NOT NULL DEFAULT '',
    worker_role          TEXT,
    display_name         TEXT,
    capabilities         TEXT,
    status               TEXT NOT NULL DEFAULT 'available'
                         CHECK (status IN ('available', 'busy', 'quarantined', 'offboarded')),
    last_heartbeat       TEXT,
    agent_instance_id    TEXT,
    channel_id           TEXT,
    session_id           TEXT,
    metadata             TEXT,
    created_at           TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at           TEXT NOT NULL DEFAULT (datetime('now'))
);
```

---

## Downstream Guidance

### Den Channels (#1769)
- Carry `worker_identity`, `profile_identity`, `worker_role` from Core assignments.
- Use `worker_identity` for lifecycle callbacks (transition, checkpoint, release).
- Use `profile_identity` + `worker_role` for channel routing and display.

### Current gateway/proxy (`den-services/gateway`; legacy Den Gateway #1770 historical)
- On agent check-in, populate `agent_instance_id` on the pool member.
- Carry `worker_identity`, `pool_member_id`, `profile_identity` in dispatch payloads.
- Lifecycle operations use `worker_identity` (or `pool_member_id`).

### Den Hermes Bridge (#1767)
- Populate `session_id` on pool member when session established.
- Use `assignment_id` + `run_id` for checkpoint correlation.
- Use `worker_identity` + `profile_identity` for display/routing.

### Den Web
- Display `profile_identity`, `worker_role`, `worker_identity` in pool member views.
- Filter members by `profile_identity` to show all members sharing a role profile.
- Use `worker_identity` for admin lifecycle actions (quarantine, status change).
