#!/bin/bash
set -e

# Router preset: persisted on the ai_local_models volume (atomic updates from ga-admin).
# First boot of an empty volume: copy baked defaults so llama-server has aliases before UI use.
ROUTER_PRESET="${GA_LLAMA_MODELS_PRESET:-/models-local/router-models.ini}"
if [ -n "$ROUTER_PRESET" ] && [ ! -f "$ROUTER_PRESET" ] && [ -f /opt/seed/router-models.ini ]; then
    echo "Seeding ${ROUTER_PRESET} from image default (first volume boot)."
    mkdir -p "$(dirname "$ROUTER_PRESET")"
    cp -f /opt/seed/router-models.ini "$ROUTER_PRESET"
fi

sanitize_router_preset() {
    local preset_path="$1"
    if [ -z "$preset_path" ] || [ ! -f "$preset_path" ]; then
        return
    fi

    local tmp_path="${preset_path}.sanitized.$$"
    if ! python3 - "$preset_path" "$tmp_path" <<'PY'
import os
import sys

source_path = sys.argv[1]
output_path = sys.argv[2]

with open(source_path, "r", encoding="utf-8") as src:
    lines = src.read().splitlines(keepends=True)

header_lines = []
sections = []
current = None

for raw_line in lines:
    stripped = raw_line.strip()
    if stripped.startswith("[") and stripped.endswith("]"):
        if current is not None:
            sections.append(current)
        current = {
            "alias": stripped[1:-1].strip(),
            "raw": [raw_line],
            "model": "",
            "mmproj": "",
        }
        continue

    if current is None:
        header_lines.append(raw_line)
        continue

    current["raw"].append(raw_line)
    if "=" not in raw_line:
        continue

    key, value = raw_line.split("=", 1)
    key = key.strip().lower()
    value = value.strip()
    if key == "model":
        current["model"] = value
    elif key == "mmproj":
        current["mmproj"] = value

if current is not None:
    sections.append(current)

version_value = "1"
for raw_line in header_lines:
    stripped = raw_line.strip()
    if not stripped or stripped.startswith("#") or stripped.startswith(";"):
        continue
    if "=" not in stripped:
        continue
    key, value = stripped.split("=", 1)
    if key.strip().lower() == "version":
        candidate = value.strip()
        if candidate:
            version_value = candidate
        break

valid_sections = []
removed_sections = []

for section in sections:
    alias = section["alias"]
    model_path = section["model"].strip()
    mmproj_path = section["mmproj"].strip()
    reasons = []

    if not model_path:
        reasons.append("model path is missing")
    elif not os.path.isfile(model_path):
        reasons.append(f"model file not found: {model_path}")

    if mmproj_path and not os.path.isfile(mmproj_path):
        reasons.append(f"mmproj file not found: {mmproj_path}")

    if reasons:
        removed_sections.append((alias, reasons))
    else:
        valid_sections.append(section)

with open(output_path, "w", encoding="utf-8", newline="\n") as dst:
    dst.write(f"version = {version_value}\n")
    if valid_sections:
        dst.write("\n")
        for index, section in enumerate(valid_sections):
            for raw_line in section["raw"]:
                if raw_line.endswith("\n"):
                    dst.write(raw_line)
                else:
                    dst.write(raw_line + "\n")
            if index != len(valid_sections) - 1:
                last_line = section["raw"][-1] if section["raw"] else ""
                if last_line.strip():
                    dst.write("\n")

if removed_sections:
    print(
        f"WARNING: router preset '{source_path}' had {len(removed_sections)} invalid alias entries at startup; removing them before llama-server boot.",
        file=sys.stderr,
    )
    for alias, reasons in removed_sections:
        reason_text = "; ".join(reasons)
        print(
            f"WARNING: removing router alias '{alias}' from startup preset because {reason_text}.",
            file=sys.stderr,
        )
PY
    then
        echo "WARNING: router preset startup validation failed for '${preset_path}'; continuing with existing file unchanged." >&2
        rm -f "$tmp_path" 2>/dev/null || true
        return
    fi

    if cmp -s "$preset_path" "$tmp_path"; then
        rm -f "$tmp_path" 2>/dev/null || true
        return
    fi

    mv -f "$tmp_path" "$preset_path"
}

