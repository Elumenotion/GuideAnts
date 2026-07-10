#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STATE_FILE="$ROOT_DIR/.installer_state.env"
DOCKER_DIR="$ROOT_DIR/docker"

MODE="install"          # install | doctor
FIX_MODE="0"            # 0 | 1
BACKEND_OVERRIDE=""     # cpu | cuda13 | rocm | slim | vulkan
COMPOSE_MODE="ghcr"     # ghcr | local
HEALTH_URL="http://localhost:5107/"
HOST_MOUNT_OVERRIDE_FILE="docker-compose.host-mounts.generated.yml"
ROCM_RUNTIME_OVERRIDE_FILE="docker-compose.rocm-runtime.generated.yml"
DOCKER_DIRECTORY="docker"
START_COMMAND="start_linux.sh"

usage() {
  cat <<'EOF'
Usage: ./start_linux.sh [options]

Options:
  --doctor               Run checks only, do not change anything.
  --fix                  Attempt limited auto-remediation where possible.
  --backend cpu|cuda13|rocm|slim|vulkan   Force backend selection. slim and vulkan are explicit only and are not auto-detected.
  --compose ghcr|local   Use GHCR compose files (default) or local build files.
  --help                 Show this help.
EOF
}

log() { printf '[guideants-installer] %s\n' "$*"; }
warn() { printf '[guideants-installer][warn] %s\n' "$*" >&2; }
fail() { printf '[guideants-installer][error] %s\n' "$*" >&2; exit 1; }

# shellcheck source=installer/scripts/rocm-runtime-compose.sh
. "$ROOT_DIR/installer/scripts/rocm-runtime-compose.sh"
export ROCM_RUNTIME_LOG_FN=log
export ROCM_RUNTIME_WARN_FN=warn

save_state() {
  cat >"$STATE_FILE" <<EOF
BACKEND=${SELECTED_BACKEND:-}
COMPOSE_MODE=${COMPOSE_MODE}
COMPOSE_FILE=${COMPOSE_FILE}
HOST_MOUNT_OVERRIDE_FILE=${HOST_MOUNT_OVERRIDE_FILE}
DOCKER_DIRECTORY=${DOCKER_DIRECTORY}
START_COMMAND=${START_COMMAND}
LAST_RUN_EPOCH=$(date +%s)
EOF
}

detect_backend() {
  if [[ -n "$BACKEND_OVERRIDE" ]]; then
    SELECTED_BACKEND="$BACKEND_OVERRIDE"
    return
  fi

  if command -v nvidia-smi >/dev/null 2>&1; then
    if nvidia-smi >/dev/null 2>&1; then
      SELECTED_BACKEND="cuda13"
      return
    fi
  fi

  if [[ -e "/dev/kfd" ]]; then
    SELECTED_BACKEND="rocm"
    return
  fi

  if command -v rocminfo >/dev/null 2>&1; then
    if rocminfo >/dev/null 2>&1; then
      SELECTED_BACKEND="rocm"
      return
    fi
  fi

  SELECTED_BACKEND="cpu"
}

ensure_cmd() {
  local cmd="$1"
  command -v "$cmd" >/dev/null 2>&1 || return 1
}

check_prereqs() {
  log "Running preflight checks..."

  if ! ensure_cmd docker; then
    if [[ "$FIX_MODE" == "1" ]]; then
      warn "Docker is missing. Auto-install is distro-specific; install Docker Engine + Compose plugin, then rerun."
    fi
    fail "Docker CLI not found."
  fi

  if ! docker compose version >/dev/null 2>&1; then
    fail "Docker Compose plugin not found (docker compose)."
  fi

  if ! docker info >/dev/null 2>&1; then
    fail "Docker daemon is not reachable. Start Docker and rerun."
  fi
}

select_compose_file() {
  if [[ "$COMPOSE_MODE" == "local" ]]; then
    case "$SELECTED_BACKEND" in
      slim) COMPOSE_FILE="docker-compose.slim.yml" ;;
      cuda13) COMPOSE_FILE="docker-compose.cuda.yml" ;;
      rocm) COMPOSE_FILE="docker-compose.rocm.yml" ;;
      vulkan) COMPOSE_FILE="docker-compose.vulkan.yml" ;;
      *) COMPOSE_FILE="docker-compose.cpu.yml" ;;
    esac
  else
    case "$SELECTED_BACKEND" in
      slim) COMPOSE_FILE="docker-compose.ghcr-slim.yml" ;;
      cuda13) COMPOSE_FILE="docker-compose.ghcr-cuda13.yml" ;;
      rocm) COMPOSE_FILE="docker-compose.ghcr-rocm.yml" ;;
      vulkan) COMPOSE_FILE="docker-compose.ghcr-vulkan.yml" ;;
      *) COMPOSE_FILE="docker-compose.ghcr-cpu.yml" ;;
    esac
  fi
}

