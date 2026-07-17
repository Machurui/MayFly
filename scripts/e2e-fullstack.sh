#!/usr/bin/env bash
# MayFly full-stack end-to-end smoke test — all five engines + import.
# Builds all images, brings up the complete stack, exercises checks through
# Caddy on http://localhost for each engine (postgres/mysql/mariadb/mssql/mongo),
# then tears everything down on EXIT.
#
# Preamble checks (run once):
#   1. GET /               → 200, body contains <div id="app">  (Vue SPA served)
#   2. GET /api/instances  → 200 (empty array OK)               (Caddy→API routing)
#
# Per-engine checks (run sequentially; each DB is deleted before the next is created):
#   A. POST /api/instances {engine}  → 201 with token + connectionString
#   B. Egress probe (best-effort)    → DB container has no outbound internet
#   C. POST /api/instances/{token}/query  → success:true [, count>0 for Northwind / output contains value for mongo]
#   D. DELETE /api/instances/{token} → 204; no leftover DB/sidecar containers
#
# Import checks (postgres + mongo, run sequentially after per-engine checks):
#   A. POST /api/instances {engine, blank}         → 201 + token
#   B. POST /api/instances/{token}/import (dump)   → success:true
#   C. POST /api/instances/{token}/query           → restored data confirmed
#   D. DELETE /api/instances/{token}               → 204
#
# Engines: postgres (Northwind seed), mysql (blank), mariadb (blank), mssql (blank, 300s), mongo (blank)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COOKIES=/tmp/mayfly-fs-cookies-$$.txt
PASS=0
FAIL=0
ERRORS=()

# ── Cleanup: ALWAYS runs on EXIT ──────────────────────────────────────────────
cleanup() {
  echo ""
  echo "=== CLEANUP ==="
  cd "$REPO"
  docker compose down -v 2>/dev/null || true
  # Remove provisioner-managed containers (DB containers are always named mayfly-pg-<id>
  # regardless of engine; sidecars are mayfly-sidecar-<id>; writer containers are transient)
  REMAINING=$(docker ps -a --format '{{.Names}}' | grep -E '^mayfly-(pg|sidecar|initwriter)-' || true)
  if [[ -n "$REMAINING" ]]; then
    echo "  Force-removing leftover containers: $REMAINING"
    echo "$REMAINING" | xargs docker rm -f 2>/dev/null || true
  fi
  # Remove provisioner-managed volumes (data volumes + credential-bearing init volumes)
  LINGERING_VOLS=$(docker volume ls --format '{{.Name}}' | grep -E '^mayfly-(vol|init)-' || true)
  if [[ -n "$LINGERING_VOLS" ]]; then
    echo "  Removing leftover volumes: $LINGERING_VOLS"
    echo "$LINGERING_VOLS" | xargs docker volume rm -f 2>/dev/null || true
  fi
  # Remove user-network and ingress network created by provisioner
  docker network rm mayfly-users mayfly-ingress mayfly-internal 2>/dev/null || true
  rm -f "$COOKIES"
  echo "  Cleanup done — no leftover mayfly containers, volumes, or networks."
}
trap cleanup EXIT

# ── Require PROVISIONER_KEY ────────────────────────────────────────────────────
# docker-compose.yml uses ${PROVISIONER_KEY:?...} — the compose will fail without it.
export PROVISIONER_KEY="${PROVISIONER_KEY:-$(openssl rand -hex 16)}"
echo "PROVISIONER_KEY set (length=${#PROVISIONER_KEY})"

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

