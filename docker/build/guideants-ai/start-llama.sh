#!/bin/bash
set -e
# GuideAnts always runs llama-server as a router backed by router-models.ini.
# The API writes canonical INI; start-llama materializes runtime INI (env defaults
# + alias overrides) and starts the parent with bootstrap argv only:
#   --models-preset, --models-max, --no-models-autoload, --host, --port
# Child model CLIs come from the effective alias preset in the runtime INI.
# Do not pass env-default or alias knobs on the parent argv — llama.cpp merges
# parent CLI into child presets and would clobber per-alias values.

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
ROUTER_CANONICAL="${GA_LLAMA_MODELS_PRESET:-/models-local/router-models.ini}"
ROUTER_RUNTIME="${GA_LLAMA_MODELS_RUNTIME_PRESET:-}"
if [ -z "$ROUTER_RUNTIME" ]; then
    case "$ROUTER_CANONICAL" in
        *.ini) ROUTER_RUNTIME="${ROUTER_CANONICAL%.ini}.runtime.ini" ;;
        *) ROUTER_RUNTIME="${ROUTER_CANONICAL}.runtime.ini" ;;
    esac
fi

if [ ! -f "$ROUTER_CANONICAL" ]; then
    echo "ERROR: router preset not found at '$ROUTER_CANONICAL' (GA_LLAMA_MODELS_PRESET)." >&2
    exit 1
fi

PYTHONPATH="/app/lib:/app/llama-admin-service${PYTHONPATH:+:$PYTHONPATH}"
export PYTHONPATH

materialize_runtime_ini() {
    python3 - "$ROUTER_CANONICAL" "$ROUTER_RUNTIME" <<'PY'
import sys

from guideants_hf.router_mmproj import materialize_router_ini_text
import llama_router_ini as router_ini

canonical_path, runtime_path = sys.argv[1], sys.argv[2]
with open(canonical_path, "r", encoding="utf-8") as handle:
    canonical = handle.read()
runtime = materialize_router_ini_text(
    canonical,
    parse_router_ini=router_ini.parse_router_ini,
    serialize_router_ini_for_runtime=router_ini.serialize_router_ini_for_runtime,
)
with open(runtime_path, "w", encoding="utf-8", newline="\n") as handle:
    handle.write(runtime)
PY
}

ARGS+=(--models-preset "$ROUTER_RUNTIME")
[ -n "$GA_LLAMA_MODELS_MAX" ] && ARGS+=(--models-max "$GA_LLAMA_MODELS_MAX")
[ "$GA_LLAMA_NO_AUTOLOAD" = "1" ] && ARGS+=(--no-models-autoload)
ARGS+=(--host "${GA_LLAMA_HOST:-127.0.0.1}")
ARGS+=(--port "${GA_LLAMA_PORT:-8080}")

LLAMA_SERVER="${GA_LLAMA_SERVER_BIN:-/app/llama-server}"
LLAMA_SERVER_LOG="${GA_LLAMA_SERVER_LOG:-/run/llama-server.log}"

# Entrypoint respawns this script when llama-server exits. If the previous
# process died on an unrecognized preset key, strip it from canonical INI
# before rematerializing so we do not restore the poison key and crash-loop.
if [ -f "$LLAMA_SERVER_LOG" ]; then
    python3 - "$ROUTER_CANONICAL" "$LLAMA_SERVER_LOG" <<'PY' || true
import sys

from guideants_hf.unrecognized_preset import sanitize_canonical_from_log
import llama_router_ini as router_ini

ok = sanitize_canonical_from_log(
    sys.argv[1],
    sys.argv[2],
    parse_router_ini=router_ini.parse_router_ini,
    serialize_router_ini=router_ini.serialize_router_ini,
)
if ok:
    print(
        f"stripped unrecognized llama.cpp preset option from {sys.argv[1]}",
        file=sys.stderr,
    )
sys.exit(0)
PY
fi

materialize_runtime_ini

if [ ! -f "$ROUTER_RUNTIME" ]; then
    echo "ERROR: router runtime preset not written at '$ROUTER_RUNTIME'." >&2
    exit 1
fi

exec "$LLAMA_SERVER" "${ARGS[@]}"
