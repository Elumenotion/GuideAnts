#!/bin/sh

PORT="${1:-59189}"
MODEL_LOG="${2:-/run/llama-server.log}"

echo "== health =="
curl -sf "http://127.0.0.1:${PORT}/health"
echo

echo "== completion =="
start_ms="$(python3 - <<'PY'
import time
print(int(time.time() * 1000))
PY
)"
curl -sf "http://127.0.0.1:${PORT}/completion" \
  -H 'Content-Type: application/json' \
  -d '{"prompt":"The capital of France is","n_predict":32,"temperature":0.1}' \
  -o /tmp/vulkan-smoke-out.json
end_ms="$(python3 - <<'PY'
import time
print(int(time.time() * 1000))
PY
)"
echo "latency_ms=$((end_ms - start_ms))"
python3 - <<'PY'
import json
with open("/tmp/vulkan-smoke-out.json", "r", encoding="utf-8") as handle:
    doc = json.load(handle)
print(doc.get("content", doc))
PY

echo "== recent vulkan log =="
grep -E 'ggml_vulkan|Vulkan|ErrorOut|D3D12|offload|n-gpu|load_tensors|timings' "$MODEL_LOG" 2>/dev/null | tail -20 || true
