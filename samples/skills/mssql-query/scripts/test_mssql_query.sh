#!/bin/bash
# test_mssql_query.sh - end-to-end check of mssql_tool.py against a live SQL Server.
#
# Requires GA_DB_CONNECTION_STRING to be set in the environment (see README.md),
# e.g. pulled from the webapi container on the host:
#   export GA_DB_CONNECTION_STRING=$(docker exec guideants-webapi-ui printenv ConnectionStrings__DefaultConnection)
#
# Exits non-zero on the first failure.
set -euo pipefail
TOOL="$(dirname "$0")/mssql_tool.py"
pass() { echo "PASS: $1"; }

if [ -z "${GA_DB_CONNECTION_STRING:-}" ]; then
  echo "SKIP: GA_DB_CONNECTION_STRING not set - set it (see README) and re-run." >&2
  exit 2
fi

python3 "$TOOL" probe >/dev/null && pass "probe"
python3 "$TOOL" databases --json >/dev/null && pass "databases --json"
python3 "$TOOL" tables >/dev/null && pass "tables"

TABLE=$(python3 "$TOOL" tables --json | python3 -c "import json,sys; print(json.load(sys.stdin)['tables'][0]['table'])")
python3 "$TOOL" schema "$TABLE" >/dev/null && pass "schema $TABLE"
python3 "$TOOL" sample "$TABLE" -n 3 >/dev/null && pass "sample $TABLE"
python3 "$TOOL" sample "$TABLE" -n 3 --csv .tmp_test_sample.csv >/dev/null && pass "sample --csv"
rm -f .tmp_test_sample.csv
python3 "$TOOL" query "SELECT 1" --json >/dev/null && pass "query SELECT 1"

if python3 "$TOOL" query "DELETE FROM sys.tables" >/dev/null 2>&1; then
  echo "FAIL: read-only gate did not refuse a DELETE" >&2
  exit 1
fi
pass "read-only gate refuses DELETE"

if python3 "$TOOL" execute "SELECT 1" >/dev/null 2>&1; then
  echo "FAIL: execute ran without --allow-write" >&2
  exit 1
fi
pass "execute requires --allow-write"

echo "ALL TESTS PASSED"
