#!/bin/bash
set -e
# NOTE: upstream llama.cpp router merges CLI "base preset" INTO each alias preset
# (common_preset::merge in common/preset.cpp overwrites preset options with base
# options, and server-models.cpp merges base AFTER cascading presets: "server
# base preset from CLI args take highest precedence"). That means any --<opt>
# passed here clobbers per-alias values in router-models.ini. For knobs we want
# the alias INI (managed by llama-admin) to control, we must NOT pass them on
# the router CLI when --models-preset is active.
#
# Alias-controlled knobs (not set on router CLI when models-preset is in use):
#   All llama-server switches except router shell bootstrap (models-preset,
#   models-max, no-autoload, host, port). Per-alias values live in
#   router-models.ini and must not be passed here or they clobber INI.

FLEET_PROJECTION_DIR="${GA_LLAMA_FLEET_PROJECTION_DIR:-/models-local/llama/runtime/fleet}"
FLEET_PROJECTION_FILE="${FLEET_PROJECTION_DIR}/fleet-projection.json"

apply_fleet_projection_env() {
    local projection_file="$1"
    [ -f "$projection_file" ] || return 0

    while IFS=$'\t' read -r key value; do
        if [ -z "$key" ]; then
            continue
        fi
        case "$key" in
            GA_LLAMA_*)
                export "$key=$value"
                ;;
        esac
    done < <(python3 - "$projection_file" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as handle:
    doc = json.load(handle)

fleet = doc.get("fleetEnv") or {}
for key, value in fleet.items():
    if not isinstance(key, str) or not key.startswith("GA_LLAMA_"):
        continue
    print(f"{key}\t{value}")
PY
)
}

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

apply_fleet_projection_env "$FLEET_PROJECTION_FILE"
apply_cuda_visible_devices_override "GA_LLAMA_CUDA_VISIBLE_DEVICES"

# Empty GGML_VK_VISIBLE_DEVICES hides every Vulkan device in llama.cpp.
if [ -z "${GGML_VK_VISIBLE_DEVICES:-}" ]; then
    unset GGML_VK_VISIBLE_DEVICES
fi

ARGS=()
ROUTER_MODE=0
if [ -n "$GA_LLAMA_MODELS_PRESET" ]; then
    ARGS+=(--models-preset "$GA_LLAMA_MODELS_PRESET")
    ROUTER_MODE=1
fi
[ -n "$GA_LLAMA_MODELS_MAX" ] && ARGS+=(--models-max "$GA_LLAMA_MODELS_MAX")
[ "$GA_LLAMA_NO_AUTOLOAD" = "1" ] && ARGS+=(--no-models-autoload)
ARGS+=(--host "${GA_LLAMA_HOST:-127.0.0.1}")
ARGS+=(--port "${GA_LLAMA_PORT:-8080}")
if [ "$ROUTER_MODE" = "0" ]; then
    # Standalone mode (no preset INI). Process-level GA_LLAMA_* apply to the single model.
    [ -n "$GA_LLAMA_CTX_SIZE" ] && ARGS+=(--ctx-size "$GA_LLAMA_CTX_SIZE")
    [ -n "$GA_LLAMA_CACHE_RAM" ] && ARGS+=(--cache-ram "$GA_LLAMA_CACHE_RAM")
    [ -n "$GA_LLAMA_THREADS" ] && ARGS+=(--threads "$GA_LLAMA_THREADS")
    [ -n "$GA_LLAMA_PARALLEL" ] && ARGS+=(--parallel "$GA_LLAMA_PARALLEL")
    [ -n "$GA_LLAMA_GPU_LAYERS" ] && ARGS+=(--n-gpu-layers "$GA_LLAMA_GPU_LAYERS")
    [ "$GA_LLAMA_KV_OFFLOAD" = "0" ] && ARGS+=(--no-kv-offload)
    [ "$GA_LLAMA_KV_OFFLOAD" = "1" ] && ARGS+=(--kv-offload)
    [ "$GA_LLAMA_KV_UNIFIED" = "1" ] && ARGS+=(--kv-unified)
    [ -n "$GA_LLAMA_JINJA" ] && [ "$GA_LLAMA_JINJA" != "0" ] && ARGS+=(--jinja)
    [ "$GA_LLAMA_CONT_BATCH" = "1" ] && ARGS+=(--cont-batching)
    [ "$GA_LLAMA_NO_MMAP" = "1" ] && ARGS+=(--no-mmap)
    [ -n "$GA_LLAMA_FLASH_ATTN" ] && ARGS+=(--flash-attn "$GA_LLAMA_FLASH_ATTN")
    [ -n "$GA_LLAMA_CACHE_TYPE_K" ] && ARGS+=(--cache-type-k "$GA_LLAMA_CACHE_TYPE_K")
    [ -n "$GA_LLAMA_CACHE_TYPE_V" ] && ARGS+=(--cache-type-v "$GA_LLAMA_CACHE_TYPE_V")
    [ -n "$GA_LLAMA_TENSOR_SPLIT" ] && ARGS+=(--tensor-split "$GA_LLAMA_TENSOR_SPLIT")
fi
exec /app/llama-server "${ARGS[@]}"
