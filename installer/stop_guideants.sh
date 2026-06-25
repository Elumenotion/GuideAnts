#!/usr/bin/env bash
#
# Stop the GuideAnts stack that was started by guideants.sh.
#
# Reads the saved backend choice from .installer_state.env and runs
# docker compose down on the matching compose file.
#
# Flags:
#   --backend <cpu|cuda13|rocm|slim>   Override the saved backend.
#   --help                             Show this help.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$ROOT_DIR/docker"
ENV_FILE="$DOCKER_DIR/.env"
STATE_FILE="$ROOT_DIR/.installer_state.env"

BACKEND_OVERRIDE=""

log()  { printf '[guideants] %s\n' "$*"; }
fail() { printf '[guideants][error] %s\n' "$*" >&2; exit 1; }

usage() {
  sed -n '3,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --backend)
      [[ $# -ge 2 ]] || fail "Missing value for --backend"
      BACKEND_OVERRIDE="$2"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) fail "Unknown option: $1 (try --help)" ;;
  esac
  shift
done

[[ -z "$BACKEND_OVERRIDE" || "$BACKEND_OVERRIDE" =~ ^(cpu|cuda13|rocm|slim)$ ]] \
  || fail "--backend must be cpu, cuda13, rocm, or slim"

compose_file_for() {
  case "$1" in
    slim)   echo "docker-compose.ghcr-slim.yml" ;;
    cuda13) echo "docker-compose.ghcr-cuda13.yml" ;;
    rocm)   echo "docker-compose.ghcr-rocm.yml" ;;
    *)      echo "docker-compose.ghcr-cpu.yml" ;;
  esac
}

if [[ -n "$BACKEND_OVERRIDE" ]]; then
  BACKEND="$BACKEND_OVERRIDE"
elif [[ -f "$STATE_FILE" ]]; then
  BACKEND="$(. "$STATE_FILE" && echo "$BACKEND")"
  if [[ ! "$BACKEND" =~ ^(cpu|cuda13|rocm|slim)$ ]]; then
    fail "Saved backend '$BACKEND' in $STATE_FILE is invalid. Use --backend to specify one."
  fi
else
  fail "No saved backend found ($STATE_FILE missing). Use --backend to specify one."
fi

COMPOSE_FILE="$(compose_file_for "$BACKEND")"
COMPOSE_PATH="$DOCKER_DIR/$COMPOSE_FILE"

[[ -f "$COMPOSE_PATH" ]] || fail "Compose file not found: $COMPOSE_PATH"

HOST_MOUNT_OVERRIDE="$DOCKER_DIR/docker-compose.host-mounts.generated.yml"
compose_args=(-f "$COMPOSE_PATH")
if [[ -f "$HOST_MOUNT_OVERRIDE" ]]; then
  compose_args+=(-f "$HOST_MOUNT_OVERRIDE")
fi

log "Stopping GuideAnts ($BACKEND backend)..."
docker compose "${compose_args[@]}" --env-file "$ENV_FILE" down
log "GuideAnts stopped."