sanitize_router_preset "$ROUTER_PRESET"

# Qwen-VL needs image-min-tokens=1024 for grounding accuracy, but that value breaks
# other vision models (e.g. Gemma mmproj max pixels). Apply it per-alias only.
normalize_router_image_min_tokens() {
    local preset_path="$1"
    if [ -z "$preset_path" ] || [ ! -f "$preset_path" ]; then
        return
    fi

    local tmp_path="${preset_path}.image-tokens.$$"
    if ! python3 - "$preset_path" "$tmp_path" <<'PY'
import re
import sys

source_path = sys.argv[1]
output_path = sys.argv[2]

with open(source_path, "r", encoding="utf-8") as src:
    lines = src.read().splitlines()

output: list[str] = []
current_alias: str | None = None
section_lines: list[str] = []

def flush_section() -> None:
    global section_lines, current_alias
    if current_alias is None:
        return

    cleaned: list[str] = []
    for raw_line in section_lines:
        stripped = raw_line.strip()
        if stripped.startswith("#") or stripped.startswith(";"):
            cleaned.append(raw_line)
            continue
        if "=" in stripped:
            key = stripped.split("=", 1)[0].strip().lower()
            if key == "image-min-tokens":
                continue
        cleaned.append(raw_line)

  # Qwen-VL aliases only; global router --image-min-tokens breaks Gemma loads.
    if re.search(r"(?i)qwen", current_alias):
        insert_at = 1 if cleaned and cleaned[0].strip().startswith("[") else 0
        cleaned.insert(insert_at, "image-min-tokens = 1024")

    output.extend(cleaned)
    section_lines = []
    current_alias = None

for raw_line in lines:
    stripped = raw_line.strip()
    if stripped.startswith("[") and stripped.endswith("]"):
        flush_section()
        current_alias = stripped[1:-1].strip()
        section_lines = [raw_line]
        continue

    if current_alias is None:
        output.append(raw_line)
        continue

    section_lines.append(raw_line)

flush_section()

with open(output_path, "w", encoding="utf-8", newline="\n") as dst:
    dst.write("\n".join(output))
    if output:
        dst.write("\n")
PY
    then
        echo "WARNING: router preset image-min-tokens normalization failed for '${preset_path}'; continuing unchanged." >&2
        rm -f "$tmp_path" 2>/dev/null || true
        return
    fi

    if cmp -s "$preset_path" "$tmp_path"; then
        rm -f "$tmp_path" 2>/dev/null || true
        return
    fi

    mv -f "$tmp_path" "$preset_path"
}

normalize_router_image_min_tokens "$ROUTER_PRESET"

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

# llama-server stdout/stderr is tee'd here so the admin service can classify crash reasons
# (CUDA OOM vs. generic SIGABRT vs. reload SIGTERM). Capped at ~128KB via the tail-truncate
# below before each respawn so the volume never fills with driver stack traces.
LLAMA_SERVER_LOG="/run/llama-server.log"
LLAMA_SERVER_LAST_EXIT_FILE="/run/llama-server.last-exit.json"
LLAMA_SERVER_LOG_MAX_BYTES=${GA_LLAMA_LOG_MAX_BYTES:-131072}
mkdir -p "$(dirname "$LLAMA_SERVER_LOG")" 2>/dev/null || true
: > "$LLAMA_SERVER_LOG"

