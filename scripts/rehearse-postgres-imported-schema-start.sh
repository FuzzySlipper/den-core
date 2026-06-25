#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
DEN_SERVICES_MIGRATION_DIR="${DEN_SERVICES_MIGRATION_DIR:-/home/dev/den-services/migration}"
POSTGRES_URL="${DEN_CORE_MIGRATION_DATABASE_URL:-${DEN_MIGRATION_DATABASE_URL:-${DEN_SERVICES_MIGRATION_DATABASE_URL:-}}}"
EFFECTIVE_POSTGRES_URL=""
SQLITE_PORT="${DEN_CORE_REHEARSAL_SQLITE_PORT:-5599}"
POSTGRES_PORT="${DEN_CORE_REHEARSAL_POSTGRES_PORT:-5598}"
WORK_DIR="$(mktemp -d /tmp/den-core-import-start-rehearsal.XXXXXX)"
CREATED_DEN_CORE_SCHEMA=0

usage() {
  cat <<'EOF_USAGE'
Usage:
  DEN_MIGRATION_DATABASE_URL=postgres://... \
    scripts/rehearse-postgres-imported-schema-start.sh

Creates a local temporary den-core SQLite fixture, backs it up, imports it into
the Postgres den_core schema via den-services' den-core-import-parity tool,
starts Core against that imported schema on a temporary local port, checks
/health and /api/projects, then drops the rehearsal den_core schema.

Safety:
  - Exits if den_core already exists on the target.
  - Does not read or mutate live SQLite.
  - Does not restart live services.
  - Does not print database credentials.

Environment:
  DEN_MIGRATION_DATABASE_URL or DEN_CORE_MIGRATION_DATABASE_URL or
    DEN_SERVICES_MIGRATION_DATABASE_URL must be a postgres:// URL.
  DEN_SERVICES_MIGRATION_DIR defaults to /home/dev/den-services/migration.
EOF_USAGE
}

cleanup() {
  local exit_code=$?
  if [[ "$CREATED_DEN_CORE_SCHEMA" -eq 1 && -n "${PGPASSWORD:-}" ]]; then
    psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -v ON_ERROR_STOP=1 -Atqc \
      "DROP SCHEMA IF EXISTS den_core CASCADE;" >/dev/null 2>&1 || true
  fi
  rm -rf "$WORK_DIR"
  exit "$exit_code"
}
trap cleanup EXIT

require_tool() {
  command -v "$1" >/dev/null || {
    echo "$1 is required" >&2
    exit 1
  }
}

