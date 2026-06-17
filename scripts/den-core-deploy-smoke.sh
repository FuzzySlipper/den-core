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

# --- Private Core health (internal port 5299) ---
echo ""
echo "--- Port ownership ---"
CORE_HEALTH=$(curl -sf http://127.0.0.1:5299/health 2>&1 || echo "FAILED")
check "Core private health at 127.0.0.1:5299" test "$CORE_HEALTH" != "FAILED"

if [ "$CORE_HEALTH" != "FAILED" ] && [ "$VERBOSE" = true ]; then
    echo "  Core health: $(echo "$CORE_HEALTH" | head -c 200)"
fi

# --- Public facade health (port 5199) ---
FACADE_HEALTH=$(curl -sf http://192.168.1.10:5199/health 2>&1 || echo "FAILED")
check "Facade health at 192.168.1.10:5199" test "$FACADE_HEALTH" != "FAILED"

# --- REAL DB check (not accidental default) ---
echo ""
echo "--- Database sanity ---"
PROJECTS=$(curl -sf http://127.0.0.1:5299/api/projects 2>&1 || echo '{"projects":[]}')
PROJECT_COUNT=$(echo "$PROJECTS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('projects', d)))" 2>/dev/null || echo "0")
check "Projects endpoint returns data (≥1 project, not empty DB)" test "$PROJECT_COUNT" -ge 1

if [ "$VERBOSE" = true ]; then
    echo "  Projects count: $PROJECT_COUNT"
    echo "  Projects: $(echo "$PROJECTS" | python3 -c "import sys,json; d=json.load(sys.stdin); print([p.get('id') for p in (d.get('projects', d) if isinstance(d, dict) else [])[:5]])" 2>/dev/null || echo 'unknown')"
fi

# --- Knowledge entries (if deployed) ---
echo ""
echo "--- Knowledge routes ---"
KNOWLEDGE_CODE=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5299/api/knowledge/entries 2>&1 || echo "000")
if [ "$KNOWLEDGE_CODE" = "200" ]; then
    check "Knowledge entries accessible" true
else
    check "Knowledge entries accessible (expected 200)" false
    echo "  (got HTTP $KNOWLEDGE_CODE — may be expected if knowledge not deployed)"
fi

# --- Subscription rate limit page (ui) ---
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
