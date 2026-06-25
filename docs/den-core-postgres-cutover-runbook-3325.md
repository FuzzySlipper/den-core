# den-core Postgres quiet-window cutover runbook rehearsal (#3325)

Task: #3325. Parent: #3140. Live cutover task: #3326.

This is an operator runbook and rehearsal record for moving live `den-core`
from SQLite to Postgres. It is not authorization to perform the live cutover.

## Current Verdict

Live cutover remains gated by the dedicated live cutover task #3326 and explicit
Patch approval. The #3365 and #3370 blockers discovered by rehearsal/live
no-go attempts now have repeatable import/startup checks.

For a synthetic fixture:

```bash
DEN_MIGRATION_DATABASE_URL=postgres://... \
  scripts/rehearse-postgres-imported-schema-start.sh
```

The script exercises the non-live sequence: create a temporary SQLite fixture,
backup with `.backup`, import through den-services `den-core-import-parity`,
start Core against `Search Path=den_core`, smoke `/health` and `/api/projects`,
then drop the rehearsal schema. Do not run #3326 unless this rehearsal passes
against the current cutover build and target.

For the live backup copy from the #3326 no-go:

```bash
go run ./cmd/den-core-import-parity \
  --source-sqlite /data/services/den-core/backups/postgres-cutover-20260625T110733Z/den.db \
  --postgres-url "$DEN_MIGRATION_DATABASE_URL" \
  --apply-migrations \
  --reset-target

DenCore__Provider=Postgres \
DenCore__ConnectionString="...;Search Path=den_core" \
  /data/services/den-core/app/DenMcp.Server --port 5598

curl -fsS http://127.0.0.1:5598/health
curl -fsS http://127.0.0.1:5598/api/projects
```

#3370 corrected the import schema to keep Phase 0 timestamp columns compatible
with current Core string-based readers and to preserve checkpoint payloads as
text. The live backup copy import/parity and alternate-port Core startup smoke
must pass before any new #3326 retry window.

## Verified Topology

Read-only checks on `den-srv` on 2026-06-25 observed:

```text
den-core.service: active/running, 127.0.0.1:5299
den-mcp.service: active/running, 0.0.0.0:5199
den-channels.service: active/running, 0.0.0.0:18080
postgres: 127.0.0.1:5433 and 192.168.1.10:5433
```

Service metadata:

```text
den-core.service
  EnvironmentFiles=/data/services/den-core/env/server.env
  WorkingDirectory=/data/services/den-core/app
  ExecStart=/data/services/den-core/app/DenMcp.Server
  User=den-core
  Group=den-core
  SupplementaryGroups=den-data den-pi-docker

den-mcp.service
  EnvironmentFiles=/data/services/den-mcp/env/server.env
  WorkingDirectory=/data/services/den-mcp/app

den-channels.service
  EnvironmentFiles=/data/services/den-channels/env/server.env
  WorkingDirectory=/data/services/den-channels/app
```

Health observations:

```text
curl http://127.0.0.1:5299/health
# healthy, commit 39f33c4c1871

curl http://127.0.0.1:5199/health
# healthy facade with healthy den_core sub-object

curl http://127.0.0.1:18080/health/ready
# returned SPA HTML fallback during rehearsal; verify the current Channels
# health endpoint before relying on it as a cutover gate.
```

Operator tools present on `den-srv`:

```text
/usr/bin/sqlite3
/usr/bin/sha256sum
/usr/bin/psql
/usr/bin/curl
/usr/bin/systemctl
```

Non-interactive `sudo` was not available from the rehearsal agent account, so
live DB backup/fingerprint commands below require an operator account with
appropriate sudo rights.

## Required Artifacts

- Reviewed/deployed den-core build containing #3322-#3324.
- `/home/dev/den-services/migration/cmd/den-core-import-parity`.
- `/home/dev/den-services/migration/postgres/den_core/001_initial.sql`.
- `/home/dev/den-services/migration/postgres/den_core/002_align_current_core_tables.sql`.
- Postgres migration URL from the approved env/secret source.
- Live SQLite path: `/data/services/den-core/data/den.db`.
- Live Core env: `/data/services/den-core/env/server.env`.
- Live service units: `den-core.service`, `den-mcp.service`, `den-channels.service`.

## Quiet-Window Preconditions

Do not start unless all are true:

- #3365 is done and the combined import plus Core-start rehearsal passes.
- Patch explicitly authorizes live cutover.
- Active agents are drained or paused.
- Operators have direct SSH/server access to `den-srv`; do not depend on
  Gateway, Channels workers, or agent wake paths to perform the cutover.
