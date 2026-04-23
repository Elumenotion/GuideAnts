#!/usr/bin/env bash
set -euo pipefail

python /opt/browser-render/browser_api.py &
browser_pid=$!

/usr/local/searxng/entrypoint.sh &
searx_pid=$!

cleanup() {
  kill -TERM "$browser_pid" "$searx_pid" 2>/dev/null || true
}

trap cleanup INT TERM

while kill -0 "$browser_pid" 2>/dev/null && kill -0 "$searx_pid" 2>/dev/null; do
  sleep 1
done

wait "$browser_pid" || true
wait "$searx_pid" || true
cleanup
