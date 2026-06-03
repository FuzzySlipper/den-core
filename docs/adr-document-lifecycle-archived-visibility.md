# ADR: Document Lifecycle — Archived Visibility + Deliberate Historical Recall

**Status:** Accepted (Implemented)  
**Date:** 2026-06-03  
**Task:** #1865

## Context

Den documents accumulate over time. Legacy policy documents, superseded specs, and stale prompts remain permanently visible in default search, librarian queries, and agent guidance resolution. There is no mechanism to retire a document from active use without deleting it, which loses audit history and makes recovery impossible.

Spaces already support `normal | hidden | archived` visibility. Documents need the same treatment.

## Decision

### 1. Document visibility enum

Documents carry a `visibility` column matching the spaces-aligned enum: `normal | hidden | archived`. Existing documents default to `normal`.

### 2. Reversible archive/unarchive

- `update_document_visibility` MCP tool flips a document's status. Archiving is a status change, not data movement.
- `UpdateVisibilityAsync` on the repository returns the updated document.
- `GetAsync` returns documents regardless of visibility — direct fetch by slug always works.

### 3. Archive preflight

- `archive_document_preflight` checks for active references before archiving.
- Currently detects `agent_guidance_entries` referencing the document.
- Returns `can_archive: false` with `referenced_by` details when references exist.
- Callers should prefer refusing the archive unless forced or the references are explicitly handled.

### 4. Default exclusion

- `list_documents` (both MCP and REST) excludes archived documents by default.
- `search_documents` excludes archived documents from FTS results.
- `query_librarian` (via `LibrarianGatherer.SearchDocsSafe` → `SearchAsync`) excludes archived.
- `get_agent_guidance` / `ResolveAsync` excludes archived documents from resolved guidance content.
- Hidden documents are excluded from default list/search (only returned when `visibility=hidden` filter is explicit).

### 5. Deliberate archived recall

- `query_archived_documents` MCP tool provides a separate recall path for archived documents.
- `ListArchivedAsync` and `SearchArchivedAsync` on the repository.
- REST routes: `GET /api/projects/{id}/documents/archived` and `GET /api/projects/{id}/documents/archived/search`.
- These are intentionally separate from the primary hot paths — no `include_archived` flag on the main list/search.

### 6. Legacy global docs archived

The following four `_global` legacy documents are archived as part of this change:
- `pi-local-worker-source-policy`
- `pi-coder-subagent-prompt-default`
- `pi-reviewer-subagent-prompt-default`
- `pi-orchestrator-guidance-default`

They disappear from default search, librarian, and guidance but are recoverable via `query_archived_documents`.

## Deferred

- **Nightly idle-curation loop**: Automatically archive documents with no access for N days. Deferred to a follow-up task.
- **Cross-document link reference detection in preflight**: Only agent guidance entries are currently detected.

## Implementation Notes

- Migration adds `visibility TEXT NOT NULL DEFAULT 'normal' CHECK (visibility IN ('normal', 'hidden', 'archived'))` to the `documents` table via `TryAddColumnAsync`.
- FTS `documents_fts` is not filtered — filtering happens at the JOIN level (`d.visibility != 'archived'`), so archived documents remain FTS-indexed for archived recall.
- All changes are in den-core. den-mcp is an MCP-facing adapter only.
