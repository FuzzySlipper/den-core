#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_FILE="${PROJECT_FILE:-$REPO_ROOT/src/DenMcp.Server/DenMcp.Server.csproj}"
PUBLISH_DIR="${PUBLISH_DIR:-}"
BUILD_ARTIFACTS_DIR="${BUILD_ARTIFACTS_DIR:-}"
DEPLOY_MODE="${DEPLOY_MODE:-auto}"
SSH_TARGET="${SSH_TARGET:-den-srv}"
SERVICE_NAME="${SERVICE_NAME:-den-core.service}"
REMOTE_SERVICE_ROOT="${REMOTE_SERVICE_ROOT:-/data/services/den-core}"
REMOTE_APP_DIR="${REMOTE_APP_DIR:-$REMOTE_SERVICE_ROOT/app}"
REMOTE_STAGE_DIR="${REMOTE_STAGE_DIR:-/tmp/den-core-live-publish}"
REMOTE_SERVICE_USER="${REMOTE_SERVICE_USER:-den-core}"
REMOTE_SERVICE_GROUP="${REMOTE_SERVICE_GROUP:-den-core}"
HEALTH_URL="${HEALTH_URL:-http://192.168.1.10:18080/den-core-api/health}"
MCP_CORE_URL="${MCP_CORE_URL:-http://192.168.1.10:18080/den-core-api/mcp}"
MCP_LAN_URL="${MCP_LAN_URL:-http://192.168.1.10:5199/mcp}"
SKIP_RESTART=0
SKIP_SMOKE=0
DRY_RUN=0
TEMP_PUBLISH_DIR_CREATED=0
TEMP_BUILD_ARTIFACTS_DIR_CREATED=0

usage() {
  cat <<'EOF_USAGE'
Usage: scripts/deploy-live-server.sh [options]

Build and publish Den Core's DenMcp.Server, stage it into the live
/data/services/den-core/app tree, restart den-core.service, and run small live
smoke checks for /health plus MCP tools/list through both the Core proxy and the
LAN MCP facade.

Modes:
  local   Run on den-srv from /data/dev/den-core and install directly.
  remote  Run from a workstation/agent host and upload to SSH_TARGET first.

DEPLOY_MODE defaults to auto. Auto selects local when this repo appears to be
running from /data/dev/den-core on den-srv, otherwise remote.

Trust boundary notes:
  /home/dev/den-core is the normal local source checkout for Runner work.
  /data/services/den-core/app is the live app tree.
  /data/dev/den-core can be stale unless this deployment path explicitly updates
  or runs from that checkout.

Do not run this script itself with sudo. In local mode it uses non-interactive
sudo internally for install/restart steps. In remote mode it uses SSH plus
remote sudo.

Options:
  --local          Force local den-srv deployment mode
  --remote         Force remote SSH deployment mode
  --skip-restart   Publish/stage/swap files but do not restart systemd service
  --skip-smoke     Do not run HTTP/MCP smoke checks after deploy
  --dry-run        Print resolved configuration and validate mode; no build/upload/install
  -h, --help       Show this help

Environment overrides:
  DEPLOY_MODE, SSH_TARGET, SERVICE_NAME, PROJECT_FILE, PUBLISH_DIR,
  BUILD_ARTIFACTS_DIR, REMOTE_SERVICE_ROOT, REMOTE_APP_DIR, REMOTE_STAGE_DIR,
  REMOTE_SERVICE_USER, REMOTE_SERVICE_GROUP, HEALTH_URL, MCP_CORE_URL,
  MCP_LAN_URL

Live defaults:
  REMOTE_SERVICE_ROOT=/data/services/den-core
  REMOTE_APP_DIR=/data/services/den-core/app
  SERVICE_NAME=den-core.service
  HEALTH_URL=http://192.168.1.10:18080/den-core-api/health
  MCP_CORE_URL=http://192.168.1.10:18080/den-core-api/mcp
  MCP_LAN_URL=http://192.168.1.10:5199/mcp
EOF_USAGE
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --local)
        DEPLOY_MODE=local
        ;;
      --remote)
        DEPLOY_MODE=remote
        ;;
      --skip-restart)
        SKIP_RESTART=1
        ;;
      --skip-smoke)
        SKIP_SMOKE=1
        ;;
      --dry-run)
        DRY_RUN=1
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        echo "Unknown argument: $1" >&2
        usage >&2
        exit 1
        ;;
    esac
    shift
  done
}