- An operator account has non-interactive sudo for systemctl, SQLite backup,
  env edit/install, and checksum commands.
- Current source/build commit, rollback commit, and env file backup path are
  recorded in the task thread.
- Postgres target is confirmed and `den_core` is either absent or intentionally
  resettable for the cutover.

## Go/No-Go Gates

Go only if:

- SQLite backup exists, passes `PRAGMA integrity_check`, and has recorded size
  plus SHA-256.
- Import/parity report exits 0.
- Every imported/common table reports `status=ok`.
- There are no unexplained count, checksum, ID range, JSON parse, timestamp
  parse, or FK anomaly differences.
- Core starts against Postgres and `/health` returns healthy.
- MCP smoke checklist passes through the real tool surface.
- Logs show no repeated DB exceptions during the soak window.

No-go and rollback if:

- Backup cannot be produced or verified.
- Import/parity exits non-zero.
- Core cannot start against Postgres.
- Basic project/task/message/document/knowledge/worker status tools fail.
- Any write smoke succeeds in one provider but is absent in the other during
  rollback verification.

## Live Cutover Commands

Run from a direct operator shell. These commands intentionally stop public
write paths before backup/import.

### 1. Announce And Freeze

```bash
ssh den-srv

sudo systemctl stop den-mcp.service
sudo systemctl stop den-channels.service
sudo systemctl stop den-core.service

systemctl is-active den-core.service den-mcp.service den-channels.service || true
```

### 2. Backup SQLite

```bash
ts=$(date -u +%Y%m%dT%H%M%SZ)
backup_dir=/data/services/den-core/backups/postgres-cutover-$ts
sudo install -d -m 0750 -o den-core -g den-core "$backup_dir"

sudo -u den-core sqlite3 /data/services/den-core/data/den.db \
  ".backup '$backup_dir/den.db'"

sudo -u den-core sqlite3 "$backup_dir/den.db" 'PRAGMA integrity_check;'
sudo stat -c 'backup_size=%s' "$backup_dir/den.db"
sudo sha256sum "$backup_dir/den.db" | sudo tee "$backup_dir/den.db.sha256"
sudo cp /data/services/den-core/env/server.env "$backup_dir/server.env.pre-postgres"
```

Expected:

```text
ok
backup_size=<nonzero>
<sha256>  <backup path>/den.db
```

### 3. Confirm Postgres Target

Do not paste secrets into the task thread.

```bash
cd /home/dev/den-services/migration
set -a
. /path/to/approved/postgres/env
set +a

psql "$DEN_MIGRATION_DATABASE_URL" -Atqc \
  "SELECT current_database(), current_user, inet_server_addr(), inet_server_port();
   SELECT count(*) FROM information_schema.schemata WHERE schema_name='den_core';"
```

If `den_core` exists unexpectedly, stop and inspect. Do not pass
`--reset-target` unless the operator has confirmed it is the intended cutover
target.

### 4. Import And Parity Check

```bash
cd /home/dev/den-services/migration

go run ./cmd/den-core-import-parity \
  --source-sqlite "$backup_dir/den.db" \
  --postgres-url "$DEN_MIGRATION_DATABASE_URL" \
  --apply-migrations \
  --reset-target \
  --timeout 20m | tee "$backup_dir/import-parity.txt"
```

Expected:

```text
applied_migrations=den_core/001, den_core/002
...
<all tables> status ok
```

### 5. Configure Core For Postgres

The Core runtime must connect with `Search Path=den_core` unless #3365 changes
the authoritative sequence.

Edit `/data/services/den-core/env/server.env` using the approved secret source:

```text
DenCore__Provider=Postgres
DenCore__ConnectionString=Host=127.0.0.1;Port=5433;Database=denservices;Username=<role>;Password=<secret>;Search Path=den_core
DenCore__ListenUrl=http://127.0.0.1:5299
```

Keep `DenCore__DatabasePath=/data/services/den-core/data/den.db` in the env
file during the soak so rollback remains explicit.

### 6. Start Core And Smoke HTTP

```bash
sudo systemctl start den-core.service
sudo journalctl -u den-core.service -n 120 --no-pager

curl -fsS http://127.0.0.1:5299/health && echo
curl -fsS http://127.0.0.1:5299/api/projects | head -c 1000 && echo
```

Do not start `den-mcp.service` or `den-channels.service` until Core health and
basic API reads pass.

