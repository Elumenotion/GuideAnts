#!/usr/bin/env bash
# Phase 0 D4 spike: revisioned fleet projection consumption without product changes.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONTRACTS_DIR="${CONTRACTS_DIR:-$(cd "$SCRIPT_DIR/.." && pwd)}"
WORKDIR="${WORKDIR:-/tmp/phase0-d4-fleet-proof}"
PROJECTION_DIR="$WORKDIR/runtime/fleet"
PROJECTION_FILE="$PROJECTION_DIR/fleet-projection.json"
PROJECTION_TMP="$PROJECTION_DIR/fleet-projection.json.tmp"
RESULTS_FILE="${RESULTS_FILE:-/work/results-d4.txt}"

ALIAS_FORBIDDEN_KEYS=(
  ctx-size
  cache-ram
  image-min-tokens
  spec-type
  spec-draft-n-max
)

mkdir -p "$PROJECTION_DIR" "$(dirname "$RESULTS_FILE")"
: >"$RESULTS_FILE"

log() {
  printf '%s\n' "$*" | tee -a "$RESULTS_FILE"
}

atomic_write_projection() {
  local source_json="$1"
  cp "$source_json" "$PROJECTION_TMP"
  sync "$PROJECTION_TMP"
  mv -f "$PROJECTION_TMP" "$PROJECTION_FILE"
}

apply_projection_to_env() {
  local file="$1"
  eval "$(/opt/venv/bin/python - <<'PY' "$file"
import json, shlex, sys
path = sys.argv[1]
doc = json.load(open(path, "r", encoding="utf-8"))
fleet = doc.get("fleetEnv") or {}
for key, value in fleet.items():
    print(f"export {key}={shlex.quote(str(value))}")
print("printf '%s' " + shlex.quote(json.dumps({
    "desiredRevision": doc.get("desiredRevision"),
    "appliedRevision": doc.get("appliedRevision"),
    "applyStatus": doc.get("applyStatus"),
    "applyError": doc.get("applyError"),
})))
PY
)"
}

build_fleet_args() {
  ARGS=""
  ROUTER_MODE=0
  if [ -n "${GA_LLAMA_MODELS_PRESET:-}" ]; then
    ARGS="$ARGS --models-preset ${GA_LLAMA_MODELS_PRESET}"
    ROUTER_MODE=1
  fi
  [ -n "${GA_LLAMA_MODELS_MAX:-}" ] && ARGS="$ARGS --models-max ${GA_LLAMA_MODELS_MAX}"
  [ "${GA_LLAMA_NO_AUTOLOAD:-}" = "1" ] && ARGS="$ARGS --no-models-autoload"
  if [ "$ROUTER_MODE" = "0" ]; then
    [ -n "${GA_LLAMA_CTX_SIZE:-}" ] && ARGS="$ARGS --ctx-size ${GA_LLAMA_CTX_SIZE}"
    [ -n "${GA_LLAMA_CACHE_RAM:-}" ] && ARGS="$ARGS --cache-ram ${GA_LLAMA_CACHE_RAM}"
  fi
  [ -n "${GA_LLAMA_THREADS:-}" ] && ARGS="$ARGS --threads ${GA_LLAMA_THREADS}"
  [ -n "${GA_LLAMA_PARALLEL:-}" ] && ARGS="$ARGS --parallel ${GA_LLAMA_PARALLEL}"
  [ "${GA_LLAMA_JINJA:-}" = "1" ] && ARGS="$ARGS --jinja"
  [ "${GA_LLAMA_KV_UNIFIED:-}" = "1" ] && ARGS="$ARGS --kv-unified"
  [ "${GA_LLAMA_CONT_BATCH:-}" = "1" ] && ARGS="$ARGS --cont-batching"
  [ -n "${GA_LLAMA_FLASH_ATTN:-}" ] && ARGS="$ARGS --flash-attn ${GA_LLAMA_FLASH_ATTN}"
  printf '%s' "$ARGS"
}

contains_alias_key() {
  local args="$1"
  local key="$2"
  case " $args " in
    *" --${key} "*) return 0 ;;
    *" --${key}="*) return 0 ;;
  esac
  return 1
}