# ── test_engine <engine> <storageMb> <initialData> <query> <expect> ───────────
# <expect>: "count"          → assert rows[0][0] > 0 (Northwind seed check)
#           "success"        → assert success:true only
#           "output:<substr>"→ assert success:true AND output field contains <substr> (mongo)
# Egress probe is best-effort: WARN+SKIP if probe tool unavailable; REACHED is a hard fail.
test_engine() {
  local engine="$1" storageMb="$2" initialData="$3" query="$4" expect="$5"
  local label="[${engine}]"
  local token=""

  echo ""
  echo "============================================"
  echo "ENGINE: $engine (storageMb=$storageMb, initialData=$initialData)"
  echo "============================================"

  # --- A: Create ---
  local max_time=180
  # mssql runs emulated (amd64 on arm64) → slow provision; use a generous timeout
  [[ "$engine" == "mssql" ]] && max_time=300

  echo "${label} A: POST /api/instances"
  local create_resp create_status create_body
  create_resp=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time "$max_time" \
    -X POST http://localhost/api/instances \
    -H 'Content-Type: application/json' \
    -d "{\"engine\":\"${engine}\",\"ttlHours\":3,\"storageMb\":${storageMb},\"initialData\":\"${initialData}\"}" \
    2>/dev/null || echo "__STATUS__:000")
  create_status=$(echo "$create_resp" | grep '__STATUS__:' | cut -d: -f2)
  create_body=$(echo "$create_resp" | grep -v '__STATUS__:')
  echo "  HTTP $create_status"
  echo "  Body: $create_body"

  if [[ "$create_status" != "201" ]]; then
    check_fail "${label} A: create → expected 201, got $create_status"
    docker compose logs api | tail -30
    docker compose logs provisioner | tail -30
    return
  fi
  check_pass "${label} A: create → 201"

  token=$(echo "$create_body" | python3 -c \
    "import sys,json; print(json.load(sys.stdin)['token'])" 2>/dev/null || echo "")
  echo "  Token: $token"
  if [[ -z "$token" ]]; then
    check_fail "${label} A: token missing from response body"
    return
  fi

  # For mongo, validate connectionString scheme
  if [[ "$engine" == "mongo" ]]; then
    local conn_string
    conn_string=$(echo "$create_body" | python3 -c \
      "import sys,json; print(json.load(sys.stdin).get('connectionString',''))" 2>/dev/null || echo "")
    if [[ ! "$conn_string" =~ ^mongodb:// ]]; then
      check_fail "${label} A: connectionString does not start with 'mongodb://' (got: $conn_string)"
      return
    fi
    echo "  connectionString scheme validated: mongodb://"
  fi

  # --- B: Egress probe (engine-agnostic, best-effort) ---
  echo ""
  echo "${label} B: Egress probe (best-effort — hard proof is in integration tests)"
  # Find DB container by label mayfly.role=db (engine-agnostic)
  local db_container
  db_container=$(docker ps --format '{{.Names}}' --filter 'label=mayfly.role=db' | head -1 || true)
  if [[ -n "$db_container" ]]; then
    echo "  Probing from container: $db_container"
    # Primary: bash /dev/tcp (portable built-in; works on alpine and Ubuntu images)
    local egress_out
    egress_out=$(docker exec "$db_container" bash -c \
      'timeout 3 bash -c "echo > /dev/tcp/1.1.1.1/53" && echo REACHED || echo NOEGRESS' \
      2>/dev/null || true)
    # Fallback: nc (if bash or /dev/tcp unavailable)
    if [[ -z "$egress_out" ]]; then
      egress_out=$(docker exec "$db_container" sh -c \
        '(nc -w2 1.1.1.1 80 2>/dev/null && echo REACHED) || echo NOEGRESS' \
        2>/dev/null || true)
    fi
    echo "  Probe output: ${egress_out:-<unavailable>}"
    if [[ -z "$egress_out" ]]; then
      echo "  WARN: probe tool unavailable in $engine image — skipping egress assertion"
    elif echo "$egress_out" | grep -q "REACHED"; then
      check_fail "${label} B: Egress probe → REACHED — DB container has internet access!"
    else
      check_pass "${label} B: Egress probe → NOEGRESS (internal network confirmed)"
    fi
  else
    echo "  WARN: could not find DB container by label mayfly.role=db — skipping egress probe"
  fi

  # --- C: Query ---
  echo ""
  echo "${label} C: POST /api/instances/$token/query  query=\"$query\""
  # Build JSON via Python so any quotes/special chars in $query are properly escaped.
  local query_json
  query_json=$(python3 -c "import json,sys; print(json.dumps({'query': sys.argv[1]}))" "$query")
  local query_resp query_status query_body
  query_resp=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 30 \
    -X POST "http://localhost/api/instances/$token/query" \
    -H 'Content-Type: application/json' \
    -d "$query_json" \
    2>/dev/null || echo "__STATUS__:000")
  query_status=$(echo "$query_resp" | grep '__STATUS__:' | cut -d: -f2)
  query_body=$(echo "$query_resp" | grep -v '__STATUS__:')
  echo "  HTTP $query_status  body: $query_body"

  local q_success
  q_success=$(echo "$query_body" | python3 -c \
    "import sys,json; print(json.load(sys.stdin).get('success',''))" 2>/dev/null || echo "")

  if [[ "$expect" == "count" ]]; then
    local q_count
    q_count=$(echo "$query_body" | python3 -c \
      "import sys,json; print(json.load(sys.stdin).get('rows',[[0]])[0][0])" 2>/dev/null || echo "0")
    echo "  success=$q_success  count=$q_count"
    if [[ "$q_success" == "True" ]] && (( q_count > 0 )); then
      check_pass "${label} C: query → success:true, count=$q_count (Northwind confirmed)"
    else
      check_fail "${label} C: query → failed or count=0 (success=$q_success count=$q_count)"
    fi
  elif [[ "$expect" == output:* ]]; then
    # mongo: check success:true AND that the output field contains the expected substring
    local expect_substr="${expect#output:}"
    local q_output
    q_output=$(echo "$query_body" | python3 -c \
      "import sys,json; print(json.load(sys.stdin).get('output',''))" 2>/dev/null || echo "")
    echo "  success=$q_success  output=$q_output"
    if [[ "$q_success" == "True" ]] && echo "$q_output" | grep -qF "$expect_substr"; then
      check_pass "${label} C: query → success:true, output contains '$expect_substr'"
    else
      check_fail "${label} C: query → failed (success=$q_success, output='$q_output', expected to contain '$expect_substr')"
    fi
  else
    echo "  success=$q_success"
    if [[ "$q_success" == "True" ]]; then
      check_pass "${label} C: query → success:true"
    else
      check_fail "${label} C: query → failed (success=$q_success)"
    fi
  fi

  # --- D: Destroy ---
  echo ""
  echo "${label} D: DELETE /api/instances/$token"
  local del_resp del_status
  del_resp=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 30 \
    -X DELETE "http://localhost/api/instances/$token" \
    2>/dev/null || echo "__STATUS__:000")
  del_status=$(echo "$del_resp" | grep '__STATUS__:' | cut -d: -f2)
  echo "  DELETE HTTP $del_status"
  if [[ "$del_status" == "204" ]]; then
    check_pass "${label} D: destroy → 204"
    sleep 2
    # Check no leftover DB or sidecar containers (all DB containers named mayfly-pg-<id>)
    local remaining
    remaining=$(docker ps -a --format '{{.Names}}' | grep -E '^mayfly-(pg|sidecar)-' || true)
    if [[ -z "$remaining" ]]; then
      check_pass "${label} D: no leftover DB/sidecar containers after delete"
    else
      check_fail "${label} D: leftover containers after delete: $remaining"
    fi
  else
    check_fail "${label} D: destroy → expected 204, got $del_status"
  fi
}