### 7. Restart Facade/UI

```bash
sudo systemctl start den-mcp.service
sudo systemctl start den-channels.service

curl -fsS http://127.0.0.1:5199/health && echo
curl -fsS http://192.168.1.10:5199/health && echo
```

Verify the current Channels health endpoint before using it as a hard gate.

## MCP Smoke Checklist

Run these through the real MCP facade/tool surface. Prefer a direct operator
session; do not depend on Gateway/Channels worker automation.

Read-only checks:

- `get_project(project_id="den-core", agent="cutover-smoke")`
- `list_projects()`
- `get_task(task_id=3326, verbose=true)`
- `list_tasks(project_id="den-core", status="planned,in_progress,review", verbose=false)`
- `get_messages(project_id="den-core", task_id=3326, limit=5)`
- `list_documents(project_id="den-core", verbose=false)`
- `search_documents(project_id="den-core", query="postgres OR cutover")`
- `den_knowledge_search(query="postgres cutover", limit=5)`
- `den_knowledge_guide(question="What is the den-core Postgres cutover plan?", context_budget=1200)`
- `get_worker_pool_summary()`
- `list_pool_members(limit=20)`
- `get_pool_residency_projection(project_id="den-core")`

Low-risk write/read/delete checks:

- `send_message(project_id="den-core", task_id=3326, sender="cutover-smoke", intent="status_update", content="[cutover smoke] message write/read check")`
- `store_document(project_id="den-core", slug="cutover-smoke-<ts>", title="Cutover Smoke <ts>", doc_type="note", content="Temporary cutover smoke document.")`
- `get_document(project_id="den-core", slug="cutover-smoke-<ts>", verbose=true)`
- `search_documents(project_id="den-core", query="Temporary cutover smoke")`
- `delete_document(project_id="den-core", slug="cutover-smoke-<ts>")`

Go/no-go threshold: every read-only check passes, the message write is visible,
the temporary document can be read/searched/deleted, and no Core DB exceptions
appear in `journalctl -u den-core.service`.

## Rollback

Rollback is allowed at any failed gate or during soak.

```bash
sudo systemctl stop den-mcp.service
sudo systemctl stop den-channels.service
sudo systemctl stop den-core.service

sudo cp "$backup_dir/server.env.pre-postgres" /data/services/den-core/env/server.env
sudo -u den-core sqlite3 "$backup_dir/den.db" 'PRAGMA integrity_check;'
sudo install -m 0640 -o den-core -g den-core "$backup_dir/den.db" \
  /data/services/den-core/data/den.db

sudo systemctl start den-core.service
curl -fsS http://127.0.0.1:5299/health && echo

sudo systemctl start den-mcp.service
sudo systemctl start den-channels.service
curl -fsS http://127.0.0.1:5199/health && echo
```

After rollback:

- Run the MCP smoke checklist again.
- Leave Postgres `den_core` intact for forensic comparison unless it is actively
  harmful.
- Post the rollback reason and do not retry without a targeted fix task.

## Soak

Minimum soak checks:

- `journalctl -u den-core.service -f` during the initial window.
- Repeat read-only MCP smoke after 15 minutes, 1 hour, and next operator check.
- Keep SQLite DB and env backup until Phase 0E explicitly removes SQLite
  compatibility.
- Do not start Phase 0E cleanup until #3326 has completed and the soak has no
  unexplained DB/search/tool failures.

## Rehearsal Log

All rehearsal actions below were non-live: no production cutover, no live
service restart, and no live SQLite mutation.

### Live Read-Only Discovery

Commands:

```bash
ssh den-srv 'systemctl show den-core.service den-mcp.service den-channels.service ...'
ssh den-srv 'sudo -n ss -ltnp | grep -E ":5299|:5199|:18080|:5433"'
ssh den-srv 'curl -fsS http://127.0.0.1:5299/health'
ssh den-srv 'curl -fsS http://127.0.0.1:5199/health'
```

Observed:

- Core/facade/channels services were active.
- Core health returned healthy.
- Facade health returned healthy with healthy Core sub-object.
- Non-interactive sudo was unavailable from the rehearsal account, so live DB
  backup/fingerprint commands require operator sudo.

### Local SQLite Backup Drill

Command:

```bash
sqlite3 "$src" ".backup '$backup'"
sqlite3 "$backup" 'PRAGMA integrity_check;'
stat -c '%s' "$backup"
sha256sum "$backup"
```

Observed:

