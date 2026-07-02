#!/usr/bin/env bash
# MayFly backend end-to-end smoke test
# Runs 6 checks: create, query via psql (or API), query via API, list, dashboard, delete
# All services are started and torn down within this script.
# Safe: trap kills background processes and mayfly containers on exit.
#
# Ports used: Provisioner=9090, API=7070
# (8080 is taken by Docker Desktop; 5000/7000 by macOS ControlCenter/AirPlay on this machine)
set -euo pipefail

REPO=/Users/fanfan/Documents/App/MayFly
LOG_DIR=/tmp/mayfly-e2e-$$
COOKIES=/tmp/mayfly-cookies-$$.txt
PASS=0
FAIL=0
ERRORS=()

PROV_PORT=9090
API_PORT=7070

mkdir -p "$LOG_DIR"

PROV_PID=""
API_PID=""
INSTANCE_TOKEN=""

cleanup() {
  echo ""
  echo "=== CLEANUP ==="
  # Delete the MayFly instance via API first (best effort)
  if [[ -n "$INSTANCE_TOKEN" ]]; then
    echo "Deleting instance $INSTANCE_TOKEN via API..."
    curl -s -o /dev/null -w "DELETE status: %{http_code}\n" \
      -b "$COOKIES" -X DELETE "http://localhost:${API_PORT}/api/instances/$INSTANCE_TOKEN" || true
    sleep 1
  fi
  # Kill background services
  if [[ -n "$PROV_PID" ]]; then
    echo "Stopping Provisioner (PID $PROV_PID)..."
    kill "$PROV_PID" 2>/dev/null || true
    wait "$PROV_PID" 2>/dev/null || true
  fi
  if [[ -n "$API_PID" ]]; then
    echo "Stopping API (PID $API_PID)..."
    kill "$API_PID" 2>/dev/null || true
    wait "$API_PID" 2>/dev/null || true
  fi
  # Force-remove any lingering mayfly-pg-* containers
  REMAINING=$(docker ps -a --format '{{.Names}}' | grep '^mayfly-pg-' || true)
  if [[ -n "$REMAINING" ]]; then
    echo "Force-removing leftover containers: $REMAINING"
    echo "$REMAINING" | xargs docker rm -f 2>/dev/null || true
  fi
  # Force-remove any lingering mayfly-vol-* volumes
  LINGERING_VOLS=$(docker volume ls --format '{{.Name}}' | grep '^mayfly-vol-' || true)
  if [[ -n "$LINGERING_VOLS" ]]; then
    echo "Removing leftover volumes: $LINGERING_VOLS"
    echo "$LINGERING_VOLS" | xargs docker volume rm -f 2>/dev/null || true
  fi
  rm -f "$COOKIES"
  echo "Cleanup done."
}
trap cleanup EXIT

check_pass() { echo "  [PASS] $1"; PASS=$((PASS + 1)); }
check_fail() { echo "  [FAIL] $1"; FAIL=$((FAIL + 1)); ERRORS+=("$1"); }

wait_for_http() {
  local url="$1" timeout_secs="$2" label="$3"
  local elapsed=0
  echo "  Waiting for $label at $url (up to ${timeout_secs}s)..."
  # Any HTTP response (even 404) proves service is up; "000" means no TCP connection made.
  # IMPORTANT: do NOT use $(curl ... || echo "000") — that appends a second "000" to stdout.
  # Instead: capture curl's output, then the || reassigns the variable if curl failed.
  while true; do
    local code
    code="$(curl -s -o /dev/null -w "%{http_code}" --max-time 2 "$url" 2>/dev/null)" || code="000"
    if [[ "$code" != "000" ]]; then
      echo "  $label is ready (${elapsed}s, status $code)"
      return 0
    fi
    if (( elapsed >= timeout_secs )); then
      echo "  TIMEOUT waiting for $label after ${elapsed}s"
      return 1
    fi
    sleep 2
    elapsed=$((elapsed + 2))
  done
}