spawn_read_projection() {
  local label="$1"
  local meta
  meta="$(/opt/venv/bin/python - <<'PY' "$PROJECTION_FILE"
import json, sys
doc = json.load(open(sys.argv[1], "r", encoding="utf-8"))
print(json.dumps({
    "desiredRevision": doc.get("desiredRevision"),
    "appliedRevision": doc.get("appliedRevision"),
    "applyStatus": doc.get("applyStatus"),
    "applyError": doc.get("applyError"),
}))
PY
)"
  apply_projection_to_env "$PROJECTION_FILE"
  local args
  args="$(build_fleet_args)"
  log "$label meta=$meta"
  log "$label args=$args"
  for key in "${ALIAS_FORBIDDEN_KEYS[@]}"; do
    if contains_alias_key "$args" "$key"; then
      log "FAIL alias key leaked into fleet args: $key"
      exit 4
    fi
  done
}

log "D4 fleet projection spike"
log "NOTE: product start-llama.sh currently reads GA_LLAMA_* env directly; projection wiring lands in Phase 3."

REVISION1="$CONTRACTS_DIR/runtime-fleet-projection.fixture.json"
REVISION2="$WORKDIR/fleet-revision-3.json"
cp "$REVISION1" "$WORKDIR/fleet-revision-2.json"
/opt/venv/bin/python - <<'PY' "$REVISION1" "$REVISION2"
import json, sys
src, dst = sys.argv[1], sys.argv[2]
doc = json.load(open(src, "r", encoding="utf-8"))
doc["revision"] = 3
doc["desiredRevision"] = 3
doc["appliedRevision"] = 2
doc["applyStatus"] = "pending_restart"
doc["applyError"] = None
doc["fleetEnv"]["GA_LLAMA_PARALLEL"] = "7"
json.dump(doc, open(dst, "w", encoding="utf-8"), indent=2)
PY

atomic_write_projection "$WORKDIR/fleet-revision-2.json"
spawn_read_projection "spawn-rev2"

# Simulate SIGTERM/respawn by re-reading after atomic replace with stale applied revision.
atomic_write_projection "$REVISION2"
spawn_read_projection "spawn-rev3-stale-applied"

meta="$(/opt/venv/bin/python - <<'PY' "$PROJECTION_FILE"
import json, sys
doc = json.load(open(sys.argv[1], "r", encoding="utf-8"))
print(json.dumps({
    "desiredRevision": doc.get("desiredRevision"),
    "appliedRevision": doc.get("appliedRevision"),
    "applyStatus": doc.get("applyStatus"),
    "applyError": doc.get("applyError"),
}))
PY
)"
apply_projection_to_env "$PROJECTION_FILE"
if echo "$meta" | grep -q '"desiredRevision": 3' && echo "$meta" | grep -q '"appliedRevision": 2'; then
  log "DESIRED_APPLIED_OBSERVABLE=PASS mismatch visible before restart confirmation"
else
  log "DESIRED_APPLIED_OBSERVABLE=FAIL"
  exit 5
fi

# Simulate llama-admin apply confirmation updating appliedRevision.
/opt/venv/bin/python - <<'PY' "$PROJECTION_FILE"
import json, sys
path = sys.argv[1]
doc = json.load(open(path, "r", encoding="utf-8"))
doc["appliedRevision"] = doc["desiredRevision"]
doc["applyStatus"] = "applied"
doc["applyError"] = None
json.dump(doc, open(path, "w", encoding="utf-8"), indent=2)
PY
meta="$(/opt/venv/bin/python - <<'PY' "$PROJECTION_FILE"
import json, sys
doc = json.load(open(sys.argv[1], "r", encoding="utf-8"))
print(json.dumps({
    "desiredRevision": doc.get("desiredRevision"),
    "appliedRevision": doc.get("appliedRevision"),
    "applyStatus": doc.get("applyStatus"),
    "applyError": doc.get("applyError"),
}))
PY
)"
apply_projection_to_env "$PROJECTION_FILE"
if echo "$meta" | grep -q '"appliedRevision": 3' && echo "$meta" | grep -q '"applyStatus": "applied"'; then
  log "APPLY_CONFIRMATION=PASS"
else
  log "APPLY_CONFIRMATION=FAIL"
  exit 6
fi

log "PROJECTION=PASS atomic replace + respawn-read spike"
log "ATOMIC_REPLACE=PASS mv tmp->final"
log "ALIAS_KEYS_EXCLUDED=PASS"
log "D4 spike finished"