select_vulkan_runtime() {
  [[ "$SELECTED_BACKEND" == "vulkan" ]] || return 0

  if docker info --format '{{.OperatingSystem}}' 2>/dev/null | grep -q 'Docker Desktop'; then
    log "Vulkan: Docker Desktop → Mesa dzn over D3D12 (/dev/dxg). Using built-in defaults (no env)."
    return 0
  fi

  local dev="/dev/null"
  [[ -e /dev/dri ]] && dev="/dev/dri"
  export GA_VULKAN_DEVICE="$dev"
  export GA_VULKAN_DRIVER_LIBS="/usr/lib"
  export GA_VULKAN_LD_LIBRARY_PATH="/usr/lib/x86_64-linux-gnu"

  if docker info --format '{{json .Runtimes}}' 2>/dev/null | grep -q '"nvidia"'; then
    export GA_VULKAN_RUNTIME="nvidia"
    export GA_VULKAN_ICD="/usr/share/vulkan/icd.d/nvidia_icd.json"
    log "Vulkan: native Linux NVIDIA → nvidia runtime injects the Vulkan ICD (device $dev)."
  elif [[ -e /dev/dri ]]; then
    local icd=""
    for v in /sys/class/drm/renderD*/device/vendor; do
      [[ -r "$v" ]] || continue
      case "$(cat "$v" 2>/dev/null)" in
        0x1002) icd="/usr/share/vulkan/icd.d/radeon_icd.x86_64.json"; break ;;
        0x8086) icd="/usr/share/vulkan/icd.d/intel_icd.x86_64.json";  break ;;
      esac
    done
    if [[ -n "$icd" ]]; then
      export GA_VULKAN_ICD="$icd"
      log "Vulkan: native Linux Mesa via /dev/dri (ICD $(basename "$icd"))."
    else
      export GA_VULKAN_ICD="/usr/share/vulkan/icd.d/radeon_icd.x86_64.json"
      warn "Vulkan: /dev/dri present but GPU vendor undetermined; assuming AMD RADV. Override GA_VULKAN_ICD if this is an Intel GPU."
    fi
  else
    warn "Vulkan: native Linux with no nvidia runtime and no /dev/dri — no GPU device found."
    warn "        LLM and image generation will run on CPU. Install Mesa (AMD/Intel) or the nvidia-container-toolkit (NVIDIA)."
  fi
}

wait_for_health() {
  log "Waiting for GuideAnts UI to become reachable at $HEALTH_URL"
  for _ in $(seq 1 120); do
    if curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  return 1
}

open_browser() {
  if ensure_cmd xdg-open; then
    xdg-open "$HEALTH_URL" >/dev/null 2>&1 || true
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --doctor) MODE="doctor" ;;
    --fix) FIX_MODE="1" ;;
    --backend)
      [[ $# -ge 2 ]] || fail "Missing value for --backend"
      BACKEND_OVERRIDE="$2"
      shift
      ;;
    --compose)
      [[ $# -ge 2 ]] || fail "Missing value for --compose"
      COMPOSE_MODE="$2"
      shift
      ;;
    --help|-h) usage; exit 0 ;;
    *) fail "Unknown option: $1" ;;
  esac
  shift
done

[[ "$COMPOSE_MODE" == "ghcr" || "$COMPOSE_MODE" == "local" ]] || fail "--compose must be ghcr or local"
[[ -z "$BACKEND_OVERRIDE" || "$BACKEND_OVERRIDE" == "cpu" || "$BACKEND_OVERRIDE" == "cuda13" || "$BACKEND_OVERRIDE" == "rocm" || "$BACKEND_OVERRIDE" == "slim" || "$BACKEND_OVERRIDE" == "vulkan" ]] || fail "--backend must be cpu, cuda13, rocm, slim, or vulkan"

check_prereqs
detect_backend
select_compose_file
select_vulkan_runtime
select_rocm_runtime "$DOCKER_DIR"

log "Selected backend: $SELECTED_BACKEND"
log "Compose file: docker/$COMPOSE_FILE"

if [[ "$MODE" == "doctor" ]]; then
  log "Doctor mode complete. No changes were made."
  save_state
  exit 0
fi

pushd "$DOCKER_DIR" >/dev/null
compose_args=(-f "$COMPOSE_FILE")
if [[ -f "$HOST_MOUNT_OVERRIDE_FILE" ]]; then
  if docker compose -f "$COMPOSE_FILE" -f "$HOST_MOUNT_OVERRIDE_FILE" config >/dev/null 2>&1; then
    compose_args+=(-f "$HOST_MOUNT_OVERRIDE_FILE")
  else
    warn "Ignoring invalid host mount override docker/$HOST_MOUNT_OVERRIDE_FILE. Recreate mounts to regenerate it."
  fi
fi
if [[ -f "$ROCM_RUNTIME_OVERRIDE_FILE" ]]; then
  if docker compose -f "$COMPOSE_FILE" -f "$ROCM_RUNTIME_OVERRIDE_FILE" config >/dev/null 2>&1; then
    compose_args+=(-f "$ROCM_RUNTIME_OVERRIDE_FILE")
    log "Including ROCm runtime override: $ROCM_RUNTIME_OVERRIDE_FILE"
  else
    warn "Ignoring invalid ROCm runtime override docker/$ROCM_RUNTIME_OVERRIDE_FILE."
  fi
fi
docker compose "${compose_args[@]}" up -d
popd >/dev/null

if wait_for_health; then
  log "GuideAnts is up: $HEALTH_URL"
  open_browser
else
  warn "GuideAnts did not pass health check in time. Check: docker compose -f docker/$COMPOSE_FILE ps"
fi

save_state