```text
backup_integrity=ok
backup_size=991232
backup_sha256=c819acc9dc999efc4944a20807e7799db9701171e3fb296600b2be5dc6749d0a
```

### Postgres Provider Harness

Command:

```bash
DEN_CORE_TEST_POSTGRES_CONNECTION_STRING=<redacted> \
  dotnet test tests/DenCore.Tests/DenCore.Tests.csproj \
  -p:NuGetAudit=false \
  --filter "Category=PostgresProvider"
```

Observed:

```text
Passed: 3/3
current_database=denservices
current_user=den_migration
server=192.168.1.10:5433
remaining den_core_test_% schemas=0
```

### Import/Parity Rehearsal

Source: local Core SQLite fixture initialized by `DenCore.Service` in
Development mode, then copied with `.backup`.

Command:

```bash
cd /home/dev/den-services/migration
go run ./cmd/den-core-import-parity \
  --source-sqlite /tmp/.../den-core-fixture-backup.db \
  --postgres-url "$DEN_MIGRATION_DATABASE_URL" \
  --apply-migrations \
  --reset-target
```

Observed:

```text
applied_migrations=den_core/001, den_core/002
projects source/imported/target = 1/1/1 status ok
all listed target tables status ok
```

Cleanup:

```text
DROP SCHEMA IF EXISTS den_core CASCADE;
remaining den_core schemas=0
remaining den_core_test_% schemas=0
```

### Combined Import Plus Core-Start Rehearsal

Command shape:

```bash
DEN_MIGRATION_DATABASE_URL=postgres://... \
  scripts/rehearse-postgres-imported-schema-start.sh
```

Observed:

```text
backup_integrity=ok
backup_tables=62
backup_size=991232
applied_migrations=den_core/001, den_core/002
projects source/imported/target = 1/1/1 status ok
temp_core_health={"status":"healthy",...}
api_projects_http=200
remaining_den_core_schemas=0
remaining_den_core_test_schemas=0
```

Result:

- The imported schema and Core Postgres startup path are compatible for the
  required health/projects smoke.
- The original #3365 failure was caused by Core's document search vector
  assuming text `documents.tags`; den-services imports that column as `jsonb`.
  Core now casts `tags::text` in the Postgres FTS index/search expression.
- Rehearsal schema cleaned up: `den_core=0`, `den_core_test_%=0`.

### Live Cutover No-Go And #3370 Compatibility Rehearsal

The first #3326 live quiet-window attempt on 2026-06-25 was a no-go. Core was
deployed to commit `602e6fd4be9b`, the live SQLite DB was backed up, and
services were stopped for the import window. Backup evidence:

```text
backup_dir=/data/services/den-core/backups/postgres-cutover-20260625T110733Z
backup_integrity=ok
backup_size=1279488000
backup_sha256=edb6fbaab64fa7c2e06ee810eb59d592dd31ce92fdf0683bf6d52513e977adb0
env_backup=server.env.pre-postgres
```

No live Postgres flip was performed. The no-go causes were:

- `worker_checkpoints.payload` and `checkpoint_responses.payload` were modeled
  as `jsonb`, but live historical rows include non-JSON payload text and Core
  treats these payloads as strings.
- The importer timestamp parser did not accept T-separated no-zone timestamps
  such as `2026-04-26T09:43:57.0000000`.
- After those two fixes were staged, row counts matched, but checksum parity
  failed on timestamp/json-heavy tables.
- An alternate-port Core smoke against the imported schema returned healthy
  `/health` but `/api/projects` failed with `InvalidCastException` because Core
  tried to read a Postgres `timestamptz` value as `System.String`.

Rollback restored the unchanged SQLite config and restarted
`den-core.service`, `den-mcp.service`, and `den-channels.service`; Core health,
facade health, raw `/api/projects`, MCP reads, and a low-risk MCP message write
all passed.

#3370 compatibility result against the same live backup copy:

```text
applied_migrations=den_core/001, den_core/002
known_source_exclusions=documents_fts..., notification_message_links, pi_session_events, pi_sessions
all listed target tables status ok
capability_invocations checksum=ok
agent_runs checksum=ok
desktop_diff_snapshots checksum=ok
desktop_git_snapshots checksum=ok
desktop_session_snapshots checksum=ok
worker_checkpoints checksum=ok
```

Alternate-port Core smoke against the imported schema:

```text
compat_health={"status":"healthy","commit":"602e6fd4be9b",...}
compat_api_projects_http=200
compat_api_projects_bytes=6469
compat_log_errors=none
```