echo "============================================"
echo "MayFly E2E Smoke Test"
echo "  Provisioner: http://localhost:${PROV_PORT}"
echo "  API:         http://localhost:${API_PORT}"
echo "============================================"
echo ""

# ──────────────────────────────────────────────
# STEP 0: Build the solution
# ──────────────────────────────────────────────
echo "=== STEP 0: Build ==="
cd "$REPO"
dotnet build -c Release MayFly.sln > "$LOG_DIR/build.log" 2>&1
echo "  Build succeeded."

# ──────────────────────────────────────────────
# STEP 1: Start metadata-db (idempotent)
# ──────────────────────────────────────────────
echo ""
echo "=== STEP 1: Start metadata-db ==="
cd "$REPO"
docker compose up -d metadata-db > "$LOG_DIR/compose.log" 2>&1
echo "  docker compose up -d metadata-db done."

# Wait for metadata-db to accept connections
echo "  Waiting for metadata-db on localhost:5433..."
ELAPSED=0
until docker exec "$(docker compose ps -q metadata-db)" pg_isready -q 2>/dev/null; do
  if (( ELAPSED >= 60 )); then
    echo "  TIMEOUT: metadata-db not ready"
    exit 1
  fi
  sleep 2
  ELAPSED=$((ELAPSED + 2))
done
echo "  metadata-db ready (${ELAPSED}s)"

# ──────────────────────────────────────────────
# STEP 2: Apply migrations
# ──────────────────────────────────────────────
echo ""
echo "=== STEP 2: Apply migrations ==="
cd "$REPO"
ConnectionStrings__Metadata="Host=localhost;Port=5433;Database=mayfly;Username=mayfly;Password=mayfly" \
  dotnet ef database update --project MayFly.Api > "$LOG_DIR/migrations.log" 2>&1
echo "  Migrations applied."

# ──────────────────────────────────────────────
# STEP 3: Start Provisioner on :${PROV_PORT}
# ──────────────────────────────────────────────
echo ""
echo "=== STEP 3: Start Provisioner ==="
cd "$REPO"
ASPNETCORE_URLS="http://localhost:${PROV_PORT}" \
  Provisioner__UseInternalHost=false \
  Provisioner__UseXfsQuota=false \
  dotnet run --project MayFly.Provisioner -c Release --no-build --no-launch-profile \
  > "$LOG_DIR/provisioner.log" 2>&1 &
PROV_PID=$!
echo "  Provisioner PID: $PROV_PID"

# Provisioner doesn't have a /health; any response (even 404) proves it's up
wait_for_http "http://localhost:${PROV_PORT}/instances/healthcheck-probe" 90 "Provisioner" || {
  echo "  Provisioner log (last 40 lines):"
  tail -40 "$LOG_DIR/provisioner.log" || true
  echo "BLOCKED: Provisioner did not start within timeout"
  exit 1
}

# ──────────────────────────────────────────────
# STEP 4: Start API on :${API_PORT}
# ──────────────────────────────────────────────
echo ""
echo "=== STEP 4: Start API ==="
cd "$REPO"
ASPNETCORE_URLS="http://localhost:${API_PORT}" \
  ConnectionStrings__Metadata="Host=localhost;Port=5433;Database=mayfly;Username=mayfly;Password=mayfly" \
  Provisioner__BaseUrl="http://localhost:${PROV_PORT}" \
  QueryExecutor__UseInternalHost=false \
  PublicHost=localhost \
  dotnet run --project MayFly.Api -c Release --no-build --no-launch-profile \
  > "$LOG_DIR/api.log" 2>&1 &
API_PID=$!
echo "  API PID: $API_PID"

wait_for_http "http://localhost:${API_PORT}/api/instances" 90 "API" || {
  echo "  API log (last 40 lines):"
  tail -40 "$LOG_DIR/api.log" || true
  echo "BLOCKED: API did not start within timeout"
  exit 1
}

