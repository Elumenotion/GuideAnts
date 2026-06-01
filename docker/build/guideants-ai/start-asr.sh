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

apply_cuda_visible_devices_override "GA_ASR_CUDA_VISIBLE_DEVICES"

export GA_ASR_HOST="${GA_ASR_HOST:-127.0.0.1}"
export GA_ASR_PORT="${GA_ASR_PORT:-8082}"
export GA_ASR_LOG_LEVEL="${GA_ASR_LOG_LEVEL:-info}"
export GA_ASR_MODEL_DIR="${GA_ASR_MODEL_DIR:-/models-local/asr}"
export GA_ASR_DEFAULT_MODEL_ID="${GA_ASR_DEFAULT_MODEL_ID:-Qwen/Qwen3-ASR-0.6B}"
export GA_ASR_TIMEOUT_SECONDS="${GA_ASR_TIMEOUT_SECONDS:-300}"
export GA_ASR_DEVICE_MAP="${GA_ASR_DEVICE_MAP:-auto}"
export GA_ASR_WARMUP_AUDIO_PATH="${GA_ASR_WARMUP_AUDIO_PATH:-/app/asr-service/warmup.webm}"

exec /opt/venv/bin/python /app/asr-service/asr_service.py
