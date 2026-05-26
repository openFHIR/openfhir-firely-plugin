#!/bin/bash
set -e

# ── Colours ──────────────────────────────────────────────────────────────────
RESET=$(tput sgr0)
BOLD=$(tput bold)
GREEN=$(tput setaf 2)
CYAN=$(tput setaf 6)
YELLOW=$(tput setaf 3)
RED=$(tput setaf 1)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
COLLECTION="$SCRIPT_DIR/openFHIR Firely Plugin Tests.postman_collection.json"

# ── Helpers ───────────────────────────────────────────────────────────────────
log()  { echo -e "${CYAN}${BOLD}[test.sh]${RESET} $*"; }
ok()   { echo -e "${GREEN}${BOLD}[test.sh] ✓ $*${RESET}"; }
warn() { echo -e "${YELLOW}${BOLD}[test.sh] ⚠ $*${RESET}"; }
fail() { echo -e "${RED}${BOLD}[test.sh] ✗ $*${RESET}"; exit 1; }

wait_for_http() {
    local url=$1
    local label=${2:-"$url"}
    local timeout=${3:-120}
    log "Waiting for $label to be ready..."
    local elapsed=0
    until curl -sf "$url" > /dev/null 2>&1; do
        sleep 2
        elapsed=$((elapsed + 2))
        if [ $elapsed -ge $timeout ]; then
            fail "Timed out waiting for $label after ${timeout}s"
        fi
    done
    ok "$label is ready"
}

wait_for_http_not_status() {
    local url=$1
    local bad_status=$2
    local label=${3:-"$url"}
    local timeout=${4:-180}
    log "Waiting for $label to leave HTTP $bad_status..."
    local elapsed=0
    until [ "$(curl -s -o /dev/null -w '%{http_code}' "$url")" != "$bad_status" ]; do
        sleep 2
        elapsed=$((elapsed + 2))
        if [ $elapsed -ge $timeout ]; then
            fail "Timed out waiting for $label to leave HTTP $bad_status after ${timeout}s"
        fi
    done
    ok "$label is ready"
}

wait_for_log() {
    local container=$1
    local pattern=$2
    local timeout=${3:-120}
    log "Waiting for container '$container' to log: $pattern"
    local elapsed=0
    until docker logs "$container" 2>&1 | grep -q "$pattern"; do
        sleep 2
        elapsed=$((elapsed + 2))
        if [ $elapsed -ge $timeout ]; then
            fail "Timed out waiting for '$container' log pattern after ${timeout}s"
        fi
    done
    ok "Container '$container' is ready"
}

cleanup() {
    log "Tearing down containers..."
    cd "$SCRIPT_DIR"
    docker compose down 2>/dev/null || true
}

# ── Preflight checks ──────────────────────────────────────────────────────────
command -v docker   > /dev/null || fail "'docker' not found in PATH"
command -v newman   > /dev/null || fail "'newman' not found — install with: npm install -g newman"
command -v dotnet   > /dev/null || fail "'dotnet' not found in PATH"

if [ ! -f "$SCRIPT_DIR/licenses/firely-license.json" ]; then
    fail "Missing $SCRIPT_DIR/licenses/firely-license.json — copy your Firely license here before running tests"
fi
if [ ! -f "$SCRIPT_DIR/licenses/openfhir-license.json" ]; then
    fail "Missing $SCRIPT_DIR/licenses/openfhir-license.json — copy your openFHIR license here before running tests"
fi

# ── Step 1: Build the plugin ──────────────────────────────────────────────────
log "Building OpenFhirFirelyPlugin..."
dotnet build "$REPO_ROOT/src/OpenFhirFirelyPlugin/OpenFhirFirelyPlugin.csproj" \
    --configuration Release \
    --nologo \
    -v quiet
ok "Plugin built → $REPO_ROOT/plugins/"

# ── Step 2: Bring up docker-compose ──────────────────────────────────────────
cd "$SCRIPT_DIR"

# Ensure any previous run is cleaned up
docker compose down --remove-orphans 2>/dev/null || true

log "Starting containers..."
docker compose up -d

# Register cleanup on exit (success or failure)
trap cleanup EXIT

# ── Step 3: Wait for all services ────────────────────────────────────────────
wait_for_log  "mongodb-test"       "Waiting for connections"          60
wait_for_log  "ehrbase-postgres-test" "database system is ready"      60
wait_for_http "http://localhost:8081/ehrbase/rest/openehr/v1/definition/template/adl1.4" \
              "EHRbase"            180
wait_for_http "http://localhost:8080/health" \
              "openFHIR"          180
wait_for_http "http://localhost:4080/metadata" \
              "Firely" 1800

# ── Step 4: Run Newman ────────────────────────────────────────────────────────
log "Running Postman collection with Newman..."
newman run "$COLLECTION" \
    --reporters cli \
    --bail

ok "All Newman tests passed!"

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}${BOLD}=========================================================${RESET}"
echo -e "${GREEN}${BOLD}         Integration Tests: All Passed!                  ${RESET}"
echo -e "${GREEN}${BOLD}=========================================================${RESET}"
echo -e "${CYAN}  Collection: openFHIR Firely Plugin Tests${RESET}"
echo -e "${CYAN}  Covers:${RESET}"
echo -e "${CYAN}    • openFHIR state preparation (OPTs, models, contexts)${RESET}"
echo -e "${CYAN}    • Patient create R4 + STU3 → EHR provisioned in EHRbase${RESET}"
echo -e "${CYAN}    • EHR identifier present in 201 response and on re-read${RESET}"
echo -e "${CYAN}    • AllergyIntolerance STU3+R4 → stored as openEHR composition${RESET}"
echo -e "${CYAN}    • FHIR query ← openEHR (AllergyIntolerance search)${RESET}"
echo -e "${CYAN}    • \$summary operation → IPS Bundle with Composition + AI${RESET}"
echo -e "${GREEN}${BOLD}=========================================================${RESET}"