# Truncate the log to the tail of LLAMA_SERVER_LOG_MAX_BYTES so the ring never grows without
# bound across crash/restart cycles. Called before each (re)spawn of llama-server.
truncate_llama_log_tail() {
    if [ ! -s "$LLAMA_SERVER_LOG" ]; then
        return
    fi
    local size
    size=$(wc -c <"$LLAMA_SERVER_LOG" 2>/dev/null || echo 0)
    if [ "$size" -le "$LLAMA_SERVER_LOG_MAX_BYTES" ]; then
        return
    fi
    local tmp
    tmp="${LLAMA_SERVER_LOG}.tmp"
    tail -c "$LLAMA_SERVER_LOG_MAX_BYTES" "$LLAMA_SERVER_LOG" >"$tmp" 2>/dev/null && mv "$tmp" "$LLAMA_SERVER_LOG"
}

# Classify llama-server exit reason by grepping the tail of its stderr for the known
# CUDA/OOM/abort markers. Mirrors LlamaCppChatClient.OutOfMemoryMarkers so the UI surfaces the
# same reason whether it learns of the crash via an HTTP 500 body or via this file.
classify_llama_exit_reason() {
    local exit_code="$1"
    local signal="$2"
    local log_tail
    log_tail=$(tail -c 16384 "$LLAMA_SERVER_LOG" 2>/dev/null || true)

    if [ "$signal" = "15" ]; then
        echo "signaled_reload"
        return
    fi
    if [ "$exit_code" = "0" ] && [ -z "$signal" ]; then
        echo "normal"
        return
    fi
    if echo "$log_tail" | grep -qiE 'CUDA error: out of memory|cudaErrorMemoryAllocation|cudaMalloc failed|ggml_cuda_host_malloc: failed|failed to allocate (CUDA|buffer|compute buffers)|std::bad_alloc|out of memory'; then
        echo "oom"
        return
    fi
    echo "crashed"
}

write_llama_last_exit_record() {
    local pid="$1"
    local exit_code="$2"
    local signal="$3"
    local reason
    reason=$(classify_llama_exit_reason "$exit_code" "$signal")
    local tail_json
    tail_json=$(tail -c 4096 "$LLAMA_SERVER_LOG" 2>/dev/null | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))' 2>/dev/null || echo '""')
    local exited_at
    exited_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    local tmp="${LLAMA_SERVER_LAST_EXIT_FILE}.tmp"
    cat >"$tmp" <<EOF
{"pid": ${pid:-null}, "exitedAt": "${exited_at}", "exitCode": ${exit_code:-null}, "signal": ${signal:-null}, "reason": "${reason}", "tail": ${tail_json}}
EOF
    mv "$tmp" "$LLAMA_SERVER_LAST_EXIT_FILE"
    echo "llama-server exit recorded: pid=${pid} exitCode=${exit_code} signal=${signal} reason=${reason}" >&2
}

monitor_service_readiness() {
    local service_name="$1"
    local pid="$2"
    local port="$3"
    local path="$4"
    local timeout_seconds="$5"

    local started_at
    started_at=$(date +%s)
    echo "Monitoring ${service_name} readiness on 127.0.0.1:${port}${path} (timeout ${timeout_seconds}s)..."

    while true; do
        if curl -fsS "http://127.0.0.1:${port}${path}" >/dev/null 2>&1; then
            echo "${service_name} is ready."
            return 0
        fi

        if ! kill -0 "$pid" 2>/dev/null; then
            echo "${service_name} (PID ${pid}) exited before becoming ready" >&2
            return 1
        fi

        local now elapsed
        now=$(date +%s)
        elapsed=$((now - started_at))
        if [ "$elapsed" -ge "$timeout_seconds" ]; then
            echo "Timed out waiting for ${service_name} readiness after ${timeout_seconds}s" >&2
            return 1
        fi

        sleep 2
    done
}

# Start all services without serial wait dependencies.
/app/start-ga-admin.sh &
GA_ADMIN_PID=$!