parse_postgres_url() {
  if [[ -z "$POSTGRES_URL" ]]; then
    usage >&2
    exit 1
  fi
  if [[ "$POSTGRES_URL" != postgres://* ]]; then
    echo "Only postgres:// URLs are supported by this rehearsal helper." >&2
    exit 1
  fi

  local tmp="${POSTGRES_URL#postgres://}"
  local creds="${tmp%@*}"
  local hostdb="${tmp#*@}"
  PGUSER="${creds%%:*}"
  PGPASSWORD="${creds#*:}"
  local hostport="${hostdb%%/*}"
  PGDATABASE="${hostdb#*/}"
  PGHOST="${hostport%%:*}"
  PGPORT="${hostport#*:}"

  if [[ "$PGHOST" == "127.0.0.1" ]]; then
    PGHOST="192.168.1.10"
  fi
  EFFECTIVE_POSTGRES_URL="postgres://$PGUSER:$PGPASSWORD@$PGHOST:$PGPORT/$PGDATABASE"

  export PGUSER PGPASSWORD PGDATABASE PGHOST PGPORT EFFECTIVE_POSTGRES_URL
}

assert_clean_target() {
  local existing
  existing="$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -Atqc \
    "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'den_core';")"
  if [[ "$existing" != "0" ]]; then
    echo "Refusing rehearsal because target schema den_core already exists." >&2
    exit 1
  fi
}

initialize_sqlite_fixture() {
  local sqlite_db="$1"
  local log="$2"

  set +e
  ASPNETCORE_ENVIRONMENT=Development timeout 45s \
    dotnet run --project "$REPO_ROOT/src/DenCore.Service/DenCore.Service.csproj" \
      -p:NuGetAudit=false --no-launch-profile -- \
      --db-path "$sqlite_db" --port "$SQLITE_PORT" >"$log" 2>&1
  local code=$?
  set -e

  if [[ "$code" -ne 124 ]]; then
    echo "SQLite fixture startup failed with exit code $code" >&2
    tail -n 80 "$log" >&2 || true
    exit 1
  fi
}

start_core_against_imported_schema() {
  local conn="$1"
  local log="$2"

  ASPNETCORE_ENVIRONMENT=Development \
  DenCore__Provider=Postgres \
  DenCore__ConnectionString="$conn" \
  timeout 45s dotnet run --project "$REPO_ROOT/src/DenCore.Service/DenCore.Service.csproj" \
    -p:NuGetAudit=false --no-launch-profile -- \
    --port "$POSTGRES_PORT" >"$log" 2>&1 &
  local pid=$!

  local ready=0
  for _ in $(seq 1 45); do
    if curl -fsS "http://127.0.0.1:$POSTGRES_PORT/health" >"$WORK_DIR/health.json" 2>/dev/null; then
      ready=1
      break
    fi
    sleep 1
  done

  if [[ "$ready" -ne 1 ]]; then
    echo "Core did not become healthy against imported Postgres schema." >&2
    tail -n 120 "$log" >&2 || true
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    exit 1
  fi

  curl -fsS "http://127.0.0.1:$POSTGRES_PORT/api/projects" >"$WORK_DIR/projects.json"
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
}

main() {
  require_tool dotnet
  require_tool sqlite3
  require_tool psql
  require_tool curl
  require_tool go
  parse_postgres_url
  assert_clean_target

  local sqlite_db="$WORK_DIR/fixture.db"
  local backup_db="$WORK_DIR/fixture-backup.db"
  initialize_sqlite_fixture "$sqlite_db" "$WORK_DIR/sqlite-service.log"
  sqlite3 "$sqlite_db" "PRAGMA integrity_check;"
  sqlite3 "$sqlite_db" ".backup '$backup_db'"
  echo "backup_integrity=$(sqlite3 "$backup_db" "PRAGMA integrity_check;")"
  echo "backup_tables=$(sqlite3 "$backup_db" "SELECT count(*) FROM sqlite_master WHERE type='table';")"
  echo "backup_size=$(stat -c '%s' "$backup_db")"

  (
    cd "$DEN_SERVICES_MIGRATION_DIR"
    DEN_MIGRATION_DATABASE_URL="$EFFECTIVE_POSTGRES_URL" \
      go run ./cmd/den-core-import-parity \
        --source-sqlite "$backup_db" \
        --postgres-url "$EFFECTIVE_POSTGRES_URL" \
        --apply-migrations \
        --reset-target >"$WORK_DIR/import-parity.log"
  )
  CREATED_DEN_CORE_SCHEMA=1
  sed -n '1,18p' "$WORK_DIR/import-parity.log"

  local conn="Host=$PGHOST;Port=$PGPORT;Database=$PGDATABASE;Username=$PGUSER;Password=$PGPASSWORD;Search Path=den_core"
  start_core_against_imported_schema "$conn" "$WORK_DIR/postgres-service.log"
  echo "temp_core_health=$(cat "$WORK_DIR/health.json")"
  echo "api_projects_http=200"

  local den_core_count test_count
  psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -v ON_ERROR_STOP=1 -Atqc \
    "DROP SCHEMA IF EXISTS den_core CASCADE;" >/dev/null
  CREATED_DEN_CORE_SCHEMA=0
  den_core_count="$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -Atqc \
    "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'den_core';")"
  test_count="$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -Atqc \
    "SELECT count(*) FROM information_schema.schemata WHERE schema_name LIKE 'den_core_test_%';")"
  echo "remaining_den_core_schemas=$den_core_count"
  echo "remaining_den_core_test_schemas=$test_count"
}

main "$@"