require_non_root() {
  if [[ ${EUID:-$(id -u)} -eq 0 ]]; then
    echo "Run this script as your normal user, not with sudo." >&2
    echo "The script performs privileged install/restart steps internally." >&2
    exit 1
  fi
}

resolve_deploy_mode() {
  case "$DEPLOY_MODE" in
    local|remote)
      ;;
    auto)
      if [[ "$REPO_ROOT" == /data/dev/den-core* ]] && [[ -d /data/services/den-core ]]; then
        DEPLOY_MODE=local
      else
        DEPLOY_MODE=remote
      fi
      ;;
    *)
      echo "Invalid DEPLOY_MODE: $DEPLOY_MODE (expected auto, local, or remote)" >&2
      exit 1
      ;;
  esac

  echo "Deploy mode: $DEPLOY_MODE"
}

print_config() {
  cat <<EOF_CONFIG
Resolved deploy configuration:
  REPO_ROOT=$REPO_ROOT
  PROJECT_FILE=$PROJECT_FILE
  BUILD_ARTIFACTS_DIR=${BUILD_ARTIFACTS_DIR:-<temporary>}
  PUBLISH_DIR=${PUBLISH_DIR:-<temporary>}
  DEPLOY_MODE=$DEPLOY_MODE
  SSH_TARGET=$SSH_TARGET
  SERVICE_NAME=$SERVICE_NAME
  REMOTE_SERVICE_ROOT=$REMOTE_SERVICE_ROOT
  REMOTE_APP_DIR=$REMOTE_APP_DIR
  REMOTE_STAGE_DIR=$REMOTE_STAGE_DIR
  REMOTE_SERVICE_USER=$REMOTE_SERVICE_USER
  REMOTE_SERVICE_GROUP=$REMOTE_SERVICE_GROUP
  HEALTH_URL=$HEALTH_URL
  MCP_CORE_URL=$MCP_CORE_URL
  MCP_LAN_URL=$MCP_LAN_URL
  SKIP_RESTART=$SKIP_RESTART
  SKIP_SMOKE=$SKIP_SMOKE
EOF_CONFIG
}

preflight_tools() {
  command -v dotnet >/dev/null || { echo "dotnet is required" >&2; exit 1; }
  command -v rsync >/dev/null || { echo "rsync is required" >&2; exit 1; }
  command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }
  command -v python3 >/dev/null || { echo "python3 is required for MCP smoke checks" >&2; exit 1; }
  if [[ "$DEPLOY_MODE" == "remote" ]]; then
    command -v ssh >/dev/null || { echo "ssh is required for remote mode" >&2; exit 1; }
  fi
  [[ -f "$PROJECT_FILE" ]] || { echo "Project file not found: $PROJECT_FILE" >&2; exit 1; }
}

shell_quote() {
  printf '%q' "$1"
}

preflight_privilege() {
  if [[ "$DEPLOY_MODE" == "local" ]]; then
    if ! sudo -n true 2>/dev/null; then
      cat >&2 <<EOF
Deploy preflight failed: local mode requires non-interactive sudo for install and restart steps.
Run in remote mode from an account that can SSH to den-srv and sudo there, or install a narrow sudoers rule.
EOF
      exit 1
    fi
  else
    if ! ssh "$SSH_TARGET" 'sudo -n true' 2>/dev/null; then
      cat >&2 <<EOF
Deploy preflight failed: remote mode requires SSH to $SSH_TARGET and non-interactive sudo on the remote host.
EOF
      exit 1
    fi
  fi
}

