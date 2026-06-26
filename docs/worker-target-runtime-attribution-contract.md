# Worker Target-vs-Runtime Attribution Contract

**Status:** Active — den-core #1844
**Cross-references:** den-channels #1845 (complete), legacy den-gateway #1846 (historical; current gateway successor is `den-services/gateway`), den-hermes-bridge #1847, den-hermes-bridge #1842

> **Gateway retirement note (2026-06):** this contract predates the `den-gateway` decommission. References to Gateway delivery/wake/claim behavior describe the transport layer contract, but new implementation work belongs in `den-services/gateway`, not the retired `den-gateway` project.

---

## Problem

The GoblinBench pooled-orchestrator retry showed a boundary leak:
- The project orchestrator correctly reports parent orchestration into `goblinbench`.
- Role workers can be woken through shared control conversations/runtime bindings that are operationally owned by `den-hermes-bridge`.
- Completion packets and task status belong to the target project (`goblinbench`), but wake/delivery/control events can still look like Hermes Bridge work.

This confuses orchestrators, cleanup logic, and operator dashboards. The system needs to say clearly: Hermes Bridge is the runtime adapter/bus depot, not the job site.

## Ownership model

| Layer | Owner | Durable truth scope |
|-------|-------|-------------------|
| Tasks, assignments, worker runs, leases, checkpoints, blocker notifications, completion packets | Den Core | Target project/task/assignment/run attribution |
| Message/conversation storage, memberships, event projections, source/target attribution on messages/events | Den Channels (#1845) | Channel project + structured target-work fields |
| Delivery attempts, wake/claim evidence, adapter bindings, routing state, echo suppression, delivery health | Current gateway/proxy (`den-services/gateway`; legacy #1846 is historical) | Transport/routing attribution |
| Hermes profile/session/process mechanics, runtime status | Den Hermes Bridge (#1847) | Runtime/control project attribution |

## Required attribution fields

### Target-work attribution (Core-owned)

These fields identify the **work being done**. They belong to the target project and must not be inferred from channel or runtime metadata alone.

| Field | Core model | Type | Description |
|-------|-----------|------|-------------|
| `project_id` | `WorkerAssignment`, `AgentRunRecord`, `ProjectTask`, `Message`, `BlockedTaskEscalation` | `string` | Target project owning the work |
| `task_id` | `WorkerAssignment`, `AgentRunRecord`, `ProjectTask`, `Message`, `BlockedTaskEscalation` | `int?` | Target task within the project |
| `assignment_id` (id) | `WorkerAssignment` | `int` | Core assignment identity |
| `run_id` | `WorkerAssignment`, `AgentRunRecord`, `WorkerCheckpoint`, `SubagentRunSummary` | `string` | Worker run identity |
| `role` | `WorkerAssignment`, `AgentRunRecord`, `SubagentRunSummary` | `string` | Worker role (coder, reviewer, etc.) |

### Worker identity attribution (Core-owned)

These fields identify the **worker performing the work**, independent of transport.

| Field | Core model | Type | Description |
|-------|-----------|------|-------------|
| `worker_identity` | `WorkerPoolMember`, `WorkerAssignment` | `string` | Concrete pool member identity |
| `profile_identity` | `WorkerPoolMember`, `WorkerAssignment` | `string` | Shared profile identity (e.g. `spawned-coder`) |
| `worker_role` | `WorkerPoolMember`, `WorkerAssignment` | `string?` | Role category separate from assignment role |

### Transport/control attribution (Gateway/Channels/Bridge-owned)

These fields identify the **transport channel** through which the worker was reached. They do NOT imply ownership of the target work.

| Field | Core model | Type | Description |
|-------|-----------|------|-------------|
| `channel_id` | `WorkerPoolMember`, `WorkerAssignment` | `string?` | Transport channel correlation |
| `session_id` | `WorkerPoolMember`, `OrchestratorLease` | `string?` | Runtime session correlation |
| `agent_instance_id` | `WorkerPoolMember`, `WorkerAssignment`, `OrchestratorLease` | `string?` | Gateway binding correlation |
| `adapter_instance_id` | `WorkerPoolMember`, `OrchestratorLease` | `string?` | Adapter routing correlation |

**No `runtime_project_id` field exists in Core models.** The runtime/control project attribution lives in Gateway/Channels/Bridge metadata. Core records carry the target project; transport context flows through correlation fields.

## Invariants

1. **Target project/task/assignment/run are workflow attribution and must not be inferred only from channel project.**
   - A worker woke via `den-hermes-bridge` channel must still attribute work to the target project (e.g. `goblinbench`).
   - Downstream services must read `project_id`/`task_id` from Core assignment/run records, not from `channel.sourceProjectId`.

2. **Runtime/control project is transport attribution and must not imply ownership of target work.**
   - `den-hermes-bridge` is the runtime adapter/bus depot, not the job site.
   - Transport `channel_id` on `WorkerAssignment` is for correlation only.

3. **Completion packets belong in the target project/task thread.**
   - `WorkerCheckpoint` payloads carry `run_id` and `assignment_id` linking back to the target project.
   - `SubagentRunSummary.ProjectId` is the target project.

4. **Gateway wake/claim evidence should carry target metadata when available, even if the transport conversation is shared.**
   - Gateway (#1846) should read Core assignment projections for target project/task context.

5. **Orchestrators must prefer run-id-scoped Core readback over immediate direct-message timeout when classifying no-claim/no-progress.**
   - `GET /api/worker-pool/assignments/by-run/{runId}` provides authoritative assignment state.

6. **Bridge may log/runtime-report profile/session details, but user-facing work evidence should remain Den-facing: worker role, assignment, run, target project/task.**
   - Bridge (#1847) should project Den Core fields, not its own runtime project, in completion/progress reports.

## Core projection audit

### WorkerAssignment

Fields exposed: `id`, `worker_identity`, `pool_member_id`, `profile_identity`, `worker_role`, `agent_instance_id`, `channel_id`, `run_id`, `lease_id`, `project_id`, `task_id`, `role`, `lease_kind`, `assigned_by`, `state`, `latest_checkpoint_id`, `cleanup_evidence`, `cleanup_recorded_at`, `acquired_at`, `released_at`, `created_at`, `updated_at`.

**Audit result:** Target attribution is explicit (`project_id`, `task_id`, `role`, `run_id`). Transport correlation is present but clearly labeled (`channel_id`, `agent_instance_id`). No ambiguity.

### AgentRunRecord

Fields exposed: `run_id`, `project_id`, `task_id`, `review_round_id`, `workspace_id`, `role`, `backend`, `model`, `sender_instance_id`, `state`, timing fields, exit code, artifact paths.

**Audit result:** Target attribution is explicit. `sender_instance_id` is transport correlation. No `runtime_project_id`. No ambiguity.

### WorkerCheckpoint

Fields exposed: `id`, `assignment_id`, `run_id`, `checkpoint_type`, `payload`, `created_at`.

**Audit result:** Target attribution flows through `assignment_id` → `WorkerAssignment.project_id/task_id`. `run_id` links to the worker run. No direct target fields on the checkpoint itself — this is correct since the assignment is the source of truth. Checkpoint payloads (JSON) should include target project/task for downstream consumer convenience, but this is a payload convention, not a schema requirement.

### SubagentRunSummary

Fields exposed: `run_id`, `state`, `role`, `task_id`, `project_id`, `backend`, `model`, `branch`, `head_commit`, etc.

**Audit result:** `project_id` and `task_id` are derived from `AgentRunRecord` or stream entry metadata. These are target-project fields. The `project_id` query parameter on list/get routes filters by target project. No ambiguity.

### BlockedTaskEscalation

Fields exposed: `task_id`, `project_id`, `blocker_summary`, `reason`, `attempted_remedies`, `suggested_next_step`, `requires_human_input`, `changed_by`, `created_at`.

**Audit result:** Target attribution is explicit. `project_id` is the target project. `changed_by` is the agent that marked it blocked. No runtime/transport fields. No ambiguity.

### NotificationFeedItem / Message

Fields exposed: `id`, `project_id`, `task_id`, `thread_id`, `sender`, `content`, `metadata`, `urgency`, `is_read`, `created_at`.

**Audit result:** `project_id` is the target project the notification belongs to. `sender` is the agent identity. The `metadata` JSON can carry `agent_work_complete` with `project_ids`, `task_ids`, `run_ids` — all target-work references. No ambiguity.

### EventOutboxItem

Fields exposed: `cursor`, `event_type`, `source_kind`, `source_id`, `source_project_id`, `title`, `summary`, `actor`, `severity`, `deep_link`, `dedupe_key`, `occurred_at`, `metadata`.

**Audit result:** `source_project_id` comes from `agent_stream_entries.project_id`, which is the target project on the stream entry. No runtime project field. The event outbox already consumes Core's target project. Downstream consumers (Channels #1845) read `source_project_id` directly. No ambiguity.

### PoolResidencyProjection

Fields exposed: `worker_identity`, `profile_identity`, `worker_role`, `residency_kind`, `project_id`, `channel_id`, `task_id`, `state`, `started_at`, `expires_at`.

**Audit result:** `project_id` is the target project the residency is bound to. `channel_id` is transport correlation. `residency_kind` distinguishes channel_member, gateway_binding, orchestrator_lease, dedicated_agent, task_worker_assignment. No ambiguity.

### OrchestratorLease

Fields exposed: `id`, `lease_id`, `lease_kind`, `scope_type`, `project_id`, `channel_id`, `task_id`, `workstream_handle`, `objective`, `lease_owner`, `orchestrator_identity`, `profile_identity`, `display_name`, `capability_metadata`, `state`, `agent_instance_id`, `adapter_instance_id`, `session_id`, `run_id`, timing fields, etc.

**Audit result:** `project_id` is the target project. Transport fields are clearly labeled (`agent_instance_id`, `adapter_instance_id`, `session_id`, `channel_id`). No ambiguity.

## Downstream consumption guidance

### For Channels (#1845 — complete)

Channels added structured target-work fields to direct-agent wake/message/event DTOs. Consumption rules:

1. Read `source_project_id` from Core `EventOutboxItem` for event attribution.
2. For wake/delivery events, read target project from the Core assignment record via `GET /api/worker-pool/assignments/by-run/{runId}`, not from `channel.sourceProjectId`.
3. Worker identity fields (`worker_identity`, `profile_identity`) come from Core pool member records.

### For current gateway/proxy (`den-services/gateway`; legacy #1846 historical)

1. On wake/claim, read Core assignment projection for target `project_id`, `task_id`, `run_id`.
2. Carry target metadata in dispatch payloads even when transport conversation is shared.
3. Use `worker_identity` (not `profile_identity`) for delivery routing.

### For Hermes Bridge (#1847)

1. Completion packets must include target `project_id`, `task_id`, `assignment_id` from the Core assignment.
2. Runtime/control project (`den-hermes-bridge`) is the transport layer. Do not project it as the work owner.
3. Report status using Den-facing fields: `role`, `assignment_id`, `run_id`, target `project_id/task_id`.

### For orchestrators

1. Prefer `GET /api/worker-pool/assignments/by-run/{runId}` for authoritative run state over direct-message timeout.
2. Classify no-claim/no-progress from Core state, not from channel delivery metadata.
3. Pool residency projection (`GET /api/worker-pool/residency/{projectId}`) provides a unified view of all active workers/orchestrators for a target project.

## Follow-up tasks

| Task | Service | Status |
|------|---------|--------|
| #1844 Core contract doc + audit + tests | den-core | This task |
| #1845 Structured target-work fields in Channels DTOs | den-channels | Complete at ad795a83 |
| #1846 Gateway wake/claim target metadata | legacy `den-gateway` task; re-file/continue current implementation in `den-services/gateway` | Historical/pending successor routing |
| #1847 Bridge runtime/target separation | den-hermes-bridge | Pending |
| #1842 Bridge completion packet target fields | den-hermes-bridge | Related |
