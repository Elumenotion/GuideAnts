#!/usr/bin/env bash
#
# Stop the GuideAnts stack that was started by guideants.sh.
#
# Reads the saved backend choice from .installer_state.env and runs
# docker compose down on the matching compose file.
#
# Flags:
#   --backend <cpu|cuda13|rocm|slim|vulkan>   Override the saved backend.
#   --compose <ghcr|local>                    Compose mode when using --backend (default: ghcr).
#   --help                                    Show this help.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$ROOT_DIR/docker"
ENV_FILE="$DOCKER_DIR/.env"
STATE_FILE="$ROOT_DIR/.installer_state.env"
ROCM_RUNTIME_OVERRIDE_FILE="docker-compose.rocm-runtime.generated.yml"

BACKEND_OVERRIDE=""
COMPOSE_MODE="ghcr"

log()  { printf '[guideants] %s\n' "$*"; }
fail() { printf '[guideants][error] %s\n' "$*" >&2; exit 1; }

# shellcheck source=scripts/rocm-runtime-compose.sh
. "$ROOT_DIR/scripts/rocm-runtime-compose.sh"

usage() {
  sed -n '3,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --backend)
      [[ $# -ge 2 ]] || fail "Missing value for --backend"
      BACKEND_OVERRIDE="$2"; shift ;;
    --compose)
      [[ $# -ge 2 ]] || fail "Missing value for --compose"
      COMPOSE_MODE="$2"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) fail "Unknown option: $1 (try --help)" ;;
  esac
  shift
done

[[ -z "$BACKEND_OVERRIDE" || "$BACKEND_OVERRIDE" =~ ^(cpu|cuda13|rocm|slim|vulkan)$ ]] \
  || fail "--backend must be cpu, cuda13, rocm, slim, or vulkan"
[[ "$COMPOSE_MODE" == "ghcr" || "$COMPOSE_MODE" == "local" ]] \
  || fail "--compose must be ghcr or local"

compose_file_for() {
  local backend="$1"
  if [[ "$COMPOSE_MODE" == "local" ]]; then
    case "$backend" in
      slim)   echo "docker-compose.slim.yml" ;;
      cuda13) echo "docker-compose.cuda.yml" ;;
      rocm)   echo "docker-compose.rocm.yml" ;;
      vulkan) echo "docker-compose.vulkan.yml" ;;
      *)      echo "docker-compose.cpu.yml" ;;
    esac
    return
  fi
  case "$backend" in
    slim)   echo "docker-compose.ghcr-slim.yml" ;;
    cuda13) echo "docker-compose.ghcr-cuda13.yml" ;;
    rocm)   echo "docker-compose.ghcr-rocm.yml" ;;
    vulkan) echo "docker-compose.ghcr-vulkan.yml" ;;
    *)      echo "docker-compose.ghcr-cpu.yml" ;;
  esac
}

if [[ -n "$BACKEND_OVERRIDE" ]]; then
  BACKEND="$BACKEND_OVERRIDE"
elif [[ -f "$STATE_FILE" ]]; then
  # shellcheck disable=SC1090
  BACKEND="$(. "$STATE_FILE" && echo "${BACKEND:-}")"
  if [[ ! "$BACKEND" =~ ^(cpu|cuda13|rocm|slim|vulkan)$ ]]; then
    fail "Saved backend '$BACKEND' in $STATE_FILE is invalid. Use --backend to specify one."
  fi
  saved_compose="$(. "$STATE_FILE" && echo "${COMPOSE_FILE:-}")"
  if [[ -n "$saved_compose" && -f "$DOCKER_DIR/$saved_compose" ]]; then
    COMPOSE_FILE="$saved_compose"
  else
    COMPOSE_FILE="$(compose_file_for "$BACKEND")"
  fi
else
  fail "No saved backend found ($STATE_FILE missing). Use --backend to specify one."
fi

[[ -n "${COMPOSE_FILE:-}" ]] || COMPOSE_FILE="$(compose_file_for "$BACKEND")"
COMPOSE_PATH="$DOCKER_DIR/$COMPOSE_FILE"
[[ -f "$COMPOSE_PATH" ]] || fail "Compose file not found: $COMPOSE_PATH"

HOST_MOUNT_OVERRIDE="$DOCKER_DIR/docker-compose.host-mounts.generated.yml"
compose_args=(-f "$COMPOSE_PATH")
if [[ -f "$HOST_MOUNT_OVERRIDE" ]]; then
  compose_args+=(-f "$HOST_MOUNT_OVERRIDE")
fi

if [[ "$BACKEND" == "rocm" ]]; then
  SELECTED_BACKEND="rocm"
  select_rocm_runtime "$DOCKER_DIR" "$ROOT_DIR"
fi

ROCM_OVERRIDE="$DOCKER_DIR/$ROCM_RUNTIME_OVERRIDE_FILE"
if [[ -f "$ROCM_OVERRIDE" ]]; then
  compose_args+=(-f "$ROCM_OVERRIDE")
fi

log "Stopping GuideAnts ($BACKEND backend)..."
docker compose "${compose_args[@]}" --env-file "$ENV_FILE" down
log "GuideAnts stopped."