initialize_publish_dir() {
  if [[ -n "$PUBLISH_DIR" ]]; then
    rm -rf "$PUBLISH_DIR"
    mkdir -p "$PUBLISH_DIR"
    return
  fi

  PUBLISH_DIR="$(mktemp -d /tmp/den-core-live-publish.XXXXXX)"
  TEMP_PUBLISH_DIR_CREATED=1
}

initialize_build_artifacts_dir() {
  # Keep MSBuild/Razor intermediates out of the checkout. Stale obj/bin files
  # from a different user or sudo build are a common deploy footgun.
  if [[ -n "$BUILD_ARTIFACTS_DIR" ]]; then
    rm -rf "$BUILD_ARTIFACTS_DIR"
    mkdir -p "$BUILD_ARTIFACTS_DIR"
    return
  fi

  BUILD_ARTIFACTS_DIR="$(mktemp -d /tmp/den-core-live-artifacts.XXXXXX)"
  TEMP_BUILD_ARTIFACTS_DIR_CREATED=1
}

cleanup() {
  if [[ "$TEMP_PUBLISH_DIR_CREATED" -eq 1 && -n "$PUBLISH_DIR" ]]; then
    rm -rf "$PUBLISH_DIR"
  fi
  if [[ "$TEMP_BUILD_ARTIFACTS_DIR_CREATED" -eq 1 && -n "$BUILD_ARTIFACTS_DIR" ]]; then
    rm -rf "$BUILD_ARTIFACTS_DIR"
  fi
}

publish_server() {
  echo "Publishing Den Core server ..."
  env \
    GIT_CONFIG_COUNT="${GIT_CONFIG_COUNT:-1}" \
    GIT_CONFIG_KEY_0="${GIT_CONFIG_KEY_0:-safe.directory}" \
    GIT_CONFIG_VALUE_0="${GIT_CONFIG_VALUE_0:-$REPO_ROOT}" \
    dotnet publish "$PROJECT_FILE" \
      -c Release \
      -r linux-x64 \
      --self-contained \
      --artifacts-path "$BUILD_ARTIFACTS_DIR" \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -o "$PUBLISH_DIR/"

  [[ -x "$PUBLISH_DIR/DenMcp.Server" ]] || { echo "Publish output missing DenMcp.Server executable" >&2; exit 1; }
}

sudo_local() {
  sudo -n "$@"
}

