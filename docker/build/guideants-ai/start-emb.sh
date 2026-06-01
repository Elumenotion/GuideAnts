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

apply_cuda_visible_devices_override "GA_EMB_CUDA_VISIBLE_DEVICES"

export GA_EMB_HOST="${GA_EMB_HOST:-127.0.0.1}"
export GA_EMB_PORT="${GA_EMB_PORT:-8085}"
export GA_EMB_LOG_LEVEL="${GA_EMB_LOG_LEVEL:-info}"
export GA_EMB_MODEL_DIR="${GA_EMB_MODEL_DIR:-/models-local/emb}"
export GA_EMB_DEFAULT_MODEL_PATH="${GA_EMB_DEFAULT_MODEL_PATH:-}"
export GA_EMB_DEVICE="${GA_EMB_DEVICE:-cuda}"
export GA_EMB_TARGET_DEVICES="${GA_EMB_TARGET_DEVICES:-cuda:0,cuda:1}"
export GA_EMB_FIX_MISTRAL_REGEX="${GA_EMB_FIX_MISTRAL_REGEX:-1}"
export GA_EMB_AUTO_LOAD_ON_STARTUP="${GA_EMB_AUTO_LOAD_ON_STARTUP:-}"
export GA_EMB_WARMUP_ON_LOAD="${GA_EMB_WARMUP_ON_LOAD:-}"

exec /opt/venv/bin/python /app/emb-service/emb_service.py
