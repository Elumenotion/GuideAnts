#!/bin/bash
set -e

apply_cuda_visible_devices_override() {
    local override_name="$1"
    local override_value="${!override_name:-}"
    local inherited="${CUDA_VISIBLE_DEVICES:-}"

    [ -z "$override_value" ] && return

    if ! [[ "$override_value" =~ ^[0-9]+(,[0-9]+)*$ ]]; then
        echo "ERROR: ${override_name} must be a comma-separated list of physical GPU indices (example: 1,0)." >&2
        exit 1
    fi

    if [ -n "$inherited" ]; then
        local requested
        local allowed
        IFS=',' read -r -a requested <<< "$override_value"
        IFS=',' read -r -a allowed <<< "$inherited"

        local index
        local candidate
        local is_allowed
        for index in "${requested[@]}"; do
            is_allowed=0
            for candidate in "${allowed[@]}"; do
                if [ "$index" = "$candidate" ]; then
                    is_allowed=1
                    break
                fi
            done
            if [ "$is_allowed" -ne 1 ]; then
                echo "ERROR: ${override_name}='${override_value}' is not compatible with inherited CUDA_VISIBLE_DEVICES='${inherited}'." >&2
                exit 1
            fi
        done
    fi

    export CUDA_VISIBLE_DEVICES="$override_value"
}

export GA_ADMIN_HOST="${GA_ADMIN_HOST:-${GA_LLAMA_ADMIN_HOST:-127.0.0.1}}"
export GA_ADMIN_PORT="${GA_ADMIN_PORT:-${GA_LLAMA_ADMIN_PORT:-8086}}"
export GA_ADMIN_LOG_LEVEL="${GA_ADMIN_LOG_LEVEL:-${GA_LLAMA_ADMIN_LOG_LEVEL:-info}}"

export GA_LLAMA_ADMIN_HOST="${GA_LLAMA_ADMIN_HOST:-$GA_ADMIN_HOST}"
export GA_LLAMA_ADMIN_PORT="${GA_LLAMA_ADMIN_PORT:-$GA_ADMIN_PORT}"
export GA_LLAMA_ADMIN_LOG_LEVEL="${GA_LLAMA_ADMIN_LOG_LEVEL:-$GA_ADMIN_LOG_LEVEL}"
export GA_LLAMA_MODEL_DIR="${GA_LLAMA_MODEL_DIR:-/models-local/llama}"
export GA_LLAMA_ROUTER_CONFIG_PATH="${GA_LLAMA_ROUTER_CONFIG_PATH:-${GA_LLAMA_MODELS_PRESET:-/models-local/router-models.ini}}"

# Warmup orchestrator reaches the standalone SD control plane on this port.
export GA_SD_HOST="${GA_SD_HOST:-127.0.0.1}"
export GA_SD_PORT="${GA_SD_PORT:-8083}"

# -B: do not write/use .pyc (bind-mounted admin modules must not lose to image bytecode).
exec /opt/venv/bin/python -B /app/admin-service/ga_admin_service.py