remote_install_script() {
  cat <<'EOF_REMOTE'
set -euo pipefail
: "${REMOTE_SERVICE_ROOT:?}"
: "${REMOTE_APP_DIR:?}"
: "${REMOTE_STAGE_DIR:?}"
: "${SERVICE_NAME:?}"
: "${REMOTE_SERVICE_USER:?}"
: "${REMOTE_SERVICE_GROUP:?}"
: "${SKIP_RESTART:?}"

publish_stage="$REMOTE_STAGE_DIR/publish"
new_app="$REMOTE_SERVICE_ROOT/app.new"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_app="$REMOTE_SERVICE_ROOT/app.previous.$timestamp"
failed_app="$REMOTE_SERVICE_ROOT/app.failed.$timestamp"

if [[ ! -f "$publish_stage/DenMcp.Server" ]]; then
  echo "Remote stage is missing DenMcp.Server: $publish_stage" >&2
  exit 1
fi

sudo -n install -d -o "$REMOTE_SERVICE_USER" -g "$REMOTE_SERVICE_GROUP" -m 0750 "$REMOTE_SERVICE_ROOT"
sudo -n rm -rf "$new_app"
sudo -n install -d -o "$REMOTE_SERVICE_USER" -g "$REMOTE_SERVICE_GROUP" -m 0750 "$new_app"
sudo -n rsync -a --delete --chown="$REMOTE_SERVICE_USER:$REMOTE_SERVICE_GROUP" \
  --exclude 'env/' \
  --exclude 'data/' \
  --exclude '.net/' \
  --exclude 'appsettings.json' \
  --exclude 'appsettings.Development.json' \
  "$publish_stage/" "$new_app/"

if sudo -n test -d "$REMOTE_APP_DIR"; then
  sudo -n mv "$REMOTE_APP_DIR" "$backup_app"
fi
sudo -n mv "$new_app" "$REMOTE_APP_DIR"

if [[ "$SKIP_RESTART" -eq 1 ]]; then
  echo "Installed new app tree at $REMOTE_APP_DIR; skipping service restart. Backup: ${backup_app:-none}"
  sudo -n rm -rf "$REMOTE_STAGE_DIR"
  exit 0
fi

if sudo -n systemctl restart "$SERVICE_NAME"; then
  sudo -n systemctl --no-pager --full status "$SERVICE_NAME" --lines=20
  echo "Restarted $SERVICE_NAME successfully. Backup: ${backup_app:-none}"
  sudo -n rm -rf "$REMOTE_STAGE_DIR"
  exit 0
fi

echo "Restart failed; rolling back to previous app tree." >&2
sudo -n systemctl stop "$SERVICE_NAME" || true
sudo -n mv "$REMOTE_APP_DIR" "$failed_app" || true
if [[ -d "$backup_app" ]]; then
  sudo -n mv "$backup_app" "$REMOTE_APP_DIR"
  sudo -n systemctl restart "$SERVICE_NAME" || true
fi
sudo -n systemctl --no-pager --full status "$SERVICE_NAME" --lines=40 || true
echo "Deploy failed and rollback attempted. Failed app saved at: $failed_app" >&2
exit 1
EOF_REMOTE
}

sync_server_tree_local() {
  echo "Applying publish output locally to $REMOTE_APP_DIR ..."
  local local_stage="$REMOTE_STAGE_DIR/publish"
  sudo_local rm -rf "$REMOTE_STAGE_DIR"
  sudo_local install -d -m 0755 "$local_stage"
  sudo_local rsync -a --delete "$PUBLISH_DIR/" "$local_stage/"
  REMOTE_SERVICE_ROOT="$REMOTE_SERVICE_ROOT" \
  REMOTE_APP_DIR="$REMOTE_APP_DIR" \
  REMOTE_STAGE_DIR="$REMOTE_STAGE_DIR" \
  SERVICE_NAME="$SERVICE_NAME" \
  REMOTE_SERVICE_USER="$REMOTE_SERVICE_USER" \
  REMOTE_SERVICE_GROUP="$REMOTE_SERVICE_GROUP" \
  SKIP_RESTART="$SKIP_RESTART" \
    bash -c "$(remote_install_script)"
}

