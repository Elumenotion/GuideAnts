#!/usr/bin/env bash
set -euo pipefail
/opt/venv/bin/python - <<'PY'
import json, urllib.request
repos = [
    'QuantFactory/Qwen2.5-7B-Instruct-GGUF',
    'bartowski/Meta-Llama-3.1-8B-Instruct-GGUF',
    'RichardErkhov/Phi-3.5-mini-instruct-gguf',
    'TheBloke/phi-2-GGUF',
]
for repo in repos:
    try:
        data = json.load(urllib.request.urlopen(f'https://huggingface.co/api/models/{repo}/tree/main', timeout=30))
    except Exception as exc:
        print('ERR', repo, exc)
        continue
    shards = [(item.get('size'), item.get('path')) for item in data if '00001-of-00002' in item.get('path', '')]
    if shards:
        print('REPO', repo)
        for entry in shards[:2]:
            print(' ', entry)
PY
