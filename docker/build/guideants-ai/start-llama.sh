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
[ "$GA_LLAMA_KV_UNIFIED" = "1" ]  && ARGS="$ARGS --kv-unified"
[ "$GA_LLAMA_JINJA" = "1" ]       && ARGS="$ARGS --jinja"
[ "$GA_LLAMA_CONT_BATCH" = "1" ]  && ARGS="$ARGS --cont-batching"
[ "$GA_LLAMA_NO_MMAP" = "1" ]     && ARGS="$ARGS --no-mmap"
exec /app/llama-server $ARGS
