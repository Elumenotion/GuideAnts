#!/bin/bash
set -e

env \
    ASPNETCORE_URLS="http://127.0.0.1:8081" \
    "Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=${GA_SANDBOX_LOG_LEVEL_HOSTING_DIAGNOSTICS:-Warning}" \
    "Logging__LogLevel__Microsoft.AspNetCore.Routing.EndpointMiddleware=${GA_SANDBOX_LOG_LEVEL_ENDPOINT_MIDDLEWARE:-Warning}" \
    dotnet /app/script-agent/ScriptExecutionAgent.dll &
AGENT_PID=$!

/app/start-media.sh &
MEDIA_PID=$!

nginx -g 'daemon off;' &
NGINX_PID=$!

shutdown_all() {
    kill "$AGENT_PID" "$MEDIA_PID" "$NGINX_PID" 2>/dev/null || true
}

trap "shutdown_all; exit" SIGTERM SIGINT

AGENT_REPORTED_EXIT=0
MEDIA_REPORTED_EXIT=0

while true; do
    if [ -n "${AGENT_PID:-}" ] && ! kill -0 "$AGENT_PID" 2>/dev/null; then
        if [ "$AGENT_REPORTED_EXIT" = "0" ]; then
            echo "ScriptExecutionAgent (PID $AGENT_PID) exited; continuing with remaining services" >&2
            AGENT_REPORTED_EXIT=1
        fi
        AGENT_PID=""
    fi

    if [ -n "${MEDIA_PID:-}" ] && ! kill -0 "$MEDIA_PID" 2>/dev/null; then
        if [ "$MEDIA_REPORTED_EXIT" = "0" ]; then
            echo "Media service (PID $MEDIA_PID) exited; continuing with remaining services" >&2
            MEDIA_REPORTED_EXIT=1
        fi
        MEDIA_PID=""
    fi

    if ! kill -0 "$NGINX_PID" 2>/dev/null; then
        echo "nginx (PID $NGINX_PID) exited; shutting down container" >&2
        shutdown_all
        exit 1
    fi

    sleep 2
done