# ── test_import_engine <engine> ───────────────────────────────────────────────
# Tests the import (dump restore) endpoint for a given engine (postgres|mongo).
# Steps: create (blank) → write dump → upload multipart → assert success:true →
#        query restored data → assert result → destroy.
test_import_engine() {
  local engine="$1"
  local label="[${engine}:import]"
  local token="" dumpfile=""

  echo ""
  echo "============================================"
  echo "IMPORT TEST: $engine"
  echo "============================================"

  # --- A: Create blank instance ---
  echo "${label} A: POST /api/instances (blank, ttlHours=3, storageMb=256)"
  local create_resp create_status create_body
  create_resp=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 180 \
    -X POST http://localhost/api/instances \
    -H 'Content-Type: application/json' \
    -d "{\"engine\":\"${engine}\",\"ttlHours\":3,\"storageMb\":256,\"initialData\":\"blank\"}" \
    2>/dev/null || echo "__STATUS__:000")
  create_status=$(echo "$create_resp" | grep '__STATUS__:' | cut -d: -f2)
  create_body=$(echo "$create_resp" | grep -v '__STATUS__:')
  echo "  HTTP $create_status"
  echo "  Body: $create_body"

  if [[ "$create_status" != "201" ]]; then
    check_fail "${label} A: create → expected 201, got $create_status"
    return
  fi
  check_pass "${label} A: create → 201"

  token=$(echo "$create_body" | python3 -c \
    "import sys,json; print(json.load(sys.stdin)['token'])" 2>/dev/null || echo "")
  echo "  Token: $token"
  if [[ -z "$token" ]]; then
    check_fail "${label} A: token missing from response body"
    return
  fi

  # --- B: Write dump to temp file and upload ---
  if [[ "$engine" == "postgres" ]]; then
    dumpfile=$(mktemp /tmp/mayfly-import-$$.XXXXXX.sql)
    printf 'CREATE TABLE imp(id int);\nINSERT INTO imp VALUES (1),(2),(3);\n' > "$dumpfile"
  else
    # mongo
    dumpfile=$(mktemp /tmp/mayfly-import-$$.XXXXXX.js)
    printf "db.getSiblingDB('appdb').getCollection('imp').insertMany([{_id:1},{_id:2},{_id:3}]);\n" \
      > "$dumpfile"
  fi
  echo ""
  echo "${label} B: POST /api/instances/$token/import (file=$(basename "$dumpfile"))"
  local import_resp import_status import_body
  import_resp=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 60 \
    -F "file=@${dumpfile}" \
    "http://localhost/api/instances/$token/import" \
    2>/dev/null || echo "__STATUS__:000")
  import_status=$(echo "$import_resp" | grep '__STATUS__:' | cut -d: -f2)
  import_body=$(echo "$import_resp" | grep -v '__STATUS__:')
  rm -f "$dumpfile"
  echo "  HTTP $import_status  body: $import_body"

  local imp_success
  imp_success=$(echo "$import_body" | python3 -c \
    "import sys,json; print(json.load(sys.stdin).get('success',''))" 2>/dev/null || echo "")
  echo "  import success=$imp_success"
  if [[ "$imp_success" == "True" ]]; then
    check_pass "${label} B: import → success:true"
  else
    check_fail "${label} B: import → failed (HTTP $import_status, success=$imp_success)"
    docker compose logs api | tail -20
    docker compose logs provisioner | tail -20
    # Still attempt destroy before returning
    curl -s -c "$COOKIES" -b "$COOKIES" -X DELETE "http://localhost/api/instances/$token" \
      --max-time 30 2>/dev/null || true
    return
  fi

  # --- C: Query restored data ---
  echo ""
  local query
  if [[ "$engine" == "postgres" ]]; then
    query="SELECT COUNT(*) FROM imp"
  else
    query="print(db.getCollection('imp').countDocuments())"
  fi
  echo "${label} C: POST /api/instances/$token/query  query=\"$query\""
  local query_json
  query_json=$(python3 -c "import json,sys; print(json.dumps({'query': sys.argv[1]}))" "$query")
  local query_resp query_status query_body
  query_resp=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 30 \
    -X POST "http://localhost/api/instances/$token/query" \
    -H 'Content-Type: application/json' \
    -d "$query_json" \
    2>/dev/null || echo "__STATUS__:000")
  query_status=$(echo "$query_resp" | grep '__STATUS__:' | cut -d: -f2)
  query_body=$(echo "$query_resp" | grep -v '__STATUS__:')
  echo "  HTTP $query_status  body: $query_body"

  local q_success
  q_success=$(echo "$query_body" | python3 -c \
    "import sys,json; print(json.load(sys.stdin).get('success',''))" 2>/dev/null || echo "")

  if [[ "$engine" == "postgres" ]]; then
    local q_count
    q_count=$(echo "$query_body" | python3 -c \
      "import sys,json; print(json.load(sys.stdin).get('rows',[[0]])[0][0])" 2>/dev/null || echo "0")
    echo "  success=$q_success  count=$q_count"
    if [[ "$q_success" == "True" ]] && [[ "$q_count" == "3" ]]; then
      check_pass "${label} C: query restored data → success:true, count=3"
    else
      check_fail "${label} C: query restored data → failed (success=$q_success, count=$q_count)"
    fi
  else
    # mongo: output field should contain "3"
    local q_output
    q_output=$(echo "$query_body" | python3 -c \
      "import sys,json; print(json.load(sys.stdin).get('output',''))" 2>/dev/null || echo "")
    echo "  success=$q_success  output=$q_output"
    if [[ "$q_success" == "True" ]] && echo "$q_output" | grep -qF "3"; then
      check_pass "${label} C: query restored data → success:true, output contains '3'"
    else
      check_fail "${label} C: query restored data → failed (success=$q_success, output='$q_output')"
    fi
  fi

  # --- D: Destroy ---
  echo ""
  echo "${label} D: DELETE /api/instances/$token"
  local del_resp del_status
  del_resp=$(curl -s -c "$COOKIES" -b "$COOKIES" \
    -w "\n__STATUS__:%{http_code}" --max-time 30 \
    -X DELETE "http://localhost/api/instances/$token" \
    2>/dev/null || echo "__STATUS__:000")
  del_status=$(echo "$del_resp" | grep '__STATUS__:' | cut -d: -f2)
  echo "  DELETE HTTP $del_status"
  if [[ "$del_status" == "204" ]]; then
    check_pass "${label} D: destroy → 204"
    sleep 2
    local remaining
    remaining=$(docker ps -a --format '{{.Names}}' | grep -E '^mayfly-(pg|sidecar)-' || true)
    if [[ -z "$remaining" ]]; then
      check_pass "${label} D: no leftover DB/sidecar containers after delete"
    else
      check_fail "${label} D: leftover containers after delete: $remaining"
    fi
  else
    check_fail "${label} D: destroy → expected 204, got $del_status"
  fi
}

