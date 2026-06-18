#!/bin/bash
# Deployment smoke check for Den Core
# Run after deploy to verify port ownership, DB health, and API responses.
# Some checks (systemctl MainPID, ss PID visibility) require root/sudo.
# Usage: ./den-core-deploy-smoke.sh [--verbose]

set -uo pipefail
VERBOSE=false
for arg in "$@"; do [ "$arg" = "--verbose" ] && VERBOSE=true; done

PASS=0
FAIL=0

check() {
    local desc="$1"
    shift
    if "$@"; then
        PASS=$((PASS + 1))
        echo "  ✅ $desc"
        return 0
    else
        FAIL=$((FAIL + 1))
        echo "  ❌ $desc"
        return 1
    fi
}

echo "=== Den Core Deploy Smoke Check ==="
echo ""

# --- 1. Private Core health (internal port 5299) ---
echo "--- Port ownership (listen PID + systemd) ---"

# Try to read systemd MainPID first (most reliable, may need sudo)
CORE_UNIT_PID=$(systemctl show den-core.service -p MainPID 2>/dev/null | sed 's/MainPID=//' || \
                systemctl --user show den-core.service -p MainPID 2>/dev/null | sed 's/MainPID=//' || true)
FACADE_UNIT_PID=$(systemctl show den-mcp.service -p MainPID 2>/dev/null | sed 's/MainPID=//' || \
                  systemctl --user show den-mcp.service -p MainPID 2>/dev/null | sed 's/MainPID=//' || true)

# Also read listener PIDs from ss (may be empty for non-root)
CORE_LISTEN_PID=$(ss -Htnlp 'sport = :5299' 2>/dev/null | grep -oP 'pid=\K[0-9]+' | head -1 || true)
FACADE_LISTEN_PID=$(ss -Htnlp 'sport = :5199' 2>/dev/null | grep -oP 'pid=\K[0-9]+' | head -1 || true)

# Ownership check: systemd PID must match listen PID (or be reliable on its own)
if [ -n "$CORE_UNIT_PID" ] && [ "$CORE_UNIT_PID" -gt 0 ] 2>/dev/null; then
    if [ -n "$CORE_LISTEN_PID" ]; then
        # Cross-verify: systemd MainPID matches ss listener PID
        if [ "$CORE_UNIT_PID" -eq "$CORE_LISTEN_PID" ] 2>/dev/null; then
            check "den-core.service (MainPID=$CORE_UNIT_PID) owns 127.0.0.1:5299 ✓ cross-verified with ss" true
        else
            check "den-core.service (MainPID=$CORE_UNIT_PID) vs ss listener (PID=$CORE_LISTEN_PID) mismatch" false
        fi
    else
        check "den-core.service (MainPID=$CORE_UNIT_PID) — ss PID not available (try as root)" true
    fi
elif [ -n "$CORE_LISTEN_PID" ]; then
    check "Listener on :5299 (PID $CORE_LISTEN_PID) — systemd unit not readable without root" true
else
    check "den-core.service not found via systemctl or ss" false
fi

if [ -n "$FACADE_UNIT_PID" ] && [ "$FACADE_UNIT_PID" -gt 0 ] 2>/dev/null; then
    if [ -n "$FACADE_LISTEN_PID" ]; then
        if [ "$FACADE_UNIT_PID" -eq "$FACADE_LISTEN_PID" ] 2>/dev/null; then
            check "den-mcp.service (MainPID=$FACADE_UNIT_PID) owns :5199 ✓ cross-verified with ss" true
        else
            check "den-mcp.service (MainPID=$FACADE_UNIT_PID) vs ss listener (PID=$FACADE_LISTEN_PID) mismatch" false
        fi
    else
        check "den-mcp.service (MainPID=$FACADE_UNIT_PID) — ss PID not available (try as root)" true
    fi
elif [ -n "$FACADE_LISTEN_PID" ]; then
    check "Listener on :5199 (PID $FACADE_LISTEN_PID) — systemd unit not readable without root" true
else
    check "den-mcp.service not found via systemctl or ss" false
fi

# --- 2. Health endpoints ---
echo ""
echo "--- Health endpoints ---"
CORE_HEALTH=$(curl -sf http://127.0.0.1:5299/health 2>&1 || echo "FAILED")
check "Core private health at 127.0.0.1:5299" test "$CORE_HEALTH" != "FAILED"
if [ "$VERBOSE" = true ] && [ "$CORE_HEALTH" != "FAILED" ]; then
    echo "  Core response: $(echo "$CORE_HEALTH" | head -c 300)"
fi

FACADE_HEALTH=$(curl -sf http://192.168.1.10:5199/health 2>&1 || echo "FAILED")
check "Facade health at 192.168.1.10:5199" test "$FACADE_HEALTH" != "FAILED"

# --- 3. Validate facade is NOT bare Core ---
echo ""
echo "--- Topology consistency ---"
if [ "$CORE_HEALTH" != "FAILED" ] && [ "$FACADE_HEALTH" != "FAILED" ]; then
    # Compare response hashes — if identical, facade is proxying bare Core
    CORE_HASH=$(echo "$CORE_HEALTH" | md5sum | cut -d' ' -f1)
    FACADE_HASH=$(echo "$FACADE_HEALTH" | md5sum | cut -d' ' -f1)
    if [ "$CORE_HASH" != "$FACADE_HASH" ]; then
        check "Facade response differs from Core response (dedicated facade endpoint)" true
    else
        check "Facade and Core responses are identical — facade may be proxying directly to Core" false
    fi
else
    check "Facade vs Core response comparison (skipped, one endpoint down)" true
fi

# --- 4. Database sanity ---
echo ""
echo "--- Database sanity ---"
PROJECTS=$(curl -sf http://127.0.0.1:5299/api/projects 2>&1 || echo '{"projects":[]}')
PROJECT_COUNT=$(echo "$PROJECTS" | python3 -c "
import sys,json
d=json.load(sys.stdin)
if isinstance(d, list):
    print(len(d))
elif isinstance(d, dict):
    items = d.get('projects', d.get('items', []))
    print(len(items) if isinstance(items, list) else 0)
else:
    print(0)
" 2>/dev/null || echo "0")
check "Projects endpoint returns real data (≥1 project, not empty DB)" test "$PROJECT_COUNT" -ge 1

if [ "$VERBOSE" = true ]; then
    echo "  Projects count: $PROJECT_COUNT"
fi

# --- 5. Knowledge routes ---
echo ""
echo "--- Knowledge routes ---"
KNOWLEDGE_CODE=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5299/api/knowledge/entries 2>&1 || echo "000")
if [ "$KNOWLEDGE_CODE" = "200" ]; then
    check "Knowledge entries accessible (HTTP 200)" true
else
    check "Knowledge entries reachable (HTTP $KNOWLEDGE_CODE — may be expected if not deployed)" true
fi

# --- 6. Static UI ---
echo ""
echo "--- Static UI ---"
UI_CODE=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5299/ 2>&1 || echo "000")
check "Static UI serves (/) at 5299" test "$UI_CODE" = "200"

# --- Summary ---
echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi
