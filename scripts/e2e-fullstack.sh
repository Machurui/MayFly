#!/usr/bin/env bash
# MayFly full-stack end-to-end smoke test.
# Builds all images, brings up the complete stack, exercises 6 checks through
# Caddy on http://localhost, then tears everything down on EXIT.
#
# Checks performed (all through Caddy — NOT hitting api or web directly):
#   1. GET /               → 200, body contains <div id="app">  (Vue SPA served)
#   2. GET /api/instances  → 200 (empty array OK)               (Caddy→API routing)
#   3. POST /api/instances → 201 with token + connectionString  (end-to-end provision)
#   4. POST /api/instances/{token}/query → success:true, count>0 (Northwind seeded)
#   5. GET /api/dashboard  → 200, aliveCount>=1                 (dashboard counts)
#   6. DELETE /api/instances/{token} → 204                      (cleanup)
set -euo pipefail

REPO=/Users/fanfan/Documents/App/MayFly
COOKIES=/tmp/mayfly-fs-cookies-$$.txt
PASS=0
FAIL=0
ERRORS=()
INSTANCE_TOKEN=""

# ── Cleanup: ALWAYS runs on EXIT ──────────────────────────────────────────────
cleanup() {
  echo ""
  echo "=== CLEANUP ==="
  cd "$REPO"
  docker compose down -v 2>/dev/null || true
  # Remove mayfly-pg-* containers created by the provisioner (outside compose)
  REMAINING=$(docker ps -a --format '{{.Names}}' | grep '^mayfly-pg-' || true)
  if [[ -n "$REMAINING" ]]; then
    echo "  Force-removing leftover containers: $REMAINING"
    echo "$REMAINING" | xargs docker rm -f 2>/dev/null || true
  fi
  # Remove mayfly-vol-* volumes created by the provisioner
  LINGERING_VOLS=$(docker volume ls --format '{{.Name}}' | grep '^mayfly-vol-' || true)
  if [[ -n "$LINGERING_VOLS" ]]; then
    echo "  Removing leftover volumes: $LINGERING_VOLS"
    echo "$LINGERING_VOLS" | xargs docker volume rm -f 2>/dev/null || true
  fi
  # Remove the mayfly-internal network so next run gets a fresh compose-managed one
  docker network rm mayfly-internal 2>/dev/null || true
  rm -f "$COOKIES"
  echo "  Cleanup done — no leftover mayfly containers, volumes, or networks."
}
trap cleanup EXIT

check_pass() { echo "  [PASS] $1"; PASS=$((PASS + 1)); }
check_fail() { echo "  [FAIL] $1"; FAIL=$((FAIL + 1)); ERRORS+=("$1"); }