echo "============================================"
echo "MayFly Full-Stack E2E — All Engines + Import"
echo "  Entry point: http://localhost"
echo "  Engines: postgres / mysql / mariadb / mssql / mongo"
echo "============================================"
echo ""

cd "$REPO"

# ── STEP 1: Build all images and start the full stack ─────────────────────────
echo "=== STEP 1: docker compose up -d --build (may take several minutes first run) ==="
# Pre-remove any stale provisioner-managed networks (e.g. left by integration tests)
# so compose can (re-)create them with the correct compose labels.
docker network rm mayfly-users mayfly-ingress mayfly-internal 2>/dev/null || true
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
# NOTE: No out-of-band migration step. The API applies EF migrations at startup
# via db.Database.Migrate() in Program.cs before serving any request.

# ── STEP 3: Wait for Caddy to accept requests ─────────────────────────────────
echo ""
echo "=== STEP 3: Wait for Caddy + API readiness ==="
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
echo "Stack ready. Running preamble checks."
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

# ── Per-engine checks: create → egress → query → destroy ──────────────────────
echo ""
echo "============================================"
echo "Per-engine checks (sequential, 1 DB at a time)"
echo "============================================"

# postgres: Northwind seed — verify data was initialised (SELECT COUNT(*) FROM products, count>0)
test_engine postgres  256  northwind  "SELECT COUNT(*) FROM products"  count

