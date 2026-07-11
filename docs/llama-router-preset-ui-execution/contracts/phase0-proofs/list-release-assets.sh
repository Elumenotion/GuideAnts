#!/usr/bin/env bash
set -euo pipefail
curl -sL 'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest' > /tmp/release.json
/opt/venv/bin/python - <<'PY'
import json
r = json.load(open('/tmp/release.json'))
print('tag', r.get('tag_name'))
for a in r.get('assets', []):
    n = a.get('name', '')
    if 'split' in n.lower() or 'ubuntu' in n.lower() or 'linux' in n.lower():
        print(n, a.get('browser_download_url'))
PY
