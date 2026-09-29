#!/usr/bin/env bash
#
# GuideAnts Spark backend launcher  (linux/arm64 - NVIDIA DGX Spark / GB10)
#
# Brings up the Spark backend stack (docker/docker-compose.spark.yml): the native
# engine set (llama.cpp / audio.cpp / stable-diffusion.cpp) plus the supporting
# services (docling, documentserver, plantuml, searxng) -- everything in the
# installer stack EXCEPT the UI and the database. Each service publishes its own
# host port so every engine can be probed directly.
#
# What it does, in order:
#   1. Detects the host arch (warns if not aarch64; the Spark is aarch64).
#   2. Checks Docker + Compose are installed and running.
#   3. Checks the NVIDIA driver / CUDA (the GB10 needs the CUDA 13 driver).
#   4. Verifies the local 'guideants-ai:spark' image exists (built on the Spark;
#      this launcher does NOT build it -- see docker/build/build_guideants_ai.sh).
#   5. Ensures the bind-mount volume directories exist.
#   6. Pulls the arm64 support images (searxng, plantuml, docling, documentserver).
#   7. Starts the stack, then health-checks each service on its published port.
#   8. Prints the port map.
#
# Flags:
#   --doctor     Run checks only; change nothing.
#   --down       Stop the stack (docker compose down) and exit.
#   --yes        Assume "yes" for prompts.
#   --help       Show this help.
#
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$ROOT_DIR/docker"
ENV_FILE="$DOCKER_DIR/.env"
COMPOSE_FILE="docker-compose.spark.yml"
PROJECT_NAME="guideants"

# Published host ports (single source of truth; also overridable via GA_*_PORT).
GA_AI_PORT="${GA_AI_PORT:-5110}"
GA_DOCLING_PORT="${GA_DOCLING_PORT:-5111}"
GA_DOCUMENTSERVER_PORT="${GA_DOCUMENTSERVER_PORT:-5112}"
GA_PLANTUML_PORT="${GA_PLANTUML_PORT:-5113}"
GA_SEARXNG_PORT="${GA_SEARXNG_PORT:-5114}"

# The locally-built Spark AI image (pull_policy: never in the compose file).
AI_IMAGE="${GA_AI_SPARK_IMAGE:-guideants-ai:spark-latest}"

MODE="up"
ASSUME_YES="0"

log()  { printf '[guideants-spark] %s\n' "$*"; }
warn() { printf '[guideants-spark][warn] %s\n' "$*" >&2; }
fail() { printf '[guideants-spark][error] %s\n' "$*" >&2; exit 1; }
hr()   { printf '%s\n' "----------------------------------------------------------------"; }

have() { command -v "$1" >/dev/null 2>&1; }

usage() { sed -n '3,29p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

# --- argument parsing --------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    --doctor) MODE="doctor" ;;
    --down)   MODE="down" ;;
    --yes|-y) ASSUME_YES="1" ;;
    --help|-h) usage; exit 0 ;;
    *) fail "Unknown option: $1 (try --help)" ;;
  esac
  shift
done

# =============================================================================
# 1. Host arch
# =============================================================================
ARCH="$(uname -m)"
OS="$(uname -s)"
hr
log "GuideAnts Spark backend launcher"
log "OS: $OS   Arch: $ARCH"
if [[ "$ARCH" != "aarch64" && "$ARCH" != "arm64" ]]; then
  warn "This host is $ARCH, not aarch64."
  warn "The Spark stack is built for the DGX Spark (aarch64). The 'guideants-ai:spark' image"
  warn "must be an arm64 image built on an arm64 host. Continuing anyway."
fi

# =============================================================================
# 2. Docker preflight
# =============================================================================
check_docker() {
  log "Checking Docker..."
  have docker || fail "Docker not found. Install Docker Engine 24+ and the Compose plugin."
  docker compose version >/dev/null 2>&1 || fail "Docker Compose plugin not found. Install/upgrade Docker."
  docker info >/dev/null 2>&1 || fail "Docker daemon not reachable. Start Docker (e.g. 'sudo systemctl start docker')."
  log "Docker is installed and running."
}
check_docker