truncate_llama_log_tail
# Tee stdout+stderr to the host-visible ring log (capped above) AND keep it flowing to the
# container stdout so existing `docker logs guideants-ai` still works. We deliberately use
# **process substitution** rather than a `| tee` pipeline: `a | b &` makes `$!` the PID of
# `b` (tee), which is useless for the SIGTERM/respawn machinery below. With `> >(tee ...) &`
# the process sub is spawned first and `$!` is then set from the final `&` to the PID of
# start-llama.sh — which `exec`s into llama-server, so that PID *is* llama-server's PID.
/app/start-llama.sh > >(tee -a "$LLAMA_SERVER_LOG") 2>&1 &
LLAMA_PID=$!
LLAMA_PID_FILE="/run/llama-server.pid"
mkdir -p "$(dirname "$LLAMA_PID_FILE")" 2>/dev/null || true
echo "$LLAMA_PID" > "$LLAMA_PID_FILE"

env \
    ASPNETCORE_URLS="http://127.0.0.1:8081" \
    "Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=${GA_SANDBOX_LOG_LEVEL_HOSTING_DIAGNOSTICS:-Warning}" \
    "Logging__LogLevel__Microsoft.AspNetCore.Routing.EndpointMiddleware=${GA_SANDBOX_LOG_LEVEL_ENDPOINT_MIDDLEWARE:-Warning}" \
    dotnet /app/script-agent/ScriptExecutionAgent.dll &
AGENT_PID=$!

# Start ASR service in background
/app/start-asr.sh &
ASR_PID=$!

/app/start-tts.sh &
TTS_PID=$!

/app/start-emb.sh &
EMB_PID=$!

/app/start-media.sh &
MEDIA_PID=$!

nginx -g 'daemon off;' &
NGINX_PID=$!

# Optional readiness monitors run in background and do not block other services.
if [ "${GA_SD_WAIT_FOR_READY_ON_STARTUP:-0}" = "1" ]; then
    monitor_service_readiness \
        "SD service" \
        "$GA_ADMIN_PID" \
        "${GA_ADMIN_PORT:-${GA_LLAMA_ADMIN_PORT:-8086}}" \
        "/sd/health" \
        "${GA_SD_READY_TIMEOUT_SECONDS:-1800}" &
fi

if [ "${GA_ASR_WAIT_FOR_READY_ON_STARTUP:-0}" = "1" ]; then
    monitor_service_readiness \
        "ASR service" \
        "$ASR_PID" \
        "${GA_ASR_PORT:-8082}" \
        "/ready" \
        "${GA_ASR_READY_TIMEOUT_SECONDS:-1800}" &
fi

if [ "${GA_TTS_WAIT_FOR_READY_ON_STARTUP:-0}" = "1" ]; then
    monitor_service_readiness \
        "TTS service" \
        "$TTS_PID" \
        "${GA_TTS_PORT:-8084}" \
        "/ready" \
        "${GA_TTS_READY_TIMEOUT_SECONDS:-1800}" &
fi

if [ "${GA_EMB_WAIT_FOR_READY_ON_STARTUP:-0}" = "1" ]; then
    monitor_service_readiness \
        "Embeddings service" \
        "$EMB_PID" \
        "${GA_EMB_PORT:-8085}" \
        "/ready" \
        "${GA_EMB_READY_TIMEOUT_SECONDS:-1800}" &
fi

shutdown_all() {
    kill "$LLAMA_PID" "$GA_ADMIN_PID" "$AGENT_PID" "$ASR_PID" "$TTS_PID" "$EMB_PID" "$MEDIA_PID" "$NGINX_PID" 2>/dev/null || true
}

trap "shutdown_all; exit" SIGTERM SIGINT

LLAMA_REPORTED_EXIT=0
GA_ADMIN_REPORTED_EXIT=0
AGENT_REPORTED_EXIT=0
ASR_REPORTED_EXIT=0
TTS_REPORTED_EXIT=0
EMB_REPORTED_EXIT=0
MEDIA_REPORTED_EXIT=0

