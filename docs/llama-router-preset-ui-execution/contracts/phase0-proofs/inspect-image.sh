#!/usr/bin/env bash
set -euo pipefail
ls -la /app/*.so 2>/dev/null || true
find /app -maxdepth 2 -name 'llama-gguf-split' 2>/dev/null || true
curl -sL 'https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b9935' | /opt/venv/bin/python - <<'PY'
import json, sys
r = json.load(sys.stdin)
for a in r.get('assets', []):
    print(a.get('name'))
PY
