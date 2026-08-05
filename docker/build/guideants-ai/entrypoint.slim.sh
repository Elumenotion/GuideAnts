#!/bin/bash
set -e

SCRIPT_EXECUTION_REQUIRE_TOKEN="${SCRIPT_EXECUTION_REQUIRE_TOKEN:-true}"
SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION="${SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION:-true}"

if [ -z "${FILE_STORAGE_ROOT:-}" ]; then
    echo "ERROR: FILE_STORAGE_ROOT must be configured for ScriptExecutionAgent." >&2
    exit 1
fi

if [ "$SCRIPT_EXECUTION_REQUIRE_TOKEN" = "true" ] && [ -z "${SCRIPT_EXECUTION_AGENT_TOKEN:-}" ]; then
    echo "ERROR: SCRIPT_EXECUTION_AGENT_TOKEN must be configured when SCRIPT_EXECUTION_REQUIRE_TOKEN=true." >&2
    exit 1
fi

if [ "$SCRIPT_EXECUTION_REQUIRE_TOKEN" != "true" ] && [ "$SCRIPT_EXECUTION_REQUIRE_TOKEN" != "false" ]; then
    echo "ERROR: SCRIPT_EXECUTION_REQUIRE_TOKEN must be 'true' or 'false'." >&2
    exit 1
fi

SCRIPT_EXECUTION_ADMIN_API_ENABLED="${SCRIPT_EXECUTION_ADMIN_API_ENABLED:-false}"
SCRIPT_EXECUTION_ADMIN_STATE_DIR="${SCRIPT_EXECUTION_ADMIN_STATE_DIR:-/var/lib/guideants/script-agent-admin}"
SCRIPT_EXECUTION_SCOPE_STATE_ROOT="${SCRIPT_EXECUTION_SCOPE_STATE_ROOT:-${SCRIPT_EXECUTION_ADMIN_STATE_DIR}/scopes}"

if [ "$SCRIPT_EXECUTION_ADMIN_API_ENABLED" = "true" ] && [ -z "${SCRIPT_EXECUTION_ADMIN_TOKEN:-}" ]; then
    echo "ERROR: SCRIPT_EXECUTION_ADMIN_TOKEN must be configured when SCRIPT_EXECUTION_ADMIN_API_ENABLED=true." >&2
    exit 1
fi

if [ "$SCRIPT_EXECUTION_ADMIN_API_ENABLED" = "true" ] && [ -x /opt/guideants/script-agent-admin/reconcile.sh ]; then
    /opt/guideants/script-agent-admin/reconcile.sh
fi

export SCRIPT_EXECUTION_ADMIN_STATE_DIR
export SCRIPT_EXECUTION_SCOPE_STATE_ROOT

echo "ScriptExecutionAgent hardening config: token_required=${SCRIPT_EXECUTION_REQUIRE_TOKEN} token_configured=$([ -n \"${SCRIPT_EXECUTION_AGENT_TOKEN:-}\" ] && echo true || echo false) identity_isolation=${SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION} storage_root_configured=true admin_api_enabled=${SCRIPT_EXECUTION_ADMIN_API_ENABLED} admin_token_configured=$([ -n \"${SCRIPT_EXECUTION_ADMIN_TOKEN:-}\" ] && echo true || echo false)"

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