while true; do
    if [ -n "${LLAMA_PID:-}" ] && ! kill -0 "$LLAMA_PID" 2>/dev/null; then
        if [ "$LLAMA_REPORTED_EXIT" = "0" ]; then
            echo "llama-server (PID $LLAMA_PID) exited; respawning so new router-models.ini entries are picked up" >&2
            LLAMA_REPORTED_EXIT=1
        fi
        # `wait` reports 128+signo on signaled exits and the raw exit status otherwise; we split
        # the two so the admin service's /llama/last-exit endpoint can distinguish SIGKILL (OOM
        # killer / hard abort) from SIGTERM (our own reload path).
        LLAMA_WAIT_STATUS=0
        wait "$LLAMA_PID" 2>/dev/null || LLAMA_WAIT_STATUS=$?
        if [ "$LLAMA_WAIT_STATUS" -gt 128 ]; then
            LLAMA_EXIT_SIGNAL=$((LLAMA_WAIT_STATUS - 128))
            LLAMA_EXIT_CODE=""
        else
            LLAMA_EXIT_SIGNAL=""
            LLAMA_EXIT_CODE="$LLAMA_WAIT_STATUS"
        fi
        write_llama_last_exit_record "$LLAMA_PID" "$LLAMA_EXIT_CODE" "$LLAMA_EXIT_SIGNAL"
        truncate_llama_log_tail
        # Same process-substitution form as the initial spawn — see the comment there for why
        # a plain `| tee` pipeline silently broke the restart path before this fix.
        /app/start-llama.sh > >(tee -a "$LLAMA_SERVER_LOG") 2>&1 &
        LLAMA_PID=$!
        echo "$LLAMA_PID" > "$LLAMA_PID_FILE"
        LLAMA_REPORTED_EXIT=0
        echo "Respawned llama-server as PID $LLAMA_PID" >&2
    fi
    if [ -n "${GA_ADMIN_PID:-}" ] && ! kill -0 "$GA_ADMIN_PID" 2>/dev/null; then
        if [ "$GA_ADMIN_REPORTED_EXIT" = "0" ]; then
            echo "ga-admin service (PID $GA_ADMIN_PID) exited; continuing with remaining services" >&2
            GA_ADMIN_REPORTED_EXIT=1
        fi
        GA_ADMIN_PID=""
    fi
    if [ -n "${AGENT_PID:-}" ] && ! kill -0 "$AGENT_PID" 2>/dev/null; then
        if [ "$AGENT_REPORTED_EXIT" = "0" ]; then
            echo "ScriptExecutionAgent (PID $AGENT_PID) exited; continuing with remaining services" >&2
            AGENT_REPORTED_EXIT=1
        fi
        AGENT_PID=""
    fi
    if [ -n "${ASR_PID:-}" ] && ! kill -0 "$ASR_PID" 2>/dev/null; then
        if [ "$ASR_REPORTED_EXIT" = "0" ]; then
            echo "ASR service (PID $ASR_PID) exited; continuing with remaining services" >&2
            ASR_REPORTED_EXIT=1
        fi
        ASR_PID=""
    fi
    if [ -n "${TTS_PID:-}" ] && ! kill -0 "$TTS_PID" 2>/dev/null; then
        if [ "$TTS_REPORTED_EXIT" = "0" ]; then
            echo "TTS service (PID $TTS_PID) exited; continuing with remaining services" >&2
            TTS_REPORTED_EXIT=1
        fi
        TTS_PID=""
    fi
    if [ -n "${EMB_PID:-}" ] && ! kill -0 "$EMB_PID" 2>/dev/null; then
        if [ "$EMB_REPORTED_EXIT" = "0" ]; then
            echo "Embeddings service (PID $EMB_PID) exited; continuing with remaining services" >&2
            EMB_REPORTED_EXIT=1
        fi
        EMB_PID=""
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