# =============================================================================
# 3. NVIDIA / CUDA
# =============================================================================
version_gte() {
  local IFS=.
  local -a v1=($1) v2=($2)
  local i max=$(( ${#v1[@]} > ${#v2[@]} ? ${#v1[@]} : ${#v2[@]} ))
  for (( i=0; i<max; i++ )); do
    local a=${v1[i]:-0} b=${v2[i]:-0}
    (( a > b )) && return 0
    (( a < b )) && return 1
  done
  return 0
}

check_gpu() {
  hr
  log "Checking NVIDIA / CUDA (GB10 needs the CUDA 13 driver)..."
  if ! have nvidia-smi; then
    fail "nvidia-smi not found. The Spark needs the NVIDIA driver + nvidia-container-toolkit installed."
  fi
  local drv
  drv="$(nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>/dev/null | head -n1 | tr -d ' ')" || drv=""
  [[ -n "$drv" ]] || fail "nvidia-smi present but returned no driver version. Is the NVIDIA driver working?"
  log "NVIDIA driver version: $drv"
  if ! version_gte "$drv" "580.0"; then
    warn "NVIDIA driver $drv is below R580 (the CUDA 13 minimum). The Spark containers need the CUDA 13 driver."
  fi
  # Confirm the nvidia runtime is available to Docker (the compose file uses the
  # deploy.resources nvidia reservation, which the compose plugin maps to --gpus).
  if docker info --format '{{json .Runtimes}}' 2>/dev/null | grep -q '"nvidia"'; then
    log "nvidia container runtime: present."
  else
    warn "No 'nvidia' runtime in Docker. The CUDA 13 NVIDIA driver + nvidia-container-toolkit"
    warn "must be installed for the GPU services to start."
  fi
}
check_gpu

# =============================================================================
# 4. Local Spark AI image
# =============================================================================
check_ai_image() {
  hr
  log "Checking local Spark AI image: $AI_IMAGE"
  if docker image inspect "$AI_IMAGE" >/dev/null 2>&1; then
    local arch
    arch="$(docker image inspect --format '{{.Architecture}}' "$AI_IMAGE" 2>/dev/null || echo '?')"
    log "Found $AI_IMAGE (architecture: $arch)."
    if [[ "$arch" != "arm64" && "$arch" != "aarch64" ]]; then
      warn "$AI_IMAGE is not an arm64 image. Build the arm64/spark image on the Spark first:"
      warn "  docker/build/build_guideants_ai.sh --backend spark"
    fi
  else
    fail "Local image '$AI_IMAGE' not found. The Spark launcher does not build the AI image."
    fail "Build it on the Spark first:  docker/build/build_guideants_ai.sh --backend spark"
  fi
}
check_ai_image

# =============================================================================
# 5. Volume directories (bind mounts)
# =============================================================================
ensure_volume_dirs() {
  local -a dirs=(
    "$DOCKER_DIR/volumes/content-files"
    "$DOCKER_DIR/volumes/searxng/config"
    "$DOCKER_DIR/volumes/searxng/data"
  )
  local d
  for d in "${dirs[@]}"; do
    mkdir -p "$d"
  done
  log "Volume directories ready: content-files, searxng/config, searxng/data."
}
ensure_volume_dirs

# =============================================================================
# 6. Pull arm64 support images
# =============================================================================
# Local support images (built by docker/build/build_support_images.sh --spark).
LOCAL_SUPPORT_IMAGES=(
  "${GA_SEARXNG_IMAGE:-guideants-searxng:latest}"
  "${GA_PLANTUML_IMAGE:-plantuml-1.2025.2}"
)

# Registry images that are multi-arch (arm64 available) and can be pulled.
PULL_SUPPORT_IMAGES=(
  "${DOCLING_SERVE_CUDA_IMAGE:-quay.io/docling-project/docling-serve-cu130:v1.29.0}"
)

check_local_support_images() {
  hr
  log "Checking local arm64 support images..."
  local img
  for img in "${LOCAL_SUPPORT_IMAGES[@]}"; do
    if docker image inspect "$img" >/dev/null 2>&1; then
      log "  OK   $img"
    else
      fail "Local image '$img' not found."
      fail "Build it first:  docker/build/build_support_images.sh --spark"
    fi
  done
}

pull_registry_support_images() {
  hr
  log "Pulling multi-arch registry images..."
  local img
  for img in "${PULL_SUPPORT_IMAGES[@]}"; do
    log "  $img"
    docker pull "$img" || warn "  pull failed for $img (will retry at up; continue)"
  done
}

# =============================================================================
# 7. Compose up + per-service health
# =============================================================================
compose() {
  docker compose -f "$DOCKER_DIR/$COMPOSE_FILE" --project-name "$PROJECT_NAME" --project-directory "$DOCKER_DIR" "$@"
}

wait_http() { # url seconds -> 0 if 200
  local url="$1" max="${2:-60}" i
  for (( i=0; i<max; i++ )); do
    if curl -fsS --connect-timeout 3 --max-time 5 "$url" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

health_check() {
  hr
  log "Health-checking each service on its published port..."
  local -a checks=(
    "guideants-ai|http://localhost:${GA_AI_PORT}/"
    "docling-serve|http://localhost:${GA_DOCLING_PORT}/version"
    "documentserver|http://localhost:${GA_DOCUMENTSERVER_PORT}/info/info.json"
    "plantuml|http://localhost:${GA_PLANTUML_PORT}/"
    "searxng|http://localhost:${GA_SEARXNG_PORT}/healthz"
  )
  local c svc url
  for c in "${checks[@]}"; do
    svc="${c%%|*}"; url="${c#*|}"
    if wait_http "$url" 45; then
      log "  OK   $svc  ->  $url"
    else
      warn "  FAIL $svc  ->  $url  (may still be starting; check: docker logs ${PROJECT_NAME}-${svc%.*})"
    fi
  done
}

# =============================================================================
# 8. Port map
# =============================================================================
print_port_map() {
  hr
  log "GuideAnts Spark backend is up. Per-service ports:"
  cat <<EOF
  guideants-ai      http://localhost:${GA_AI_PORT}/            (nginx front)
      LLM    http://localhost:${GA_AI_PORT}/llama-cpp/
      ASR    http://localhost:${GA_AI_PORT}/asr/
      TTS    http://localhost:${GA_AI_PORT}/tts/
      EMB    http://localhost:${GA_AI_PORT}/emb/
      SD     http://localhost:${GA_AI_PORT}/sd/
      media  http://localhost:${GA_AI_PORT}/media/
  docling-serve     http://localhost:${GA_DOCLING_PORT}/version
  documentserver    http://localhost:${GA_DOCUMENTSERVER_PORT}/info/info.json
  plantuml          http://localhost:${GA_PLANTUML_PORT}/
  searxng           http://localhost:${GA_SEARXNG_PORT}/
EOF
  hr
  log "Stop the stack:  $0 --down"
}

# =============================================================================
# main
# =============================================================================
case "$MODE" in
  down)
    compose down
    log "Stack stopped."
    exit 0
    ;;
  doctor)
    log "Doctor mode: ran preflight checks only. No changes made."
    exit 0
    ;;
esac

check_local_support_images
pull_registry_support_images
compose up -d
health_check
print_port_map
