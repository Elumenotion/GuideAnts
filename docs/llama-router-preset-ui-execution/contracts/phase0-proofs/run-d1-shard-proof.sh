#!/usr/bin/env bash
# Phase 0 D1 physical proof: bundled llama-server opens sharded GGUF via first-shard INI model field.
set -euo pipefail

export LD_LIBRARY_PATH="${LD_LIBRARY_PATH:-/app}"

WORKDIR="${WORKDIR:-/tmp/phase0-d1-shard-proof}"
MODEL_URL="${MODEL_URL:-https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF/resolve/main/tinyllama-1.1b-chat-v1.0.Q2_K.gguf}"
TOOLS_URL="${TOOLS_URL:-https://github.com/ggml-org/llama.cpp/releases/download/b9365/llama-b9365-bin-ubuntu-x64.tar.gz}"
LLAMA_SERVER="${LLAMA_SERVER:-/app/llama-server}"
RESULTS_FILE="${RESULTS_FILE:-/work-out/results-d1.txt}"

mkdir -p "$WORKDIR" "$(dirname "$RESULTS_FILE")"
cd "$WORKDIR"

log() {
  printf '%s\n' "$*" | tee -a "$RESULTS_FILE"
}

: >"$RESULTS_FILE"
log "D1 shard proof workdir=$WORKDIR"
log "llama-server version:"
"$LLAMA_SERVER" --version 2>&1 | tee -a "$RESULTS_FILE" || true

if [ ! -f model.Q2_K.gguf ]; then
  log "Downloading tiny single-file GGUF for split fixture..."
  curl -fL "$MODEL_URL" -o model.Q2_K.gguf
fi

if [ ! -x ./llama-gguf-split ]; then
  log "Downloading llama.cpp ubuntu-x64 tool bundle for llama-gguf-split..."
  curl -fL "$TOOLS_URL" -o tools.tgz
  tar -xzf tools.tgz
  found="$(find . -maxdepth 3 -type f -name 'llama-gguf-split' | head -1 || true)"
  if [ -z "$found" ]; then
    log "FAIL llama-gguf-split not found in $TOOLS_URL"
    exit 2
  fi
  install -m 0755 "$found" ./llama-gguf-split
fi

rm -f shard-test-*.gguf
./llama-gguf-split --split --split-max-size 250M model.Q2_K.gguf shard-test

SHARD1="$(ls -1 shard-test-00001-of-*.gguf 2>/dev/null | head -1 || true)"
SHARD2="$(ls -1 shard-test-00002-of-*.gguf 2>/dev/null | head -1 || true)"
SHARD_COUNT="$(ls -1 shard-test-00001-of-*.gguf 2>/dev/null | sed -E 's/.*-00001-of-0*([0-9]+).gguf/\1/' | head -1 || true)"
if [ -z "$SHARD1" ] || [ -z "$SHARD2" ]; then
  log "FAIL could not produce two-shard fixture via llama-gguf-split"
  ls -la shard-test* 2>/dev/null | tee -a "$RESULTS_FILE" || true
  exit 2
fi

log "SHARD1=$SHARD1"
log "SHARD2=$SHARD2"
log "SHARD_COUNT=$SHARD_COUNT"

PRESET="$WORKDIR/router-models.ini"
cat >"$PRESET" <<INI
[shard-test]
model = $WORKDIR/$SHARD1
ctx-size = 512
INI

log "INI model field (first shard only):"
grep '^model' "$PRESET" | tee -a "$RESULTS_FILE"

start_router_and_load() {
  local label="$1"
  pkill -f "$LLAMA_SERVER --models-preset $PRESET" 2>/dev/null || true
  sleep 1
  "$LLAMA_SERVER" \
    --models-preset "$PRESET" \
    --host 127.0.0.1 \
    --port 18080 \
    --no-models-autoload \
    >"$WORKDIR/router-$label.log" 2>&1 &
  local pid=$!
  local ready=0
  for _ in $(seq 1 45); do
    if curl -sf "http://127.0.0.1:18080/health" >/dev/null 2>&1; then
      ready=1
      break
    fi
    if curl -sf "http://127.0.0.1:18080/v1/models" >/dev/null 2>&1; then
      ready=1
      break
    fi
    sleep 1
  done
  if [ "$ready" -ne 1 ]; then
    log "$label router failed to become ready"
    tail -n 40 "$WORKDIR/router-$label.log" | tee -a "$RESULTS_FILE" || true
    kill "$pid" 2>/dev/null || true
    return 1
  fi
  local load_code
  load_code="$(curl -s -o "$WORKDIR/load-$label.json" -w '%{http_code}' \
    -X POST "http://127.0.0.1:18080/models/load" \
    -H 'Content-Type: application/json' \
    -d '{"model":"shard-test"}')"
  log "$label load HTTP=$load_code body=$(cat "$WORKDIR/load-$label.json")"
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  if [ "$load_code" = "200" ]; then
    return 0
  fi
  return 1
}

if start_router_and_load complete; then
  log "COMPLETE_SET=PASS"
else
  log "COMPLETE_SET=FAIL"
  tail -n 40 "$WORKDIR/router-complete.log" | tee -a "$RESULTS_FILE" || true
fi

while IFS= read -r extra_shard; do
  [ -n "$extra_shard" ] || continue
  mv "$extra_shard" "$extra_shard.missing"
done < <(ls -1 shard-test-0000[2-9]-of-*.gguf shard-test-000[1-9][0-9]-of-*.gguf 2>/dev/null || true)
if start_router_and_load missing; then
  log "MISSING_SHARD=FAIL expected load failure when trailing shard(s) absent"
  while IFS= read -r renamed; do
    [ -n "$renamed" ] || continue
    mv "$renamed" "${renamed%.missing}"
  done < <(ls -1 *.missing 2>/dev/null || true)
  exit 3
else
  log "MISSING_SHARD=PASS explicit load failure with absent trailing shard(s)"
  while IFS= read -r renamed; do
    [ -n "$renamed" ] || continue
    mv "$renamed" "${renamed%.missing}"
  done < <(ls -1 *.missing 2>/dev/null || true)
fi

log "D1 proof finished"