# Wait for a URL to return any non-zero HTTP status (000 = no TCP connection)
wait_for_http() {
  local url="$1" timeout_secs="$2" label="$3"
  local elapsed=0
  echo "  Waiting for $label at $url (up to ${timeout_secs}s)..."
  while true; do
    local code
    code="$(curl -s -o /dev/null -w "%{http_code}" --max-time 3 "$url" 2>/dev/null)" || code="000"
    if [[ "$code" != "000" ]]; then
      echo "  $label ready (${elapsed}s, HTTP $code)"
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
echo "MayFly Full-Stack E2E Smoke (through Caddy)"
echo "  Entry point: http://localhost"
echo "============================================"
echo ""

cd "$REPO"

# ── STEP 1: Build all images and start the full stack ─────────────────────────
echo "=== STEP 1: docker compose up -d --build (may take several minutes first run) ==="
docker compose up -d --build
echo "  Stack started."

# ── STEP 2: Wait for metadata-db ──────────────────────────────────────────────
echo ""
echo "=== STEP 2: Wait for metadata-db to be ready ==="
ELAPSED=0
until docker compose exec -T metadata-db pg_isready -q 2>/dev/null; do
  if (( ELAPSED >= 60 )); then
    echo "  TIMEOUT: metadata-db not ready after 60s"
    docker compose logs metadata-db | tail -20
    exit 1
  fi
  sleep 2
  ELAPSED=$((ELAPSED + 2))
done
echo "  metadata-db ready (${ELAPSED}s)"

# ── STEP 3: Apply EF migrations (host dotnet ef → published port 5433) ────────
echo ""
echo "=== STEP 3: Apply EF migrations ==="
# Build the API project locally so the EF migration runner can execute
dotnet build "$REPO/MayFly.Api/MayFly.Api.csproj" -c Release \
  > /tmp/mayfly-apibuild-$$.log 2>&1 \
  && echo "  API project built (local, for migration runner)." \
  || { echo "  dotnet build failed:"; cat /tmp/mayfly-apibuild-$$.log; exit 1; }

ConnectionStrings__Metadata="Host=localhost;Port=5433;Database=mayfly;Username=mayfly;Password=mayfly" \
  dotnet ef database update --project "$REPO/MayFly.Api" --no-build 2>&1 \
  && echo "  Migrations applied." \
  || {
    echo "  'dotnet ef' failed — retrying without --no-build..."
    ConnectionStrings__Metadata="Host=localhost;Port=5433;Database=mayfly;Username=mayfly;Password=mayfly" \
      dotnet ef database update --project "$REPO/MayFly.Api" 2>&1 \
      && echo "  Migrations applied (second attempt)." \
      || { echo "  Migration failed — stack may not serve API correctly."; exit 1; }
  }

# ── STEP 4: Wait for Caddy to accept requests ─────────────────────────────────
echo ""
echo "=== STEP 4: Wait for Caddy + API readiness ==="
wait_for_http "http://localhost/" 90 "Caddy" || {
  echo "  Service logs:"
  docker compose logs caddy | tail -20
  docker compose logs api | tail -20
  docker compose logs web | tail -20
  exit 1
}
# Brief pause so the API finishes initialising after Caddy comes up
sleep 3

echo ""
echo "============================================"
echo "Stack ready. Running 6 checks."
echo "============================================"

# ── CHECK 1: SPA served through Caddy ─────────────────────────────────────────
echo ""
echo "=== CHECK 1: GET http://localhost/ → 200 + <div id=\"app\"> ==="
SPA_BODY_FILE=/tmp/mayfly-spa-$$.html
SPA_STATUS=$(curl -s -o "$SPA_BODY_FILE" -w "%{http_code}" --max-time 10 \
  http://localhost/ 2>/dev/null || echo "000")
echo "  HTTP $SPA_STATUS"
if [[ "$SPA_STATUS" == "200" ]]; then
  if grep -q '<div id="app">' "$SPA_BODY_FILE" 2>/dev/null; then
    check_pass 'CHECK 1: GET / → 200 with <div id="app">'
  else
    echo "  Body excerpt: $(head -5 "$SPA_BODY_FILE")"
    check_fail 'CHECK 1: GET / → 200 but missing <div id="app">'
  fi
else
  check_fail "CHECK 1: GET / expected 200, got $SPA_STATUS"
fi
rm -f "$SPA_BODY_FILE"

# ── CHECK 2: GET /api/instances via Caddy ─────────────────────────────────────
echo ""
echo "=== CHECK 2: GET http://localhost/api/instances → 200 ==="
LIST_RESP=$(curl -s -c "$COOKIES" -b "$COOKIES" \
  -w "\n__STATUS__:%{http_code}" --max-time 10 \
  http://localhost/api/instances 2>/dev/null || echo "__STATUS__:000")
LIST_STATUS=$(echo "$LIST_RESP" | grep '__STATUS__:' | cut -d: -f2)
LIST_BODY=$(echo "$LIST_RESP" | grep -v '__STATUS__:')
echo "  HTTP $LIST_STATUS  body: $LIST_BODY"
if [[ "$LIST_STATUS" == "200" ]]; then
  check_pass "CHECK 2: GET /api/instances → 200"
else
  check_fail "CHECK 2: GET /api/instances expected 200, got $LIST_STATUS"
  docker compose logs api | tail -20
fi

# ── CHECK 3: POST /api/instances via Caddy ────────────────────────────────────
echo ""
echo "=== CHECK 3: POST http://localhost/api/instances → 201 ==="
# Use --max-time 180: northwind seeding (postgres init + SQL) can take ~60s
CREATE_RESP=$(curl -s -c "$COOKIES" -b "$COOKIES" \
  -w "\n__STATUS__:%{http_code}" --max-time 180 \
  -X POST http://localhost/api/instances \
  -H 'Content-Type: application/json' \
  -d '{"engine":"postgres","ttlHours":3,"storageMb":256,"initialData":"northwind"}' \
  2>/dev/null || echo "__STATUS__:000")
CREATE_STATUS=$(echo "$CREATE_RESP" | grep '__STATUS__:' | cut -d: -f2)
CREATE_BODY=$(echo "$CREATE_RESP" | grep -v '__STATUS__:')
echo "  HTTP $CREATE_STATUS"
echo "  Body: $CREATE_BODY"
if [[ "$CREATE_STATUS" == "201" ]]; then
  check_pass "CHECK 3: POST /api/instances → 201"
  INSTANCE_TOKEN=$(echo "$CREATE_BODY" | python3 -c \
    "import sys,json; print(json.load(sys.stdin)['token'])" 2>/dev/null || echo "")
  CONN_STRING=$(echo "$CREATE_BODY" | python3 -c \
    "import sys,json; print(json.load(sys.stdin)['connectionString'])" 2>/dev/null || echo "")
  echo "  Token: $INSTANCE_TOKEN"
  echo "  ConnectionString: $CONN_STRING"
  [[ -z "$INSTANCE_TOKEN" ]] && check_fail "CHECK 3b: token missing from response body"
else
  check_fail "CHECK 3: POST /api/instances expected 201, got $CREATE_STATUS"
  docker compose logs api | tail -30
  docker compose logs provisioner | tail -30
fi

# ── CHECK 4: Query via Caddy (Northwind COUNT) ────────────────────────────────
echo ""
echo "=== CHECK 4: POST http://localhost/api/instances/{token}/query ==="
if [[ -n "$INSTANCE_TOKEN" ]]; then
  QUERY_RESP=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 30 \
    -X POST "http://localhost/api/instances/$INSTANCE_TOKEN/query" \
    -H 'Content-Type: application/json' \
    -d '{"sql":"SELECT COUNT(*) FROM products"}' \
    2>/dev/null || echo "__STATUS__:000")
  QUERY_STATUS=$(echo "$QUERY_RESP" | grep '__STATUS__:' | cut -d: -f2)
  QUERY_BODY=$(echo "$QUERY_RESP" | grep -v '__STATUS__:')
  echo "  HTTP $QUERY_STATUS  body: $QUERY_BODY"
  Q_SUCCESS=$(echo "$QUERY_BODY" | python3 -c \
    "import sys,json; print(json.load(sys.stdin).get('success',''))" 2>/dev/null || echo "")
  Q_COUNT=$(echo "$QUERY_BODY" | python3 -c \
    "import sys,json; print(json.load(sys.stdin).get('rows',[[0]])[0][0])" 2>/dev/null || echo "0")
  echo "  success=$Q_SUCCESS  products=$Q_COUNT"
  if [[ "$Q_SUCCESS" == "True" ]] && (( Q_COUNT > 0 )); then
    check_pass "CHECK 4: POST /query → success:true, products=$Q_COUNT (Northwind confirmed)"
  else
    check_fail "CHECK 4: Query failed or count=0 (success=$Q_SUCCESS count=$Q_COUNT)"
  fi
else
  check_fail "CHECK 4: Skipped (no instance token from check 3)"
fi

# ── CHECK 5: Dashboard via Caddy ──────────────────────────────────────────────
echo ""
echo "=== CHECK 5: GET http://localhost/api/dashboard → aliveCount>=1 ==="
DASH_RESP=$(curl -s -c "$COOKIES" -b "$COOKIES" \
  -w "\n__STATUS__:%{http_code}" --max-time 10 \
  http://localhost/api/dashboard 2>/dev/null || echo "__STATUS__:000")
DASH_STATUS=$(echo "$DASH_RESP" | grep '__STATUS__:' | cut -d: -f2)
DASH_BODY=$(echo "$DASH_RESP" | grep -v '__STATUS__:')
echo "  HTTP $DASH_STATUS  body: $DASH_BODY"
ALIVE=$(echo "$DASH_BODY" | python3 -c \
  "import sys,json; print(json.load(sys.stdin).get('aliveCount',0))" 2>/dev/null || echo "0")
if [[ "$DASH_STATUS" == "200" ]] && (( ALIVE >= 1 )); then
  check_pass "CHECK 5: GET /api/dashboard → 200, aliveCount=$ALIVE"
else
  check_fail "CHECK 5: Expected 200+aliveCount>=1, got status=$DASH_STATUS aliveCount=$ALIVE"
fi

# ── CHECK 6: Delete instance via Caddy ────────────────────────────────────────
echo ""
echo "=== CHECK 6: DELETE http://localhost/api/instances/{token} → 204 ==="
if [[ -n "$INSTANCE_TOKEN" ]]; then
  DEL_RESP=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 30 \
    -X DELETE "http://localhost/api/instances/$INSTANCE_TOKEN" \
    2>/dev/null || echo "__STATUS__:000")
  DEL_STATUS=$(echo "$DEL_RESP" | grep '__STATUS__:' | cut -d: -f2)
  echo "  DELETE HTTP $DEL_STATUS"
  if [[ "$DEL_STATUS" == "204" ]]; then
    check_pass "CHECK 6: DELETE /api/instances/$INSTANCE_TOKEN → 204"
    INSTANCE_TOKEN=""  # clear so cleanup trap skips extra delete attempt
    sleep 2
    REMAINING=$(docker ps -a --format '{{.Names}}' | grep '^mayfly-pg-' || true)
    if [[ -z "$REMAINING" ]]; then
      check_pass "CHECK 6b: No leftover mayfly-pg-* containers after delete"
    else
      check_fail "CHECK 6b: Leftover containers after delete: $REMAINING"
    fi
  else
    check_fail "CHECK 6: DELETE expected 204, got $DEL_STATUS"
  fi
else
  check_fail "CHECK 6: Skipped (no instance token)"
fi

# ── Summary ───────────────────────────────────────────────────────────────────
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
echo "Stack will be torn down now (via trap)."

if (( FAIL > 0 )); then
  exit 1
else
  echo "All checks passed."
  exit 0
fi