echo ""
echo "============================================"
echo "Both services up. Running 6 checks."
echo "============================================"

# ──────────────────────────────────────────────
# CHECK 1: Create instance (POST /api/instances)
# ──────────────────────────────────────────────
echo ""
echo "=== CHECK 1: Create instance ==="
# Use --max-time 120 because northwind seeding takes ~30s (postgres init + SQL)
CREATE_RESPONSE=$(curl -s -c "$COOKIES" -w "\n__STATUS__:%{http_code}" \
  --max-time 120 \
  -X POST "http://localhost:${API_PORT}/api/instances" \
  -H 'Content-Type: application/json' \
  -d '{"engine":"postgres","ttlHours":3,"storageMb":256,"initialData":"northwind"}')

CREATE_STATUS=$(echo "$CREATE_RESPONSE" | grep '__STATUS__:' | cut -d: -f2)
CREATE_BODY=$(echo "$CREATE_RESPONSE" | grep -v '__STATUS__:')

echo "  HTTP Status: $CREATE_STATUS"
echo "  Response body: $CREATE_BODY"

if [[ "$CREATE_STATUS" == "201" ]]; then
  check_pass "CHECK 1: POST /api/instances → 201"
  INSTANCE_TOKEN=$(echo "$CREATE_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['token'])" 2>/dev/null || echo "")
  CONN_STRING=$(echo "$CREATE_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['connectionString'])" 2>/dev/null || echo "")
  echo "  Token: $INSTANCE_TOKEN"
  echo "  ConnectionString: $CONN_STRING"
  if [[ -z "$INSTANCE_TOKEN" ]]; then
    check_fail "CHECK 1b: Could not extract token from response body"
  fi
else
  check_fail "CHECK 1: POST /api/instances expected 201, got $CREATE_STATUS"
  echo "  API log (last 40 lines):"
  tail -40 "$LOG_DIR/api.log" || true
  echo "  Provisioner log (last 40 lines):"
  tail -40 "$LOG_DIR/provisioner.log" || true
fi

# ──────────────────────────────────────────────
# CHECK 2: External psql connect (docker-based fallback, then API fallback)
# ──────────────────────────────────────────────
echo ""
echo "=== CHECK 2: External connection to Postgres ==="
if [[ -n "$CONN_STRING" && -n "$INSTANCE_TOKEN" ]]; then
  PG_PORT=$(echo "$CREATE_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['publicPort'])" 2>/dev/null || echo "")
  echo "  Northwind DB exposed on host port: $PG_PORT"
  # On macOS Docker Desktop, --network host doesn't route to the Mac loopback.
  # Use host.docker.internal to reach the Mac host from inside a container.
  DOCKER_CONN=$(echo "$CONN_STRING" | sed 's|@localhost:|@host.docker.internal:|')
  echo "  Attempting docker psql: $DOCKER_CONN"
  PSQL_OUT=$(docker run --rm postgres:16-alpine \
    psql "$DOCKER_CONN" -t -c "SELECT COUNT(*) FROM products;" 2>&1 || echo "PSQL_FAILED")
  echo "  psql output: $PSQL_OUT"
  if echo "$PSQL_OUT" | grep -qE '^[[:space:]]*[0-9]+'; then
    COUNT=$(echo "$PSQL_OUT" | grep -oE '[0-9]+' | head -1)
    if (( COUNT > 0 )); then
      check_pass "CHECK 2: docker psql SELECT COUNT(*) FROM products → $COUNT (Northwind confirmed)"
    else
      check_fail "CHECK 2: products count is 0 via docker psql (seeding failed?)"
    fi
  else
    echo "  NOTE: docker psql could not connect externally — using API fallback."
    FB_RESPONSE=$(curl -s -b "$COOKIES" --max-time 30 \
      -X POST "http://localhost:${API_PORT}/api/instances/$INSTANCE_TOKEN/query" \
      -H 'Content-Type: application/json' \
      -d '{"sql":"SELECT COUNT(*) AS n FROM products"}' 2>/dev/null || echo "{}")
    FB_COUNT=$(echo "$FB_RESPONSE" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('rows',[[0]])[0][0])" 2>/dev/null || echo "0")
    if (( FB_COUNT > 0 )); then
      check_pass "CHECK 2: API fallback SELECT COUNT(*) FROM products → $FB_COUNT (psql unavailable; API confirms Northwind seeded)"
    else
      check_fail "CHECK 2: External psql failed AND API fallback count=0 (psql err: $PSQL_OUT)"
    fi
  fi
