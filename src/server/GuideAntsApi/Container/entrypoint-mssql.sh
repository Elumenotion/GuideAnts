#!/usr/bin/env bash
set -euo pipefail

: "${ACCEPT_EULA:=Y}"
: "${MSSQL_PID:=Express}"
: "${MSSQL_TCP_PORT:=1433}"
: "${MSSQL_DB_NAME:=guideants-dev}"
: "${SQL_READY_RETRIES:=60}"
: "${SQL_READY_SLEEP_SECONDS:=2}"

export ACCEPT_EULA
export MSSQL_PID
export MSSQL_TCP_PORT

if [[ -z "${MSSQL_SA_PASSWORD:-}" ]]; then
  echo "MSSQL_SA_PASSWORD is required for the mssql container image." >&2
  exit 1
fi

mkdir -p \
  /var/opt/mssql \
  /var/opt/mssql/data \
  /var/opt/mssql/log \
  /var/opt/mssql/secrets \
  /var/opt/mssql/FTData \
  /app/ContentFiles

chown -R mssql:mssql /var/opt/mssql

runuser -u mssql -- /opt/mssql/bin/sqlservr &
sql_pid=$!

sql_ready=0
for ((attempt = 1; attempt <= SQL_READY_RETRIES; attempt++)); do
  if /opt/mssql-tools18/bin/sqlcmd \
    -S "localhost,${MSSQL_TCP_PORT}" \
    -U sa \
    -P "${MSSQL_SA_PASSWORD}" \
    -Q "SELECT 1" \
    -C \
    -No >/dev/null 2>&1; then
    sql_ready=1
    break
  fi

  if ! kill -0 "${sql_pid}" 2>/dev/null; then
    echo "sqlservr exited before it became ready." >&2
    wait "${sql_pid}" || true
    exit 1
  fi

  sleep "${SQL_READY_SLEEP_SECONDS}"
done

if [[ "${sql_ready}" -ne 1 ]]; then
  echo "SQL Server did not become ready after ${SQL_READY_RETRIES} attempts." >&2
  kill -TERM "${sql_pid}" 2>/dev/null || true
  wait "${sql_pid}" || true
  exit 1
fi

if [[ -z "${ConnectionStrings__DefaultConnection:-}" ]]; then
  export ConnectionStrings__DefaultConnection="Server=localhost,${MSSQL_TCP_PORT};Initial Catalog=${MSSQL_DB_NAME};Persist Security Info=False;User ID=sa;Password=${MSSQL_SA_PASSWORD};MultipleActiveResultSets=True;Encrypt=False;TrustServerCertificate=True;Connection Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=5;"
fi

dotnet /app/GuideAntsApi.dll &
app_pid=$!

shutdown() {
  if [[ -n "${app_pid:-}" ]] && kill -0 "${app_pid}" 2>/dev/null; then
    kill -TERM "${app_pid}" 2>/dev/null || true
  fi

  if [[ -n "${sql_pid:-}" ]] && kill -0 "${sql_pid}" 2>/dev/null; then
    kill -TERM "${sql_pid}" 2>/dev/null || true
  fi
}

trap shutdown TERM INT

set +e
wait -n "${app_pid}" "${sql_pid}"
exit_code=$?
set -e

shutdown
wait "${app_pid}" 2>/dev/null || true
wait "${sql_pid}" 2>/dev/null || true

exit "${exit_code}"
