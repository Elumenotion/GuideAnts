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
#   --ctx-size  (alias key: ctx-size)
#   --cache-ram (alias key: cache-ram)
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

apply_cuda_visible_devices_override "GA_LLAMA_CUDA_VISIBLE_DEVICES"

ARGS=""
ROUTER_MODE=0
if [ -n "$GA_LLAMA_MODELS_PRESET" ]; then
    ARGS="$ARGS --models-preset $GA_LLAMA_MODELS_PRESET"
    ROUTER_MODE=1
fi
[ -n "$GA_LLAMA_MODELS_MAX" ]     && ARGS="$ARGS --models-max $GA_LLAMA_MODELS_MAX"
[ "$GA_LLAMA_NO_AUTOLOAD" = "1" ] && ARGS="$ARGS --no-models-autoload"
ARGS="$ARGS --host ${GA_LLAMA_HOST:-127.0.0.1}"
ARGS="$ARGS --port ${GA_LLAMA_PORT:-8080}"
if [ "$ROUTER_MODE" = "0" ]; then
    # Standalone mode (no preset INI). These knobs apply to the single loaded model.
    [ -n "$GA_LLAMA_CTX_SIZE" ]   && ARGS="$ARGS --ctx-size $GA_LLAMA_CTX_SIZE"
    [ -n "$GA_LLAMA_CACHE_RAM" ]  && ARGS="$ARGS --cache-ram $GA_LLAMA_CACHE_RAM"
fi
[ -n "$GA_LLAMA_THREADS" ]        && ARGS="$ARGS --threads $GA_LLAMA_THREADS"
[ -n "$GA_LLAMA_PARALLEL" ]       && ARGS="$ARGS --parallel $GA_LLAMA_PARALLEL"
[ -n "$GA_LLAMA_GPU_LAYERS" ]     && ARGS="$ARGS --n-gpu-layers $GA_LLAMA_GPU_LAYERS"
# Vulkan can fail scheduler reservation when KV-cache tensors are placed on
# the GPU for some model families. Keep this as an explicit env-controlled
# base preset because router mode propagates it to child instances.
[ "$GA_LLAMA_KV_OFFLOAD" = "0" ]  && ARGS="$ARGS --no-kv-offload"
[ "$GA_LLAMA_KV_OFFLOAD" = "1" ]  && ARGS="$ARGS --kv-offload"
[ "$GA_LLAMA_KV_UNIFIED" = "1" ]  && ARGS="$ARGS --kv-unified"
[ "$GA_LLAMA_JINJA" = "1" ]       && ARGS="$ARGS --jinja"
[ "$GA_LLAMA_CONT_BATCH" = "1" ]  && ARGS="$ARGS --cont-batching"
[ "$GA_LLAMA_NO_MMAP" = "1" ]     && ARGS="$ARGS --no-mmap"
# Global runtime knobs that intentionally DO propagate from the router base
# preset into every spawned child instance (unlike ctx-size/cache-ram, which
# are left per-alias). --flash-attn takes a literal value (on|off|auto);
# cache-type-v quantization requires flash attention to be enabled. The
# image-min-tokens is per-alias in router-models.ini (Qwen-VL only). Do not set
# GA_LLAMA_IMAGE_MIN_TOKENS globally — it propagates to every child and breaks
# models whose mmproj image_max_pixels is below the 1024-token floor.
[ -n "$GA_LLAMA_FLASH_ATTN" ]     && ARGS="$ARGS --flash-attn $GA_LLAMA_FLASH_ATTN"
[ -n "$GA_LLAMA_CACHE_TYPE_K" ]   && ARGS="$ARGS --cache-type-k $GA_LLAMA_CACHE_TYPE_K"
[ -n "$GA_LLAMA_CACHE_TYPE_V" ]   && ARGS="$ARGS --cache-type-v $GA_LLAMA_CACHE_TYPE_V"
# --tensor-split sets the per-GPU layer proportion (comma list, e.g. "7,1").
# Indices follow this process's visible-device order: with
# GA_LLAMA_CUDA_VISIBLE_DEVICES=1,0 the FIRST proportion targets physical GPU 1
# (5090) and the second targets physical GPU 0 (4090), so a larger first value
# biases layers onto the 5090. Empty => llama.cpp's default free-VRAM heuristic
# (which mis-splits here because the 4090 is shared with asr/tts/emb/sd).
[ -n "$GA_LLAMA_TENSOR_SPLIT" ]   && ARGS="$ARGS --tensor-split $GA_LLAMA_TENSOR_SPLIT"
exec /app/llama-server $ARGS