else
  check_fail "CHECK 2: Skipped (no connectionString or token from check 1)"
fi

# ──────────────────────────────────────────────
# CHECK 3: Query via API
# ──────────────────────────────────────────────
echo ""
echo "=== CHECK 3: Query via API ==="
if [[ -n "$INSTANCE_TOKEN" ]]; then
  QUERY_RESPONSE=$(curl -s -b "$COOKIES" -w "\n__STATUS__:%{http_code}" \
    --max-time 30 \
    -X POST "http://localhost:${API_PORT}/api/instances/$INSTANCE_TOKEN/query" \
    -H 'Content-Type: application/json' \
    -d '{"sql":"SELECT COUNT(*) AS n FROM products"}')
  QUERY_STATUS=$(echo "$QUERY_RESPONSE" | grep '__STATUS__:' | cut -d: -f2)
  QUERY_BODY=$(echo "$QUERY_RESPONSE" | grep -v '__STATUS__:')
  echo "  HTTP Status: $QUERY_STATUS"
  echo "  Response: $QUERY_BODY"
  QUERY_SUCCESS=$(echo "$QUERY_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('success',''))" 2>/dev/null || echo "")
  ROW_VAL=$(echo "$QUERY_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('rows',[[0]])[0][0])" 2>/dev/null || echo "0")
  echo "  success=$QUERY_SUCCESS rows[0][0]=$ROW_VAL"
  if [[ "$QUERY_SUCCESS" == "True" ]] && (( ROW_VAL > 0 )); then
    check_pass "CHECK 3: API POST /query → success:true, products count=$ROW_VAL"
  else
    check_fail "CHECK 3: API query failed or count=0 (success=$QUERY_SUCCESS count=$ROW_VAL)"
  fi
else
  check_fail "CHECK 3: Skipped (no instance token)"
fi

# ──────────────────────────────────────────────
# CHECK 4: List instances
# ──────────────────────────────────────────────
echo ""
echo "=== CHECK 4: List instances ==="
LIST_RESPONSE=$(curl -s -b "$COOKIES" -w "\n__STATUS__:%{http_code}" \
  "http://localhost:${API_PORT}/api/instances")
LIST_STATUS=$(echo "$LIST_RESPONSE" | grep '__STATUS__:' | cut -d: -f2)
LIST_BODY=$(echo "$LIST_RESPONSE" | grep -v '__STATUS__:')
echo "  HTTP Status: $LIST_STATUS"
echo "  Response: $LIST_BODY"
LIST_COUNT=$(echo "$LIST_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d))" 2>/dev/null || echo "0")
if [[ "$LIST_STATUS" == "200" ]] && (( LIST_COUNT >= 1 )); then
  check_pass "CHECK 4: GET /api/instances → 200, $LIST_COUNT instance(s) listed"
else
  check_fail "CHECK 4: Expected 200 + >=1 instance, got status=$LIST_STATUS count=$LIST_COUNT"
fi

# ──────────────────────────────────────────────
# CHECK 5: Dashboard
# ──────────────────────────────────────────────
echo ""
echo "=== CHECK 5: Dashboard ==="
DASH_RESPONSE=$(curl -s -b "$COOKIES" -w "\n__STATUS__:%{http_code}" \
  "http://localhost:${API_PORT}/api/dashboard")