# mysql: blank — verify DB + appuser work (SELECT 1, success:true)
test_engine mysql     256  blank      "SELECT 1"                        success

# mariadb: blank — verify DB + appuser work (SELECT 1, success:true)
test_engine mariadb   256  blank      "SELECT 1"                        success

# mssql: blank, 1 GiB (mssql 2 GiB memory floor), longer provision timeout (emulated on arm64)
test_engine mssql     1024 blank      "SELECT 1"                        success

# mongo: blank — verify mongosh JS exec (insert + count; response uses output field, not rows)
test_engine mongo     256  blank      "db.getCollection('t').insertOne({x:1}); print(\"COUNT=\"+db.getCollection('t').countDocuments())"  "output:COUNT=1"

# ── Seeded-template checks (one SQL engine, one NoSQL engine) ─────────────────
# postgres + blog: verify posts table was seeded (5 rows expected → count > 0)
test_engine postgres  256  blog       "SELECT COUNT(*) FROM posts"  count

# mongo + blog: verify posts collection was seeded (5 docs expected)
test_engine mongo     256  blog       "print(\"POSTS=\"+db.getCollection('posts').countDocuments())"  "output:POSTS=5"

# ── Import (dump restore) checks ──────────────────────────────────────────────
echo ""
echo "============================================"
echo "Import (dump restore) checks"
echo "============================================"

# postgres: upload a small SQL dump; appuser is granted access after restore;
# query confirms data was restored (COUNT(*) FROM imp = 3)
test_import_engine postgres

# mongo: upload a JS dump; query confirms data was restored (countDocuments = 3)
test_import_engine mongo

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
