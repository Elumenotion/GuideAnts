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
export GA_TTS_DEFAULT_MODEL_ID="${GA_TTS_DEFAULT_MODEL_ID:-ResembleAI/chatterbox}"
export GA_TTS_DEFAULT_MODEL_PATH="${GA_TTS_DEFAULT_MODEL_PATH:-chatterbox}"
export GA_TTS_SERVER_PATH="${GA_TTS_SERVER_PATH:-/usr/local/bin/audiocpp_server}"
# Must match the ENGINE_ENABLE_* flags this image's audiocpp_server was built with
# (cuda flavor -> cuda, cpu flavor -> cpu, vulkan/rocm flavors -> vulkan). Set per
# flavor in docker-compose.*.yml; this default only covers the cuda flavor.
export GA_TTS_BACKEND="${GA_TTS_BACKEND:-cuda}"
export GA_TTS_ENGINE_HOST="${GA_TTS_ENGINE_HOST:-127.0.0.1}"
export GA_TTS_ENGINE_PORT="${GA_TTS_ENGINE_PORT:-18084}"
export GA_TTS_CATALOG_PATH="${GA_TTS_CATALOG_PATH:-/app/tts-service/catalog/manifest.json}"
export GA_TTS_VOICE_PACK_PATH="${GA_TTS_VOICE_PACK_PATH:-/opt/guideants/voice-pack}"
export GA_TTS_TIMEOUT_SECONDS="${GA_TTS_TIMEOUT_SECONDS:-300}"
export GA_TTS_SAMPLE_RATE="${GA_TTS_SAMPLE_RATE:-24000}"
export GA_TTS_VOICE="${GA_TTS_VOICE:-af_alloy}"
export GA_TTS_LANG_CODE="${GA_TTS_LANG_CODE:-a}"
export GA_TTS_SPEED="${GA_TTS_SPEED:-1.0}"
export GA_TTS_WARMUP_TEXT="${GA_TTS_WARMUP_TEXT:-Hello.}"
export GA_TTS_WARMUP_ON_LOAD="${GA_TTS_WARMUP_ON_LOAD:-true}"

exec /opt/venv/bin/python /app/tts-service/tts_service.py
