# Phase 1 extraction tracker after Postgres cutover (#3327)

Status: live Core runs on Postgres `den_core` after #3326. This tracker keeps
the temporary monolith schema visible so Phase 1 extraction work does not treat
`den_core` as the permanent ownership boundary.

Source map: Den document `den-core/den-core-schema-boundary-2026-06`.

## Current authority

- Runtime writer: `.NET den-core` process.
- Live schema: `den_core`.
- Live provider: `Postgres`.
- SQLite: rollback/archive artifact and legacy test fixture only.

## Extraction rules

- Extract one domain at a time from `den_core.<table>` to its final
  `den_<domain>` schema.
- Add the den-services writer module and database role before moving writes.
- Cross-domain reads must use versioned views, not raw table grants.
- Cross-domain writes must use the owning service API or the approved
  function/outbox pattern.
- No new Go writer should write `den_core` tables.
- Delete or tombstone Core write paths as each domain moves.

## Wave 1 candidates

These are the lowest-coupling domains from the boundary plan and should be
planned first.

| Target schema | Owner module | Source tables in `den_core` | Notes |
|---|---|---|---|
| `den_knowledge` | `knowledge` | `knowledge_entries`, `knowledge_entry_tags`, `knowledge_entry_revisions`, `knowledge_entry_links`, `knowledge_entries_fts` | Replace FTS shadow behavior with Postgres GIN/search views in the owning schema. |
| `den_topics` | `topics` | `consolidation_topics` | No initial cross-domain readers. |
| `den_curation` | `curation` | `topic_clip_queue_items`, `curation_decisions` | Grants read-only access to topics as needed. |
| `den_blackboard` | `blackboard` | `blackboard_entries` | Small standalone write surface. |
| `den_capability` | `capability` | `capability_definitions`, `capability_invocations` | Capability registry/invocation audit surface. |
| `den_discussions` | `discussions` | `discussion_threads`, `discussion_comments` | Document sidebars read discussions through a view. |

## Later waves

| Wave | Target schemas | Source table families |
|---|---|---|
| 2 | `den_messages`, `den_documents` | messages/read cursors and documents/search. Universal reader contracts must be designed before route tombstones. |
| 3 | `den_agent_runtime`, `den_workspaces`, `den_dispatch`, `den_collaboration` | agent sessions/bindings/stream/runs, workspaces, dispatch entries, collaboration records. |
| 4 | `den_projects`, `den_tasks`, `den_review`, `den_orchestrator` | project/task workflow backbone, review state, worker pool/orchestrator state. |
| 5 | `den_usage_cost`, `den_desktop_snapshots`, `librarian`, `routing`, `attention` | usage/cost, desktop snapshots, and no-schema aggregators that read owned views. |

## Open boundary items

- `agent_guidance_entries` remains unresolved in the boundary plan. Revisit
  during Wave 3 or Wave 4 before extracting projects, documents, or agent
  runtime.
- The `den_outbox` shape is deferred until Wave 2 proves whether per-producer
  outbox schemas or a shared coordination table are needed.
- Test harness cleanup is still pending: many repository/service tests use the
  legacy SQLite fixture even though production validation now fails closed on
  SQLite.

## Next planning tasks to open

- Wave 1 schema planning task: draft `001_initial.sql` for `den_knowledge`,
  `den_topics`, `den_curation`, `den_blackboard`, `den_capability`, and
  `den_discussions`, including roles, grants, and read views.
- Wave 1 module skeleton task set in `den-services` for the same owner modules.
- Den Core route/tool tombstone planning task for the Wave 1 domains.
- Test harness migration task: replace broad SQLite default fixture usage with
  Postgres test schemas, then remove the remaining SQLite package dependency
  from non-test build paths.
