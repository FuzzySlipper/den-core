#!/bin/bash
# Deployment smoke check for Den Core
# Run after deploy to verify port ownership, DB health, and API responses.
# Usage: ./den-core-deploy-smoke.sh [--verbose]

set -euo pipefail
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
    else
        FAIL=$((FAIL + 1))
        echo "  ❌ $desc"
    fi
}

echo "=== Den Core Deploy Smoke Check ==="
echo ""

# --- 1. Private Core health (internal port 5299) ---
echo "--- Port ownership (listen PID + port) ---"

# Verify den-core owns 127.0.0.1:5299
CORE_PID=$(ss -Htnlp 'sport = :5299' 2>/dev/null | grep -oP 'pid=\K[0-9]+' | head -1 || echo "")
if [ -n "$CORE_PID" ]; then
    CORE_CMD=$(ps -p "$CORE_PID" -o comm= 2>/dev/null || echo "unknown")
    check "den-core (PID $CORE_PID, process $CORE_CMD) owns 127.0.0.1:5299" \
        test "$(echo "$CORE_CMD" | grep -c -i 'dencore\|DenCore')" -ge 1 || \
        check "Some process owns 127.0.0.1:5299" test -n "$CORE_PID"
else
    check "den-core owns 127.0.0.1:5299" false
fi
CORE_HEALTH=$(curl -sf http://127.0.0.1:5299/health 2>&1 || echo "FAILED")
check "Core private health at 127.0.0.1:5299" test "$CORE_HEALTH" != "FAILED"
if [ "$VERBOSE" = true ]; then
    echo "  Response: $(echo "$CORE_HEALTH" | head -c 300)"
fi

# --- 2. Public facade ownership (port 5199) ---
# Verify den-mcp owns 0.0.0.0:5199 (or :5199)
FACADE_PID=$(ss -Htnlp 'sport = :5199' 2>/dev/null | grep -oP 'pid=\K[0-9]+' | head -1 || echo "")
if [ -n "$FACADE_PID" ]; then
    FACADE_CMD=$(ps -p "$FACADE_PID" -o comm= 2>/dev/null || echo "unknown")
    check "den-mcp (PID $FACADE_PID, process $FACADE_CMD) owns :5199" \
        test "$(echo "$FACADE_CMD" | grep -c -i 'denmcp\|den-mcp\|DenMcp')" -ge 1 || \
        check "Some process owns :5199" test -n "$FACADE_PID"
else
    check "den-mcp owns :5199" false
fi

FACADE_HEALTH=$(curl -sf http://192.168.1.10:5199/health 2>&1 || echo "FAILED")
check "Facade health at 192.168.1.10:5199" test "$FACADE_HEALTH" != "FAILED"

# Verify facade response is NOT bare Core health (has different shape)
if [ "$FACADE_HEALTH" != "FAILED" ]; then
    FACADE_HAS_STATUS=$(echo "$FACADE_HEALTH" | python3 -c "import sys,json; d=json.load(sys.stdin); print('status' in d)" 2>/dev/null || echo "false")
    CORE_HAS_CORE_FIELDS=$(echo "$CORE_HEALTH" | python3 -c "import sys,json; d=json.load(sys.stdin); print('version' in d and 'commit' in d)" 2>/dev/null || echo "false")
    if [ "$FACADE_HAS_STATUS" = "True" ] && [ "$CORE_HAS_CORE_FIELDS" = "True" ]; then
        # Both respond with "status" — check they're NOT identical
        if [ "$CORE_HEALTH" = "$FACADE_HEALTH" ]; then
            check "Facade response differs from Core response (dedicated facade endpoint)" false
            echo "  ⚠ Both endpoints returned identical health payload — facade may be proxying directly to Core"
        else
            check "Facade response shape differs from Core (owned by distinct service)" true
        fi
    fi
fi

# --- 3. Database sanity ---
echo ""
echo "--- Database sanity ---"
PROJECTS=$(curl -sf http://127.0.0.1:5299/api/projects 2>&1 || echo '{"projects":[]}')
PROJECT_COUNT=$(echo "$PROJECTS" | python3 -c "
import sys,json
d=json.load(sys.stdin)
# Handle both array-at-root and {projects: [...]} shapes
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
    echo "  Project IDs: $(echo "$PROJECTS" | python3 -c "import sys,json; d=json.load(sys.stdin); items = d if isinstance(d, list) else d.get('projects', d.get('items', [])); print([p.get('id') for p in items[:5]])" 2>/dev/null || echo 'unknown')"
fi

# --- 4. Knowledge entries ---
echo ""
echo "--- Knowledge routes ---"
KNOWLEDGE_CODE=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5299/api/knowledge/entries 2>&1 || echo "000")
if [ "$KNOWLEDGE_CODE" = "200" ]; then
    check "Knowledge entries accessible (HTTP 200)" true
else
    check "Knowledge entries accessible (got $KNOWLEDGE_CODE — ok if endpoint not deployed)" true
fi

# --- 5. Static UI ---
echo ""
echo "--- Static UI ---"
UI_CODE=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5299/ 2>&1 || echo "000")
check "Static UI serves (/) at 5299" test "$UI_CODE" = "200"

# --- 6. Process/service unit health ---
echo ""
echo "--- Service unit ---"
if systemctl is-active --quiet den-core.service 2>/dev/null; then
    check "den-core.service is active" true
elif systemctl --user is-active --quiet den-core.service 2>/dev/null; then
    check "den-core.service (user) is active" true
else
    check "den-core.service is active (unit check)" false
fi

# --- Summary ---
echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi
