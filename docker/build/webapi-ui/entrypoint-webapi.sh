#!/usr/bin/env bash
set -euo pipefail

: "${ASPNETCORE_URLS:=http://127.0.0.1:8081}"
export ASPNETCORE_URLS

nginx -g 'daemon off;' &
nginx_pid=$!

dotnet /app/GuideAntsApi.dll &
app_pid=$!

shutdown() {
  if [[ -n "${app_pid:-}" ]] && kill -0 "${app_pid}" 2>/dev/null; then
    kill -TERM "${app_pid}" 2>/dev/null || true
  fi

  if [[ -n "${nginx_pid:-}" ]] && kill -0 "${nginx_pid}" 2>/dev/null; then
    kill -TERM "${nginx_pid}" 2>/dev/null || true
  fi
}

trap shutdown TERM INT

set +e
wait -n "${app_pid}" "${nginx_pid}"
exit_code=$?
set -e

shutdown
wait "${app_pid}" 2>/dev/null || true
wait "${nginx_pid}" 2>/dev/null || true

exit "${exit_code}"
