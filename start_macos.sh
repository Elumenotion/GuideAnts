#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STATE_FILE="$ROOT_DIR/.installer_state.env"
DOCKER_DIR="$ROOT_DIR/docker"

MODE="install"          # install | doctor
FIX_MODE="0"            # 0 | 1
BACKEND_OVERRIDE=""     # cpu | cuda13 | rocm
COMPOSE_MODE="ghcr"     # ghcr | local
HEALTH_URL="http://localhost:5107/"

usage() {
  cat <<'EOF'
Usage: ./start_macos.sh [options]

Options:
  --doctor               Run checks only, do not change anything.
  --fix                  Attempt limited auto-remediation where possible.
  --backend cpu|cuda13|rocm   Force backend selection.
  --compose ghcr|local   Use GHCR compose files (default) or local build files.
  --help                 Show this help.
EOF
}

log() { printf '[guideants-installer] %s\n' "$*"; }
warn() { printf '[guideants-installer][warn] %s\n' "$*" >&2; }
fail() { printf '[guideants-installer][error] %s\n' "$*" >&2; exit 1; }

save_state() {
  cat >"$STATE_FILE" <<EOF
BACKEND=${SELECTED_BACKEND:-}
COMPOSE_MODE=${COMPOSE_MODE}
LAST_RUN_EPOCH=$(date +%s)
EOF
}

detect_backend() {
  if [[ -n "$BACKEND_OVERRIDE" ]]; then
    SELECTED_BACKEND="$BACKEND_OVERRIDE"
    return
  fi

  if command -v system_profiler >/dev/null 2>&1; then
    if system_profiler SPDisplaysDataType 2>/dev/null | grep -Eiq 'AMD|Radeon'; then
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
      if ensure_cmd brew; then
        warn "Docker CLI missing. Install Docker Desktop manually or with Homebrew cask, then start Docker."
      fi
    fi
    fail "Docker CLI not found."
  fi

  if ! docker compose version >/dev/null 2>&1; then
    fail "Docker Compose plugin not found (docker compose)."
  fi

  if ! docker info >/dev/null 2>&1; then
    fail "Docker daemon is not reachable. Start Docker Desktop and rerun."
  fi
}

select_compose_file() {
  if [[ "$COMPOSE_MODE" == "local" ]]; then
    case "$SELECTED_BACKEND" in
      cuda13) COMPOSE_FILE="docker-compose.cuda.yml" ;;
      rocm) COMPOSE_FILE="docker-compose.rocm.yml" ;;
      *) COMPOSE_FILE="docker-compose.cpu.yml" ;;
    esac
  else
    case "$SELECTED_BACKEND" in
      cuda13) COMPOSE_FILE="docker-compose.ghcr-cuda13.yml" ;;
      rocm) COMPOSE_FILE="docker-compose.ghcr-rocm.yml" ;;
      *) COMPOSE_FILE="docker-compose.ghcr-cpu.yml" ;;
    esac
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
  open "$HEALTH_URL" >/dev/null 2>&1 || true
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
[[ -z "$BACKEND_OVERRIDE" || "$BACKEND_OVERRIDE" == "cpu" || "$BACKEND_OVERRIDE" == "cuda13" || "$BACKEND_OVERRIDE" == "rocm" ]] || fail "--backend must be cpu, cuda13, or rocm"

check_prereqs
detect_backend
select_compose_file

log "Selected backend: $SELECTED_BACKEND"
log "Compose file: docker/$COMPOSE_FILE"

if [[ "$MODE" == "doctor" ]]; then
  log "Doctor mode complete. No changes were made."
  save_state
  exit 0
fi

if [[ "$(uname -m)" == "arm64" ]]; then
  export DOCKER_DEFAULT_PLATFORM=linux/amd64
fi

pushd "$DOCKER_DIR" >/dev/null
docker compose -f "$COMPOSE_FILE" up -d
popd >/dev/null

if wait_for_health; then
  log "GuideAnts is up: $HEALTH_URL"
  open_browser
else
  warn "GuideAnts did not pass health check in time. Check: docker compose -f docker/$COMPOSE_FILE ps"
fi

save_state
