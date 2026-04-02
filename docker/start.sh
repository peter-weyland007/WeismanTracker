#!/usr/bin/env bash
set -euo pipefail

API_PORT="${API_PORT:-5199}"
WEB_PORT="${WEB_PORT:-8080}"

export WeismanApi__BaseUrl="${WeismanApi__BaseUrl:-http://127.0.0.1:${API_PORT}}"

raw_conn="${ConnectionStrings__Default:-}"
raw_conn="${raw_conn%\"}"
raw_conn="${raw_conn#\"}"
raw_conn="${raw_conn%\'}"
raw_conn="${raw_conn#\'}"

if [[ -z "${raw_conn// }" ]]; then
  export ConnectionStrings__Default="Data Source=/app/data/weismantracker.db"
elif [[ "$raw_conn" == *=* ]]; then
  export ConnectionStrings__Default="$raw_conn"
else
  # If a plain file path was provided, normalize it to a valid Sqlite connection string.
  export ConnectionStrings__Default="Data Source=$raw_conn"
fi

mkdir -p /app/data

echo "Starting API on :${API_PORT}"
dotnet /app/api/api.dll --urls "http://0.0.0.0:${API_PORT}" &
API_PID=$!

echo "Starting Web on :${WEB_PORT} (API=${WeismanApi__BaseUrl})"
dotnet /app/web/web.dll --urls "http://0.0.0.0:${WEB_PORT}" &
WEB_PID=$!

cleanup() {
  kill "$API_PID" "$WEB_PID" 2>/dev/null || true
}
trap cleanup TERM INT

wait -n "$API_PID" "$WEB_PID"
STATUS=$?
cleanup
exit $STATUS