sync_server_tree_remote() {
  echo "Uploading publish output to $SSH_TARGET:$REMOTE_STAGE_DIR/publish ..."
  # Intentionally expand quoted paths locally before sending the command to the remote shell.
  # shellcheck disable=SC2029
  ssh "$SSH_TARGET" "rm -rf $(shell_quote "$REMOTE_STAGE_DIR") && mkdir -p $(shell_quote "$REMOTE_STAGE_DIR/publish")"
  rsync -a --delete "$PUBLISH_DIR/" "$SSH_TARGET:$REMOTE_STAGE_DIR/publish/"

  echo "Applying publish output on $SSH_TARGET:$REMOTE_APP_DIR ..."
  local remote_env remote_install_path
  remote_install_path="$REMOTE_STAGE_DIR/install-den-core.sh"
  remote_env="REMOTE_SERVICE_ROOT=$(shell_quote "$REMOTE_SERVICE_ROOT")"
  remote_env+=" REMOTE_APP_DIR=$(shell_quote "$REMOTE_APP_DIR")"
  remote_env+=" REMOTE_STAGE_DIR=$(shell_quote "$REMOTE_STAGE_DIR")"
  remote_env+=" SERVICE_NAME=$(shell_quote "$SERVICE_NAME")"
  remote_env+=" REMOTE_SERVICE_USER=$(shell_quote "$REMOTE_SERVICE_USER")"
  remote_env+=" REMOTE_SERVICE_GROUP=$(shell_quote "$REMOTE_SERVICE_GROUP")"
  remote_env+=" SKIP_RESTART=$(shell_quote "$SKIP_RESTART")"
  # Intentionally expand the prepared remote path locally before sending the remote command.
  # shellcheck disable=SC2029
  remote_install_script | ssh "$SSH_TARGET" "cat > $(shell_quote "$remote_install_path") && chmod 700 $(shell_quote "$remote_install_path")"
  # Intentionally expand the prepared environment string locally for the remote shell.
  # shellcheck disable=SC2029
  ssh "$SSH_TARGET" "$remote_env bash $(shell_quote "$remote_install_path")"
}

sync_server_tree() {
  if [[ "$DEPLOY_MODE" == "local" ]]; then
    sync_server_tree_local
  else
    sync_server_tree_remote
  fi
}

smoke_http_and_mcp() {
  if [[ "$SKIP_SMOKE" -eq 1 ]]; then
    echo "Skipping smoke checks."
    return
  fi

  if [[ "$SKIP_RESTART" -eq 1 ]]; then
    echo "Skipping smoke checks because --skip-restart was used."
    return
  fi

  echo "Running health smoke against $HEALTH_URL ..."
  curl --retry 20 --retry-delay 1 --retry-connrefused -fsS "$HEALTH_URL" >/dev/null

  echo "Running MCP tools/list smoke against Core proxy and LAN facade ..."
  MCP_CORE_URL="$MCP_CORE_URL" MCP_LAN_URL="$MCP_LAN_URL" python3 - <<'PY'
import json
import os
import urllib.request


def parse(text: str):
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        data = [line[5:].strip() for line in text.splitlines() if line.startswith("data:")]
        if data:
            return json.loads("\n".join(data))
        raise


def post(url: str, payload: dict, sid: str | None = None):
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if sid:
        headers["Mcp-Session-Id"] = sid
    req = urllib.request.Request(url, data=json.dumps(payload).encode(), headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=30) as response:
        return response.headers.get("Mcp-Session-Id"), parse(response.read().decode())


def tool_names(url: str) -> set[str]:
    sid, _ = post(url, {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "den-core-deploy-live-smoke", "version": "1"},
        },
    })
    _, payload = post(url, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, sid)
    tools = payload.get("result", {}).get("tools", [])
    names = {tool.get("name", "") for tool in tools}
    if not names:
        raise SystemExit(f"{url}: tools/list returned no tools: {payload!r}")
    required = {"send_message", "get_task", "register_worker_run", "post_worker_completion_packet"}
    missing = sorted(required - names)
    if missing:
        raise SystemExit(f"{url}: missing expected Core MCP tools: {missing}")
    return names


for url in [os.environ["MCP_CORE_URL"], os.environ["MCP_LAN_URL"]]:
    names = tool_names(url)
    print(f"{url}: tools/list ok ({len(names)} tools)")
PY

  echo "Smoke checks passed."
}

main() {
  require_non_root
  parse_args "$@"
  resolve_deploy_mode
  print_config
  preflight_tools

  if [[ "$DRY_RUN" -eq 1 ]]; then
    echo "Dry run requested; stopping before build/upload/install."
    exit 0
  fi

  preflight_privilege
  initialize_publish_dir
  initialize_build_artifacts_dir
  trap cleanup EXIT
  publish_server
  sync_server_tree
  smoke_http_and_mcp
  echo "Deploy complete."
}

main "$@"