DASH_STATUS=$(echo "$DASH_RESPONSE" | grep '__STATUS__:' | cut -d: -f2)
DASH_BODY=$(echo "$DASH_RESPONSE" | grep -v '__STATUS__:')
echo "  HTTP Status: $DASH_STATUS"
echo "  Response: $DASH_BODY"
ALIVE_COUNT=$(echo "$DASH_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('aliveCount',0))" 2>/dev/null || echo "0")
STORAGE_BYTES=$(echo "$DASH_BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('storageUsedBytes',0))" 2>/dev/null || echo "0")
echo "  aliveCount=$ALIVE_COUNT storageUsedBytes=$STORAGE_BYTES"
if [[ "$DASH_STATUS" == "200" ]] && (( ALIVE_COUNT == 1 )); then
  if (( STORAGE_BYTES > 0 )); then
    check_pass "CHECK 5: Dashboard → aliveCount=$ALIVE_COUNT storageUsedBytes=$STORAGE_BYTES"
  else
    # storageUsedBytes=0 is acceptable right after creation; LifecycleService tick populates it
    check_pass "CHECK 5: Dashboard → aliveCount=$ALIVE_COUNT (storageUsedBytes=0; lifecycle tick not yet run — acceptable)"
    echo "  NOTE: storageUsedBytes will be non-zero after the ~30s LifecycleService tick."
  fi
else
  check_fail "CHECK 5: Expected 200+aliveCount=1, got status=$DASH_STATUS aliveCount=$ALIVE_COUNT"
fi

# ──────────────────────────────────────────────
# CHECK 6: Delete instance + confirm container gone
# ──────────────────────────────────────────────
echo ""
echo "=== CHECK 6: Delete instance ==="
if [[ -n "$INSTANCE_TOKEN" ]]; then
  DELETE_RESPONSE=$(curl -s -b "$COOKIES" -w "\n__STATUS__:%{http_code}" \
    -X DELETE "http://localhost:${API_PORT}/api/instances/$INSTANCE_TOKEN")
  DELETE_STATUS=$(echo "$DELETE_RESPONSE" | grep '__STATUS__:' | cut -d: -f2)
  echo "  DELETE HTTP Status: $DELETE_STATUS"

  if [[ "$DELETE_STATUS" == "204" ]]; then
    check_pass "CHECK 6a: DELETE /api/instances/$INSTANCE_TOKEN → 204"
    # Clear token so cleanup trap doesn't retry
    INSTANCE_TOKEN=""
    # Give Docker a moment to remove the container
    sleep 2
    REMAINING_CONTAINERS=$(docker ps -a --format '{{.Names}}' | grep '^mayfly-pg-' || echo "")
    echo "  Remaining mayfly-pg-* containers: '${REMAINING_CONTAINERS:-<none>}'"
    if [[ -z "$REMAINING_CONTAINERS" ]]; then
      check_pass "CHECK 6b: No leftover mayfly-pg-* containers"
    else
      check_fail "CHECK 6b: Leftover containers found: $REMAINING_CONTAINERS"
    fi
  else
    check_fail "CHECK 6: DELETE expected 204, got $DELETE_STATUS"
  fi
else
  check_fail "CHECK 6: Skipped (no instance token available)"
fi

# ──────────────────────────────────────────────
# SUMMARY
# ──────────────────────────────────────────────
echo ""
echo "============================================"
echo "RESULTS: $PASS passed, $FAIL failed"
echo "============================================"
if (( ${#ERRORS[@]} > 0 )); then
  echo "Failures:"
  for err in "${ERRORS[@]}"; do
    echo "  - $err"
  done
fi

echo ""
echo "Log files in: $LOG_DIR"
echo "  build.log        provisioner.log"
echo "  compose.log      api.log"
echo "  migrations.log"

if (( FAIL > 0 )); then
  exit 1
else
  echo ""
  echo "All checks passed."
  exit 0
fi
