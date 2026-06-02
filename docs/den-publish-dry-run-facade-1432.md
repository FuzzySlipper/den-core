# Den Publish dry-run facade (#1432)

This document describes the first Den-native facade for the standalone `den-publish` service.

## Boundary

Den Core / MCP owns:

- accepting field-level Den/code-gate submission metadata from an orchestrator;
- checking Den state before calling `den-publish`;
- requiring the referenced review round to exist, belong to the task, have verdict `looks_good`, and match the exact submitted head/base commits;
- rejecting unresolved blocking review findings unless they have structured overrides with an override id, reason, approver, and covered finding ids;
- constructing the direct `DenPublish.Api` request as camelCase JSON;
- posting a task-thread audit/status message with the `den-publish` result.

`den-publish` owns:

- service-managed workspaces;
- code-gate fetches;
- remote/policy validation;
- scope/ancestry validation;
- dry-run planning;
- live promotion when separately enabled and approved.

Core must not regain Git workspace/push authority through this facade.

## MCP tool

Tool name:

```text
legacy_request_den_publish_dry_run
```

The public Hermes tool is exposed with the usual MCP prefix, e.g. `mcp_den_legacy_request_den_publish_dry_run` once the Core/MCP server containing this change is deployed.

The tool accepts explicit fields instead of a raw API payload:

- `project_id`
- `task_id`
- `submission_id`
- `worker_run_id`
- `requested_by`
- `submitted_by`
- `code_gate_repo`
- `code_gate_remote_url`
- `ingress_ref`
- `base_branch`
- `base_commit`
- `head_commit`
- `canonical_remote_url`
- `target_branch`
- `review_round_id`
- optional `changed_files_claim`, `allowed_path_prefixes`, `tests_run`, and structured `scope_overrides`

The service builds the canonical camelCase API shape internally. Agents should not hand-write `/promotion/dry-run` JSON after this path is live.

## Fail-closed checks before den-publish call

The facade rejects locally before any HTTP call when:

- project metadata is missing;
- review round is missing;
- review round belongs to another task;
- review verdict is not `looks_good`;
- review head/base does not match the submitted head/base;
- the target branch is not task-scoped;
- a blocking/acceptance review finding is unresolved and lacks a structured override.

## Result recording

A successful or failed `den-publish` HTTP response is recorded as a task-thread message with metadata type:

```text
den_publish_dry_run_result
```

The result includes decision/submission ids, publish status, validation status, fetched head, local managed ref, HTTP status, and success flag.

Preflight rejections are returned to the caller without posting a task-thread message, to avoid polluting task history with malformed requests.

## Configuration

Core reads:

```text
DenCore:DenPublishFacade:Endpoint
```

Default:

```text
http://127.0.0.1:5090
```

## Verification

Initial test coverage:

- camelCase API payload generation and successful result audit;
- missing review fail-closed before HTTP;
- stale review head fail-closed before HTTP;
- unresolved blocking finding fail-closed without structured override.

Run:

```bash
dotnet test tests/DenCore.Service.Tests/DenCore.Service.Tests.csproj --filter DenPublishFacadeServiceTests
dotnet test
```
