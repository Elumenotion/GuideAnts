#!/usr/bin/env bash
#
# Stop the GuideAnts stack that was started by guideants.sh.
#
# Reads saved component selections from .installer_state.env and runs
# docker compose down on the matching compose fragment list.
#
# Flags:
#   --help   Show this help.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$ROOT_DIR/docker"
ENV_FILE="$DOCKER_DIR/.env"
STATE_FILE="$ROOT_DIR/.installer_state.env"

log()  { printf '[guideants] %s\n' "$*"; }
fail() { printf '[guideants][error] %s\n' "$*" >&2; exit 1; }

# shellcheck source=scripts/rocm-runtime-compose.sh
. "$ROOT_DIR/scripts/rocm-runtime-compose.sh"
# shellcheck source=scripts/installer-wizard.sh
. "$ROOT_DIR/scripts/installer-wizard.sh"

usage() {
  sed -n '3,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help|-h) usage; exit 0 ;;
    *) fail "Unknown option: $1 (try --help)" ;;
  esac
done

[[ -f "$STATE_FILE" ]] || fail "No saved state found ($STATE_FILE). Run guideants.sh first."

installer_legacy_state
installer_build_compose_args_from_state "$ROOT_DIR" "$STATE_FILE" 1 1 0

if [[ "$AI_BACKEND" == "rocm" ]]; then
  select_rocm_runtime "$DOCKER_DIR" "$ROOT_DIR"
  [[ -f "$DOCKER_DIR/docker-compose.rocm-runtime.generated.yml" ]] && \
    COMPOSE_ARGS+=(-f "$DOCKER_DIR/docker-compose.rocm-runtime.generated.yml")
fi

log "Stopping GuideAnts (DB=$DB_LAYOUT, AI=$AI_BACKEND)..."
docker compose "${COMPOSE_ARGS[@]}" --env-file "$ENV_FILE" down
log "GuideAnts stopped."
