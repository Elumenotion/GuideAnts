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

apply_cuda_visible_devices_override "GA_TTS_CUDA_VISIBLE_DEVICES"

export GA_TTS_HOST="${GA_TTS_HOST:-127.0.0.1}"
export GA_TTS_PORT="${GA_TTS_PORT:-8084}"
export GA_TTS_LOG_LEVEL="${GA_TTS_LOG_LEVEL:-info}"
export GA_TTS_MODEL_DIR="${GA_TTS_MODEL_DIR:-/models-local/tts}"
export GA_TTS_DEFAULT_MODEL_ID="${GA_TTS_DEFAULT_MODEL_ID:-microsoft/VibeVoice-1.5B}"
export GA_TTS_DEFAULT_MODEL_PATH="${GA_TTS_DEFAULT_MODEL_PATH:-VibeVoice-1.5B}"
export GA_TTS_TOKENIZER_ID="${GA_TTS_TOKENIZER_ID:-Qwen/Qwen2.5-1.5B}"
export GA_TTS_TOKENIZER_PATH="${GA_TTS_TOKENIZER_PATH:-Qwen2.5-1.5B-tokenizer}"
export GA_TTS_TIMEOUT_SECONDS="${GA_TTS_TIMEOUT_SECONDS:-300}"
export GA_TTS_DEVICE_MAP="${GA_TTS_DEVICE_MAP:-auto}"
export GA_TTS_DTYPE="${GA_TTS_DTYPE:-bfloat16}"
export GA_TTS_MAX_NEW_TOKENS="${GA_TTS_MAX_NEW_TOKENS:-512}"
export GA_TTS_SAMPLE_RATE="${GA_TTS_SAMPLE_RATE:-24000}"
export GA_TTS_DEFAULT_VOICE_SECONDS="${GA_TTS_DEFAULT_VOICE_SECONDS:-1.0}"

exec /opt/venv/bin/python /app/tts-service/tts_service.py
