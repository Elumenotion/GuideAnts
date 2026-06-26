#!/usr/bin/env bash
#
# GuideAnts portable launcher — one script, every OS.
#
#   Linux / macOS : ./guideants.sh
#   Windows       : open a WSL or Git Bash terminal, then:  bash guideants.sh
#
# What it does, in order:
#   1. Detects your OS / shell environment.
#   2. Checks Docker is installed and running.
#   3. Reports memory and disk (warns if low, never blocks).
#   4. Walks you through which backend to use (cpu / cuda13 / rocm / slim / vulkan).
#   5. Checks the registry for newer images and asks before updating.
#   6. Starts the stack, waits for health, opens your browser.
#
# Flags:
#   --doctor                 Run checks only; change nothing.
#   --backend <cpu|cuda13|rocm|slim|vulkan>   Skip the backend prompt.
#   --compose <ghcr|local>   Use GHCR images (default) or local build images.
#   --mount <path>           Mount a host folder into a project (requires prior login).
#   --unmount                Interactively remove a host folder mount (requires prior login).
#   --reconfigure            Re-prompt for backend even if one was saved.
#   --yes                    Assume "yes" for prompts (auto-accept updates).
#   --help                   Show this help.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$ROOT_DIR/docker"
ENV_FILE="$DOCKER_DIR/.env"
STATE_FILE="$ROOT_DIR/.installer_state.env"
HEALTH_URL="http://localhost:5107/"
HOST_MOUNT_OVERRIDE_FILE="docker-compose.host-mounts.generated.yml"
DOCKER_DIRECTORY="docker"

MODE="install"            # install | doctor
BACKEND_OVERRIDE=""       # cpu | cuda13 | rocm | slim | vulkan
COMPOSE_MODE="ghcr"       # ghcr | local
ASSUME_YES="0"            # 0 | 1
RECONFIGURE="0"           # 0 | 1
MOUNT_PATH=""             # host folder to bind-mount
UNMOUNT="0"               # 0 | 1

# --- logging helpers ---------------------------------------------------------
log()  { printf '[guideants] %s\n' "$*"; }
warn() { printf '[guideants][warn] %s\n' "$*" >&2; }
fail() { printf '[guideants][error] %s\n' "$*" >&2; exit 1; }
hr()   { printf '%s\n' "----------------------------------------------------------------"; }

usage() {
  sed -n '3,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

# --- argument parsing --------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    --doctor) MODE="doctor" ;;
    --yes|-y) ASSUME_YES="1" ;;
    --reconfigure) RECONFIGURE="1" ;;
    --backend)
      [[ $# -ge 2 ]] || fail "Missing value for --backend"
      BACKEND_OVERRIDE="$2"; shift ;;
    --compose)
      [[ $# -ge 2 ]] || fail "Missing value for --compose"
      COMPOSE_MODE="$2"; shift ;;
    --mount)
      [[ $# -ge 2 ]] || fail "Missing value for --mount"
      MOUNT_PATH="$2"; shift ;;
    --unmount) UNMOUNT="1" ;;
    --help|-h) usage; exit 0 ;;
    *) fail "Unknown option: $1 (try --help)" ;;
  esac
  shift
done

[[ -z "$BACKEND_OVERRIDE" || "$BACKEND_OVERRIDE" =~ ^(cpu|cuda13|rocm|slim|vulkan)$ ]] \
  || fail "--backend must be cpu, cuda13, rocm, slim, or vulkan"
[[ "$COMPOSE_MODE" == "ghcr" || "$COMPOSE_MODE" == "local" ]] \
  || fail "--compose must be ghcr or local"

# =============================================================================
# 1. Detect OS / shell environment
# =============================================================================
OS="unknown"           # linux | macos | windows
IS_WSL="0"
case "$(uname -s)" in
  Linux)
    OS="linux"
    if grep -qiE 'microsoft|wsl' /proc/version 2>/dev/null; then
      OS="windows"; IS_WSL="1"
    fi
    ;;
  Darwin) OS="macos" ;;
  MINGW*|MSYS*|CYGWIN*) OS="windows" ;;
esac
ARCH="$(uname -m)"

# =============================================================================
# 2. Docker preflight
# =============================================================================
have() { command -v "$1" >/dev/null 2>&1; }

check_docker() {
  log "Checking Docker..."
  if ! have docker; then
    case "$OS" in
      macos)   fail "Docker not found. Install Docker Desktop: https://www.docker.com/products/docker-desktop/ then rerun." ;;
      windows) fail "Docker not found. Install Docker Desktop (with WSL2 integration), then rerun this from a WSL terminal." ;;
      *)       fail "Docker not found. Install Docker Engine 24+ and the Compose plugin, then rerun." ;;
    esac
  fi
  if ! docker compose version >/dev/null 2>&1; then
    fail "Docker Compose plugin not found. Install/upgrade Docker (the legacy 'docker-compose' v1 is not supported)."
  fi
  if ! docker info >/dev/null 2>&1; then
    case "$OS" in
      linux) fail "Docker daemon not reachable. Start it (e.g. 'sudo systemctl start docker') and rerun." ;;
      *)     fail "Docker daemon not reachable. Start Docker Desktop and rerun." ;;
    esac
  fi
  if [[ "$OS" == "windows" ]] && have wsl.exe; then
    wsl.exe --status >/dev/null 2>&1 || warn "Could not confirm WSL2 status; Docker Desktop may still work if configured."
  fi
  log "Docker is installed and running."
}

# =============================================================================
# 3. Memory & disk reporting (warn, never block)
# =============================================================================
host_ram_gib() {
  case "$OS" in
    macos) awk -v b="$(sysctl -n hw.memsize 2>/dev/null || echo 0)" 'BEGIN{printf "%.0f", b/1073741824}' ;;
    *)
      if [[ -r /proc/meminfo ]]; then
        awk '/MemTotal/{printf "%.0f", $2/1048576}' /proc/meminfo
      else echo "0"; fi
      ;;
  esac
}

docker_ram_gib() {
  local b; b="$(docker info --format '{{.MemTotal}}' 2>/dev/null || echo 0)"
  awk -v b="$b" 'BEGIN{printf "%.0f", b/1073741824}'
}

report_resources() {
  local hram dram disk
  hram="$(host_ram_gib)"; dram="$(docker_ram_gib)"
  log "Host RAM: ${hram} GiB    Docker engine RAM: ${dram} GiB"
  if disk="$(df -Ph "$DOCKER_DIR" 2>/dev/null | awk 'NR==2{print $4}')"; then
    log "Free disk on bundle volume: ${disk}"
  fi
  LOW_RAM="0"
  if [[ "${dram:-0}" -gt 0 && "${dram:-0}" -lt 16 ]]; then
    LOW_RAM="1"
    warn "Docker has < 16 GiB. Full stacks (cpu/cuda13/rocm) want >=16 GiB; 'slim' runs in ~8 GiB."
  fi
}

# =============================================================================
# 4. Interactive backend walkthrough
# =============================================================================
ask_yes_no() { # prompt default(Y/N) -> 0 yes / 1 no
  local prompt="$1" def="${2:-Y}" reply
  if [[ "$ASSUME_YES" == "1" ]]; then return 0; fi
  read -r -p "$prompt " reply || reply=""
  reply="${reply:-$def}"
  [[ "$reply" =~ ^[Yy] ]]
}

nvidia_driver_major() {
  have nvidia-smi || return 1
  local v; v="$(nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>/dev/null | head -n1 | tr -d ' ')" || return 1
  [[ -n "$v" ]] || return 1
  echo "${v%%.*}"
}

nvidia_driver_full() {
  have nvidia-smi || return 1
  nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>/dev/null | head -n1 | tr -d ' '
}

nvidia_cuda_version() {
  have nvidia-smi || return 1
  nvidia-smi 2>/dev/null | grep -oP 'CUDA Version:\s*\K[0-9]+\.[0-9]+' | head -n1
}

rocm_version() {
  if [[ -f /opt/rocm/.info/version ]]; then
    cat /opt/rocm/.info/version 2>/dev/null | head -n1 | tr -d ' '
    return
  fi
  if have rocminfo; then
    rocminfo 2>/dev/null | grep -oP 'ROCm Runtime Version:\s*\K[0-9]+\.[0-9]+(\.[0-9]+)?' | head -n1
    return
  fi
  if have apt && dpkg -l rocm-core 2>/dev/null | grep -q '^ii'; then
    dpkg -l rocm-core 2>/dev/null | awk '/^ii/{print $3}' | head -n1
    return
  fi
  return 1
}

# Compare two dotted version strings: returns 0 if $1 >= $2
version_gte() {
  local IFS=.
  local -a v1=($1) v2=($2)
  local i max=$(( ${#v1[@]} > ${#v2[@]} ? ${#v1[@]} : ${#v2[@]} ))
  for (( i=0; i<max; i++ )); do
    local a=${v1[i]:-0} b=${v2[i]:-0}
    if (( a > b )); then return 0; fi
    if (( a < b )); then return 1; fi
  done
  return 0
}

FALLBACK_MIN_NVIDIA_DRIVER="580.0"
FALLBACK_MIN_CUDA_VERSION="13.0"
MIN_ROCM_VERSION="6.0.0"

# Resolve the guideants-ai image reference from a compose file.
resolve_cuda_image_ref() {
  local compose_path="$1"
  docker compose -f "$compose_path" --env-file "$ENV_FILE" config --format json 2>/dev/null \
    | python3 -c "
import json,sys
svc = json.load(sys.stdin).get('services',{}).get('guideants-ai',{})
img = svc.get('image','')
if img: print(img)
" 2>/dev/null
}

# Inspect a CUDA image for NVIDIA_REQUIRE_CUDA and set MIN_CUDA_VERSION / MIN_NVIDIA_DRIVER.
# Tries local inspect first; falls back to pulling the image config from the registry.
detect_cuda_requirements() {
  local image_ref="$1" envs=""
  MIN_CUDA_VERSION="$FALLBACK_MIN_CUDA_VERSION"
  MIN_NVIDIA_DRIVER="$FALLBACK_MIN_NVIDIA_DRIVER"

  envs="$(docker inspect "$image_ref" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null || true)"

  if [[ -z "$envs" ]]; then
    log "Image not available locally; pulling config from registry..."
    local token manifest config_digest config_json
    local repo="${image_ref%%:*}" tag="${image_ref##*:}"
    [[ "$tag" == "$image_ref" ]] && tag="latest"

    # GHCR auth (public, anonymous token)
    if [[ "$repo" == ghcr.io/* ]]; then
      local ghcr_path="${repo#ghcr.io/}"
      token="$(curl -fsSL "https://ghcr.io/token?scope=repository:${ghcr_path}:pull" 2>/dev/null \
        | python3 -c "import json,sys; print(json.load(sys.stdin).get('token',''))" 2>/dev/null || true)"
      if [[ -n "$token" ]]; then
        manifest="$(curl -fsSL -H "Authorization: Bearer $token" \
          -H "Accept: application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json" \
          "https://ghcr.io/v2/${ghcr_path}/manifests/${tag}" 2>/dev/null || true)"
        config_digest="$(echo "$manifest" | python3 -c "import json,sys; print(json.load(sys.stdin).get('config',{}).get('digest',''))" 2>/dev/null || true)"
        if [[ -n "$config_digest" ]]; then
          config_json="$(curl -fsSL -H "Authorization: Bearer $token" \
            "https://ghcr.io/v2/${ghcr_path}/blobs/${config_digest}" 2>/dev/null || true)"
          envs="$(echo "$config_json" | python3 -c "
import json,sys
cfg = json.load(sys.stdin)
for e in cfg.get('config',{}).get('Env',[]):
    print(e)
" 2>/dev/null || true)"
        fi
      fi
    fi
  fi

  if [[ -z "$envs" ]]; then
    warn "Could not inspect CUDA image; using fallback minimums (CUDA >= $MIN_CUDA_VERSION, driver >= $MIN_NVIDIA_DRIVER)."
    return
  fi

  local require_line
  require_line="$(echo "$envs" | grep '^NVIDIA_REQUIRE_CUDA=' | head -n1 || true)"
  if [[ -n "$require_line" ]]; then
    local require_val="${require_line#NVIDIA_REQUIRE_CUDA=}"
    local parsed_cuda parsed_driver
    parsed_cuda="$(echo "$require_val" | grep -oP 'cuda>=\K[0-9]+\.[0-9]+' | head -n1 || true)"
    parsed_driver="$(echo "$require_val" | grep -oP 'driver>=\K[0-9]+' | sort -n | head -n1 || true)"
    if [[ -n "$parsed_cuda" ]]; then
      MIN_CUDA_VERSION="$parsed_cuda"
    fi
    if [[ -n "$parsed_driver" ]]; then
      MIN_NVIDIA_DRIVER="${parsed_driver}.0"
    fi
    log "Image requires: CUDA >= $MIN_CUDA_VERSION, NVIDIA driver >= $MIN_NVIDIA_DRIVER"
  else
    warn "Image has no NVIDIA_REQUIRE_CUDA env var; using fallback minimums."
  fi
}

check_gpu_drivers() {
  local backend="$1"
  if [[ "$backend" == "cuda13" ]]; then
    hr
    log "Checking NVIDIA / CUDA driver versions..."

    local compose_path="$DOCKER_DIR/$(compose_file_for "$backend")"
    local cuda_image_ref
    cuda_image_ref="$(resolve_cuda_image_ref "$compose_path" || true)"
    if [[ -n "$cuda_image_ref" ]]; then
      log "CUDA image: $cuda_image_ref"
      detect_cuda_requirements "$cuda_image_ref"
    else
      MIN_CUDA_VERSION="$FALLBACK_MIN_CUDA_VERSION"
      MIN_NVIDIA_DRIVER="$FALLBACK_MIN_NVIDIA_DRIVER"
      warn "Could not resolve CUDA image from compose file; using fallback minimums."
    fi

    local drv cuda_ver
    if ! drv="$(nvidia_driver_full)"; then
      warn "nvidia-smi not found or not working. CUDA backend requires NVIDIA drivers."
      warn "Install the latest NVIDIA drivers: https://www.nvidia.com/Download/index.aspx"
      return
    fi
    log "NVIDIA driver version: $drv"
    if ! version_gte "$drv" "$MIN_NVIDIA_DRIVER"; then
      warn "NVIDIA driver $drv is below the minimum ($MIN_NVIDIA_DRIVER) required by the CUDA image."
      warn "The CUDA container will refuse to start without a compatible driver."
      warn "Update your drivers: https://www.nvidia.com/Download/index.aspx"
      case "$OS" in
        linux)
          warn "  Ubuntu/Debian: sudo apt update && sudo apt install --upgrade nvidia-driver-${MIN_NVIDIA_DRIVER%%.*}"
          warn "  RHEL/Fedora:   sudo dnf upgrade nvidia-driver"
          ;;
        windows)
          warn "  Download the latest driver from the NVIDIA website, or use GeForce Experience to update."
          ;;
      esac
      fail "Aborting. Update your NVIDIA drivers to >= $MIN_NVIDIA_DRIVER and rerun, or use --backend cpu."
    else
      log "NVIDIA driver $drv meets minimum requirement (>= $MIN_NVIDIA_DRIVER)."
    fi
    if cuda_ver="$(nvidia_cuda_version)"; then
      log "CUDA version reported by driver: $cuda_ver"
      if ! version_gte "$cuda_ver" "$MIN_CUDA_VERSION"; then
        warn "CUDA $cuda_ver is below the minimum ($MIN_CUDA_VERSION) required by the CUDA image."
        warn "The CUDA container will refuse to start without CUDA >= $MIN_CUDA_VERSION support."
        warn "Update your NVIDIA drivers to get CUDA $MIN_CUDA_VERSION+ support."
        fail "Aborting. Update your NVIDIA drivers and rerun, or use --backend cpu."
      else
        log "CUDA $cuda_ver meets minimum requirement (>= $MIN_CUDA_VERSION)."
      fi
    else
      warn "Could not detect CUDA version from nvidia-smi."
    fi
  elif [[ "$backend" == "rocm" ]]; then
    hr
    log "Checking AMD ROCm driver version..."
    local rver
    if rver="$(rocm_version)" && [[ -n "$rver" ]]; then
      log "ROCm version: $rver"
      if ! version_gte "$rver" "$MIN_ROCM_VERSION"; then
        warn "ROCm $rver is below the minimum ($MIN_ROCM_VERSION)."
        warn "Update ROCm: https://rocm.docs.amd.com/projects/install-on-linux/en/latest/"
        warn "  Ubuntu/Debian: sudo apt update && sudo apt upgrade rocm"
        warn "  RHEL/Fedora:   sudo dnf upgrade rocm"
        if ! ask_yes_no "Continue anyway with outdated ROCm? [y/N]" "N"; then
          fail "Aborting. Update ROCm and rerun."
        fi
      else
        log "ROCm $rver meets minimum requirement (>= $MIN_ROCM_VERSION)."
      fi
    else
      if [[ -e /dev/kfd ]]; then
        warn "AMD GPU detected (/dev/kfd) but could not determine ROCm version."
        warn "Ensure ROCm is properly installed: https://rocm.docs.amd.com/projects/install-on-linux/en/latest/"
        warn "Without a working ROCm installation, GPU acceleration may not function."
      else
        warn "No AMD GPU device found (/dev/kfd missing). ROCm backend requires an AMD GPU with ROCm drivers."
        warn "Install ROCm: https://rocm.docs.amd.com/projects/install-on-linux/en/latest/"
        if ! ask_yes_no "Continue anyway without ROCm? [y/N]" "N"; then
          fail "Aborting. Install ROCm drivers and rerun."
        fi
      fi
    fi
  elif [[ "$backend" == "vulkan" ]]; then
    hr
    # select_vulkan_runtime() (run just before this) already detected the host,
    # exported the GA_VULKAN_* wiring, and logged the chosen GPU path. Vulkan
    # never hard-fails the installer: a GPU-less host degrades to CPU with a warning.
    log "Vulkan GPU wiring resolved (runtime='${GA_VULKAN_RUNTIME:-runc}', device='${GA_VULKAN_DEVICE:-/dev/dxg}', icd='$(basename "${GA_VULKAN_ICD:-dzn_icd.json}")')."
  fi
}

recommend_backend() {
  # Sets RECOMMENDED and REASON.
  local major
  if major="$(nvidia_driver_major)"; then
    if [[ "$major" =~ ^[0-9]+$ && "$major" -ge 580 ]]; then
      RECOMMENDED="cuda13"; REASON="NVIDIA driver R${major} detected (>= R580)."
      return
    fi
    RECOMMENDED="cpu"; REASON="NVIDIA driver R${major} is below the CUDA 13 minimum (R580); using CPU."
    return
  fi
  if [[ -e /dev/kfd ]] || { have rocminfo && rocminfo >/dev/null 2>&1; }; then
    RECOMMENDED="rocm"; REASON="AMD ROCm device detected (/dev/kfd)."
    return
  fi
  if [[ "${LOW_RAM:-0}" == "1" ]]; then
    RECOMMENDED="slim"; REASON="No usable local GPU and limited RAM; slim (cloud AI) recommended."
  else
    RECOMMENDED="cpu"; REASON="No usable local GPU detected; using CPU."
  fi
}

choose_backend() {
  if [[ -n "$BACKEND_OVERRIDE" ]]; then
    SELECTED_BACKEND="$BACKEND_OVERRIDE"
    log "Backend forced via --backend: $SELECTED_BACKEND"
    return
  fi
  if [[ "$RECONFIGURE" == "0" && -f "$STATE_FILE" ]]; then
    local saved_backend
    saved_backend="$(. "$STATE_FILE" && echo "${BACKEND:-}")"
    if [[ "$saved_backend" =~ ^(cpu|cuda13|rocm|slim|vulkan)$ ]]; then
      SELECTED_BACKEND="$saved_backend"
      log "Using previously saved backend: $SELECTED_BACKEND (run with --reconfigure to change)"
      return
    fi
  fi
  recommend_backend
  hr
  log "Recommended backend: $RECOMMENDED"
  log "  ($REASON)"

  local -a backend_keys=() backend_labels=()
  local major
  if major="$(nvidia_driver_major)" && [[ "$major" =~ ^[0-9]+$ && "$major" -ge 580 ]]; then
    backend_keys+=("cuda13")
    backend_labels+=("cuda13  Local AI on NVIDIA GPU (R${major} driver detected)")
  fi
  if [[ -e /dev/kfd ]] || { have rocminfo && rocminfo >/dev/null 2>&1; }; then
    backend_keys+=("rocm")
    backend_labels+=("rocm    Local AI on AMD GPU (ROCm device detected)")
  fi
  backend_keys+=("vulkan")
  backend_labels+=("vulkan  Local AI on any GPU via Vulkan (NVIDIA/AMD/Intel)")
  backend_keys+=("cpu")
  backend_labels+=("cpu     Local AI, no GPU (slower, biggest download ~60 GB)")
  backend_keys+=("slim")
  backend_labels+=("slim    No local model runtime; use cloud AI providers (lightest, ~15 GB)")

  local n=${#backend_keys[@]}
  printf '\n  Choose a backend:\n'
  local i
  for i in $(seq 0 $((n-1))); do
    printf '    %d) %s\n' "$((i+1))" "${backend_labels[$i]}"
  done
  printf '\n'

  if [[ "$ASSUME_YES" == "1" ]]; then
    SELECTED_BACKEND="$RECOMMENDED"
    log "--yes: using recommended backend ($SELECTED_BACKEND)."
    return
  fi
  local choice
  read -r -p "Enter 1-${n}, or press Enter for recommended [$RECOMMENDED]: " choice || choice=""
  if [[ -z "$choice" ]]; then
    SELECTED_BACKEND="$RECOMMENDED"
  elif [[ "$choice" =~ ^[0-9]+$ && "$choice" -ge 1 && "$choice" -le "$n" ]]; then
    SELECTED_BACKEND="${backend_keys[$((choice-1))]}"
  else
    warn "Unrecognized choice '$choice'; using recommended."
    SELECTED_BACKEND="$RECOMMENDED"
  fi
}

# Vulkan is ONE image that reaches the GPU differently per host. docker-compose.vulkan.yml
# defaults to the Windows / Docker Desktop dzn (Vulkan-on-D3D12) path; on native Linux we
# export GA_VULKAN_* so the SAME file uses /dev/dri + in-image Mesa (AMD/Intel) or the
# nvidia container runtime + toolkit-injected ICD (NVIDIA). Windows needs no env — the
# compose defaults win. VK_DRIVER_FILES is always pinned to one ICD so llvmpipe (CPU) is
# never selected; a host with no usable GPU degrades to CPU (warned), never silently.
select_vulkan_runtime() {
  [[ "$SELECTED_BACKEND" == "vulkan" ]] || return 0

  # Windows / Docker Desktop: the dzn defaults already do the right thing for every vendor.
  if docker info --format '{{.OperatingSystem}}' 2>/dev/null | grep -q 'Docker Desktop'; then
    log "Vulkan: Docker Desktop → Mesa dzn over D3D12 (/dev/dxg). Using built-in defaults (no env)."
    return 0
  fi

  # --- Native Linux ----------------------------------------------------------
  # Pick a render-node device, falling back to /dev/null so the static devices:
  # entry never hard-fails a headless/GPU-less host (NVIDIA still works via the
  # toolkit, which injects its own /dev/nvidia* nodes).
  local dev="/dev/null"
  [[ -e /dev/dri ]] && dev="/dev/dri"
  export GA_VULKAN_DEVICE="$dev"
  export GA_VULKAN_DRIVER_LIBS="/usr/lib"                 # harmless existing dir (the /usr/lib/wsl bind is unused on Linux)
  export GA_VULKAN_LD_LIBRARY_PATH="/usr/lib/x86_64-linux-gnu"

  if docker info --format '{{json .Runtimes}}' 2>/dev/null | grep -q '"nvidia"'; then
    # NVIDIA: the nvidia-container-toolkit injects the Vulkan ICD when the nvidia
    # runtime is used AND NVIDIA_DRIVER_CAPABILITIES includes 'graphics' (set in compose).
    export GA_VULKAN_RUNTIME="nvidia"
    export GA_VULKAN_ICD="/usr/share/vulkan/icd.d/nvidia_icd.json"
    log "Vulkan: native Linux NVIDIA → nvidia runtime injects the Vulkan ICD (device $dev)."
    log "       (If the GPU isn't found, the injected ICD path may differ — override GA_VULKAN_ICD.)"
  elif [[ -e /dev/dri ]]; then
    # AMD/Intel via in-image Mesa (RADV/ANV). Pin the matching ICD so llvmpipe is excluded.
    local icd=""
    for v in /sys/class/drm/renderD*/device/vendor; do
      [[ -r "$v" ]] || continue
      case "$(cat "$v" 2>/dev/null)" in
        0x1002) icd="/usr/share/vulkan/icd.d/radeon_icd.x86_64.json"; break ;;  # AMD (RADV)
        0x8086) icd="/usr/share/vulkan/icd.d/intel_icd.x86_64.json";  break ;;  # Intel (ANV)
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

compose_file_for() {
  if [[ "$COMPOSE_MODE" == "local" ]]; then
    case "$1" in
      slim)   echo "docker-compose.slim.yml" ;;
      cuda13) echo "docker-compose.cuda.yml" ;;
      rocm)   echo "docker-compose.rocm.yml" ;;
      vulkan) echo "docker-compose.vulkan.yml" ;;
      *)      echo "docker-compose.cpu.yml" ;;
    esac
  else
    case "$1" in
      slim)   echo "docker-compose.ghcr-slim.yml" ;;
      cuda13) echo "docker-compose.ghcr-cuda13.yml" ;;
      rocm)   echo "docker-compose.ghcr-rocm.yml" ;;
      vulkan) echo "docker-compose.ghcr-vulkan.yml" ;;
      *)      echo "docker-compose.ghcr-cpu.yml" ;;
    esac
  fi
}

# =============================================================================
# 5. Automatic update check (read-only) + prompt
# =============================================================================
remote_digest() {
  local ref="$1" d
  if docker buildx version >/dev/null 2>&1; then
    d="$(docker buildx imagetools inspect "$ref" 2>/dev/null \
         | awk '/^Digest:/{print $2; exit}')"
    [[ -n "$d" ]] && { echo "$d"; return 0; }
  fi
  docker manifest inspect -v "$ref" 2>/dev/null \
    | grep -m1 -o '"digest": *"sha256:[a-f0-9]*"' | grep -o 'sha256:[a-f0-9]*'
}

local_digest() {
  local ref="$1" rd
  rd="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$ref" 2>/dev/null | head -n1)"
  [[ "$rd" == *@* ]] && echo "${rd##*@}"
}

# Sets UPDATE_DECISION = pull | pull_stale | skip
# When pull_stale, STALE_SERVICES contains the service names to update.
plan_pull() {
  local compose_path="$DOCKER_DIR/$1"
  local images missing=0 stale=0 img r l
  STALE_SERVICES=()
  if ! images="$(docker compose -f "$compose_path" --env-file "$ENV_FILE" config --images 2>/dev/null)"; then
    warn "Could not resolve image list; will let Compose pull what's missing."
    UPDATE_DECISION="skip"; return
  fi
  log "Checking for image updates (this reads the registry, downloads nothing)..."
  local -a stale_images=()
  while IFS= read -r img; do
    [[ -n "$img" ]] || continue
    l="$(local_digest "$img" || true)"
    if [[ -z "$l" ]]; then missing=$((missing+1)); continue; fi
    r="$(remote_digest "$img" || true)"
    if [[ -n "$r" && "$r" != "$l" ]]; then
      stale=$((stale+1))
      stale_images+=("$img")
    fi
  done <<< "$images"

  if [[ "$missing" -gt 0 ]]; then
    log "$missing image(s) not present locally — they will be downloaded on first start."
    UPDATE_DECISION="pull"; return
  fi
  if [[ "$stale" -gt 0 ]]; then
    hr
    log "Updates available for $stale image(s) ($SELECTED_BACKEND)."
    if ask_yes_no "Update now before starting? [Y/n]" "Y"; then
      UPDATE_DECISION="pull_stale"
      resolve_stale_services "$compose_path" "${stale_images[@]}"
    else
      log "Keeping current images."
      UPDATE_DECISION="skip"
    fi
  else
    log "All images are up to date."
    UPDATE_DECISION="skip"
  fi
}

resolve_stale_services() {
  local compose_path="$1"; shift
  local -a stale_imgs=("$@")
  STALE_SERVICES=()
  local mapping svc img svc_img
  mapping="$(docker compose -f "$compose_path" --env-file "$ENV_FILE" config --format json 2>/dev/null \
    | python3 -c "
import json,sys
for svc,conf in json.load(sys.stdin).get('services',{}).items():
    print(svc+'='+conf.get('image',''))
" 2>/dev/null || true)"
  [[ -n "$mapping" ]] || return 0
  while IFS='=' read -r svc svc_img; do
    for img in "${stale_imgs[@]}"; do
      if [[ "$svc_img" == "$img" ]]; then
        STALE_SERVICES+=("$svc")
        break
      fi
    done
  done <<< "$mapping"
}

detect_prior_install() {
  local existing
  existing="$(docker volume ls --filter name=guideants --format '{{.Name}}' 2>/dev/null | head -n1 || true)"
  if [[ -n "$existing" ]]; then
    log "Existing GuideAnts data detected (named volumes). Your projects, DB, and models will be reused."
  fi
}

# =============================================================================
# 5b. Post-startup host folder mount (--mount flag)
# =============================================================================
API_BASE="http://localhost:5107"

json_extract() {
  local json="$1" field="$2"
  local python_cmd=""
  if command -v python3 >/dev/null 2>&1; then python_cmd=python3
  elif command -v python >/dev/null 2>&1; then python_cmd=python
  else fail "Python is required for --mount."; fi
  "$python_cmd" -c "
import json,sys
data = json.loads(sys.argv[1])
val = data.get(sys.argv[2])
if val is not None: print(val)
" "$json" "$field"
}

json_extract_list() {
  local json="$1"
  local python_cmd=""
  if command -v python3 >/dev/null 2>&1; then python_cmd=python3
  elif command -v python >/dev/null 2>&1; then python_cmd=python
  else fail "Python is required for --mount."; fi
  "$python_cmd" -c "
import json,sys
items = json.loads(sys.argv[1])
if not isinstance(items, list) or len(items) == 0:
    sys.exit(1)
for item in items:
    print(item.get('id','') + '|' + item.get('title',''))
" "$json"
}

acquire_token() {
  # Best-effort scrub any stale on-disk token from earlier installs
  rm -f "$DOCKER_DIR/volumes/content-files/.cli-auth-token" 2>/dev/null || true

  # 1. Wait for the API to be ready, then create a CLI session
  local resp code body attempt=0
  while true; do
    resp="$(curl -sS -w "\n%{http_code}" --connect-timeout 3 --max-time 5 \
      -X POST "$API_BASE/api/cli/sessions" 2>/dev/null || true)"
    code="$(echo "$resp" | tail -n1)"
    body="$(echo "$resp" | sed '$d')"
    [[ "$code" == "200" ]] && break
    attempt=$((attempt + 1))
    if [[ $attempt -ge 15 ]]; then
      fail "Could not create CLI session (HTTP $code). Ensure the stack is running and you are logged in at $HEALTH_URL."
    fi
    sleep 2
  done

  local session_id device_secret
  session_id="$(json_extract "$body" "sessionId")"
  device_secret="$(json_extract "$body" "deviceSecret")"

  if [[ -z "$session_id" || -z "$device_secret" ]]; then
    fail "Malformed session response from server. Try again or check the stack logs."
  fi

  # 2. Open the browser approval page
  local approve_url="$API_BASE/cli/authorize?session=$session_id"
  open_browser "$approve_url"
  log "Authorize this request in your browser:"
  log "  $approve_url"
  log "Approve the command-line mount request in your browser, then return here..."

  # 3. Poll for the token (~5 min total, ~2 s between attempts)
  local max_attempts=150 attempt=0
  while [[ $attempt -lt $max_attempts ]]; do
    sleep 2
    attempt=$((attempt + 1))

    resp="$(curl -sS -w "\n%{http_code}" -H "X-Device-Secret: $device_secret" \
      "$API_BASE/api/cli/sessions/$session_id/token" 2>/dev/null || true)"
    code="$(echo "$resp" | tail -n1)"
    body="$(echo "$resp" | sed '$d')"

    case "$code" in
      200)
        AUTH_TOKEN="$(json_extract "$body" "token")"
        if [[ -z "$AUTH_TOKEN" ]]; then
          fail "Server returned 200 but the token was empty."
        fi
        log "Authorized."
        return 0
        ;;
      202)
        # Still pending; print a heartbeat dot every 5 attempts
        if (( attempt % 5 == 0 )); then printf "." >&2; fi
        ;;
      403)
        fail "Authorization request was denied in the browser. Rerun with --mount to try again."
        ;;
      410)
        fail "Authorization request expired or was already used. Rerun with --mount and approve promptly."
        ;;
      404)
        fail "Authorization session not found. Rerun and try again."
        ;;
      401)
        fail "Authorization failed (device secret rejected)."
        ;;
      *)
        # Transient error; keep polling until the overall timeout
        ;;
    esac
  done

  fail "Timed out waiting for browser approval. Rerun with --mount and approve in the browser."
}

apply_host_mount() {
  [[ -z "$MOUNT_PATH" ]] && return 0

  if [[ "$MOUNT_PATH" != /* && ! "$MOUNT_PATH" =~ ^[A-Za-z]:[\\/] ]]; then
    MOUNT_PATH="$(cd "$MOUNT_PATH" 2>/dev/null && pwd)" || fail "Directory not found: $MOUNT_PATH"
  fi
  if [[ ! -d "$MOUNT_PATH" ]]; then
    fail "Mount path does not exist or is not a directory: $MOUNT_PATH"
  fi

  acquire_token

  hr
  log "Fetching projects..."
  local projects_response projects_http_code projects_body
  projects_response="$(curl -sS -w "\n%{http_code}" -H "Authorization: Bearer $AUTH_TOKEN" "$API_BASE/api/projects" 2>/dev/null || true)"
  projects_http_code="$(echo "$projects_response" | tail -n1)"
  projects_body="$(echo "$projects_response" | sed '$d')"

  if [[ "$projects_http_code" != "200" ]]; then
    fail "Failed to fetch projects (HTTP $projects_http_code)."
  fi

  local -a project_ids=() project_titles=()
  while IFS='|' read -r pid ptitle; do
    [[ -n "$pid" ]] || continue
    project_ids+=("$pid")
    project_titles+=("$ptitle")
  done < <(json_extract_list "$projects_body")

  if [[ ${#project_ids[@]} -eq 0 ]]; then
    fail "No projects found. Create a project in GuideAnts first."
  fi

  printf '\n  Select a project to mount "%s" into:\n' "$MOUNT_PATH"
  local i
  for i in $(seq 0 $((${#project_ids[@]}-1))); do
    printf '    %d) %s\n' "$((i+1))" "${project_titles[$i]}"
  done
  printf '\n'

  local selected_project_id
  if [[ ${#project_ids[@]} -eq 1 ]]; then
    if [[ "$ASSUME_YES" == "1" ]]; then
      selected_project_id="${project_ids[0]}"
      log "Auto-selecting only project: ${project_titles[0]}"
    else
      local choice
      read -r -p "Enter 1 or press Enter for [${project_titles[0]}]: " choice || choice=""
      selected_project_id="${project_ids[0]}"
    fi
  else
    local choice
    read -r -p "Enter 1-${#project_ids[@]}: " choice || choice=""
    if [[ "$choice" =~ ^[0-9]+$ && "$choice" -ge 1 && "$choice" -le "${#project_ids[@]}" ]]; then
      selected_project_id="${project_ids[$((choice-1))]}"
    else
      fail "Invalid selection."
    fi
  fi

  hr
  log "Fetching notebooks..."
  local notebooks_response notebooks_http_code notebooks_body
  notebooks_response="$(curl -sS -w "\n%{http_code}" -H "Authorization: Bearer $AUTH_TOKEN" "$API_BASE/api/projects/$selected_project_id/notebooks" 2>/dev/null || true)"
  notebooks_http_code="$(echo "$notebooks_response" | tail -n1)"
  notebooks_body="$(echo "$notebooks_response" | sed '$d')"

  if [[ "$notebooks_http_code" != "200" ]]; then
    fail "Failed to fetch notebooks (HTTP $notebooks_http_code)."
  fi

  local -a notebook_ids=() notebook_titles=()
  while IFS='|' read -r nid ntitle; do
    [[ -n "$nid" ]] || continue
    notebook_ids+=("$nid")
    notebook_titles+=("$ntitle")
  done < <(json_extract_list "$notebooks_body")

  local selected_scope="Project"
  local selected_notebook_id=""

  if [[ ${#notebook_ids[@]} -eq 0 ]]; then
    log "No notebooks found in this project. Mounting at the project root (applies to every notebook)."
  else
    printf '\n  Select a notebook to mount "%s" into:\n' "$MOUNT_PATH"
    for i in $(seq 0 $((${#notebook_ids[@]}-1))); do
      printf '    %d) %s\n' "$((i+1))" "${notebook_titles[$i]}"
    done
    printf '    %d) Entire project (project root + every notebook)\n' "$((${#notebook_ids[@]}+1))"
    printf '\n'

    local all_choice=$((${#notebook_ids[@]}+1))
    if [[ "$ASSUME_YES" == "1" ]]; then
      log "--yes: mounting at the project root (applies to every notebook)."
    else
      local nb_choice
      read -r -p "Enter 1-${all_choice}: " nb_choice || nb_choice=""
      if [[ "$nb_choice" =~ ^[0-9]+$ && "$nb_choice" -ge 1 && "$nb_choice" -lt "$all_choice" ]]; then
        selected_scope="Notebook"
        selected_notebook_id="${notebook_ids[$((nb_choice-1))]}"
        log "Mounting to notebook: ${notebook_titles[$((nb_choice-1))]}"
      elif [[ "$nb_choice" =~ ^[0-9]+$ && "$nb_choice" -eq "$all_choice" ]]; then
        log "Mounting at the project root (applies to every notebook)."
      else
        fail "Invalid selection."
      fi
    fi
  fi

  log "Creating host mount..."
  local create_body create_response create_http_code create_result
  local json_safe_path="${MOUNT_PATH//\\/\\\\}"
  if [[ "$selected_scope" == "Notebook" ]]; then
    create_body="{\"scope\":\"Notebook\",\"notebookId\":\"$selected_notebook_id\",\"hostPath\":\"$json_safe_path\"}"
  else
    create_body="{\"scope\":\"Project\",\"hostPath\":\"$json_safe_path\"}"
  fi
  create_response="$(curl -sS -w "\n%{http_code}" \
    -X POST \
    -H "Authorization: Bearer $AUTH_TOKEN" \
    -H "Content-Type: application/json" \
    -d "$create_body" \
    "$API_BASE/api/projects/$selected_project_id/host-folder-mounts" 2>/dev/null || true)"
  create_http_code="$(echo "$create_response" | tail -n1)"
  create_result="$(echo "$create_response" | sed '$d')"

  if [[ "$create_http_code" != "201" ]]; then
    local err_msg
    err_msg="$(json_extract "$create_result" "message" 2>/dev/null || echo "$create_result")"
    fail "Failed to create mount (HTTP $create_http_code): $err_msg"
  fi

  local mount_id apply_command
  mount_id="$(json_extract "$create_result" "mountId")"
  apply_command="$(json_extract "$create_result" "applyCommand")"

  log "Mount created (id: $mount_id). Applying..."

  local mount_script_ps1="$ROOT_DIR/scripts/guideants-host-mount.ps1"
  local mount_script_sh="$ROOT_DIR/scripts/guideants-host-mount.sh"
  if [[ "$OS" == "windows" && "$IS_WSL" == "0" && -f "$mount_script_ps1" ]]; then
    powershell.exe -ExecutionPolicy Bypass -File "$mount_script_ps1" apply -MountId "$mount_id" -HostPath "$MOUNT_PATH" -ProjectId "$selected_project_id" || true
  elif [[ -f "$mount_script_sh" ]]; then
    bash "$mount_script_sh" apply --mount-id "$mount_id" --host-path "$MOUNT_PATH" --project-id "$selected_project_id" || true
  else
    warn "Host mount script not found. Run manually: $apply_command"
  fi
  fix_crlf_containers

  log "Host folder mounted successfully."
}

# =============================================================================
# 5c. Post-startup host folder unmount (--unmount flag)
# =============================================================================
json_extract_mounts() {
  local json="$1"
  local python_cmd=""
  if command -v python3 >/dev/null 2>&1; then python_cmd=python3
  elif command -v python >/dev/null 2>&1; then python_cmd=python
  else fail "Python is required for --unmount."; fi
  "$python_cmd" -c "
import json,sys
items = json.loads(sys.argv[1])
if not isinstance(items, list):
    sys.exit(1)
active = [i for i in items if i.get('status') == 'Active']
if len(active) == 0:
    sys.exit(1)
for item in active:
    print(item.get('mountId','') + '|' + item.get('displayName','') + '|' + str(item.get('scope','')))
" "$json"
}

remove_host_mount() {
  [[ "$UNMOUNT" == "1" ]] || return 0

  acquire_token

  hr
  log "Fetching projects..."
  local projects_response projects_http_code projects_body
  projects_response="$(curl -sS -w "\n%{http_code}" -H "Authorization: Bearer $AUTH_TOKEN" "$API_BASE/api/projects" 2>/dev/null || true)"
  projects_http_code="$(echo "$projects_response" | tail -n1)"
  projects_body="$(echo "$projects_response" | sed '$d')"

  if [[ "$projects_http_code" != "200" ]]; then
    fail "Failed to fetch projects (HTTP $projects_http_code)."
  fi

  local -a project_ids=() project_titles=()
  while IFS='|' read -r pid ptitle; do
    [[ -n "$pid" ]] || continue
    project_ids+=("$pid")
    project_titles+=("$ptitle")
  done < <(json_extract_list "$projects_body")

  if [[ ${#project_ids[@]} -eq 0 ]]; then
    fail "No projects found."
  fi

  printf '\n  Select a project to unmount from:\n'
  local i
  for i in $(seq 0 $((${#project_ids[@]}-1))); do
    printf '    %d) %s\n' "$((i+1))" "${project_titles[$i]}"
  done
  printf '\n'

  local selected_project_id
  if [[ ${#project_ids[@]} -eq 1 ]]; then
    if [[ "$ASSUME_YES" == "1" ]]; then
      selected_project_id="${project_ids[0]}"
      log "Auto-selecting only project: ${project_titles[0]}"
    else
      local choice
      read -r -p "Enter 1 or press Enter for [${project_titles[0]}]: " choice || choice=""
      selected_project_id="${project_ids[0]}"
    fi
  else
    local choice
    read -r -p "Enter 1-${#project_ids[@]}: " choice || choice=""
    if [[ "$choice" =~ ^[0-9]+$ && "$choice" -ge 1 && "$choice" -le "${#project_ids[@]}" ]]; then
      selected_project_id="${project_ids[$((choice-1))]}"
    else
      fail "Invalid selection."
    fi
  fi

  hr
  log "Fetching mounts..."
  local mounts_response mounts_http_code mounts_body
  mounts_response="$(curl -sS -w "\n%{http_code}" -H "Authorization: Bearer $AUTH_TOKEN" "$API_BASE/api/projects/$selected_project_id/host-folder-mounts" 2>/dev/null || true)"
  mounts_http_code="$(echo "$mounts_response" | tail -n1)"
  mounts_body="$(echo "$mounts_response" | sed '$d')"

  if [[ "$mounts_http_code" != "200" ]]; then
    fail "Failed to fetch mounts (HTTP $mounts_http_code)."
  fi

  local -a mount_ids=() mount_names=() mount_scopes=()
  while IFS='|' read -r mid mname mscope; do
    [[ -n "$mid" ]] || continue
    mount_ids+=("$mid")
    mount_names+=("$mname")
    mount_scopes+=("$mscope")
  done < <(json_extract_mounts "$mounts_body")

  if [[ ${#mount_ids[@]} -eq 0 ]]; then
    log "No active mounts found for this project."
    return 0
  fi

  printf '\n  Select a mount to remove:\n'
  for i in $(seq 0 $((${#mount_ids[@]}-1))); do
    printf '    %d) %s  [scope: %s]\n' "$((i+1))" "${mount_names[$i]}" "${mount_scopes[$i]}"
  done
  printf '\n'

  local selected_mount_id
  if [[ ${#mount_ids[@]} -eq 1 ]]; then
    if [[ "$ASSUME_YES" == "1" ]]; then
      selected_mount_id="${mount_ids[0]}"
      log "Auto-selecting only mount: ${mount_names[0]}"
    else
      local choice
      read -r -p "Enter 1 or press Enter for [${mount_names[0]}]: " choice || choice=""
      selected_mount_id="${mount_ids[0]}"
    fi
  else
    local choice
    read -r -p "Enter 1-${#mount_ids[@]}: " choice || choice=""
    if [[ "$choice" =~ ^[0-9]+$ && "$choice" -ge 1 && "$choice" -le "${#mount_ids[@]}" ]]; then
      selected_mount_id="${mount_ids[$((choice-1))]}"
    else
      fail "Invalid selection."
    fi
  fi

  log "Removing mount..."
  local remove_response remove_http_code remove_result
  remove_response="$(curl -sS -w "\n%{http_code}" \
    -X POST \
    -H "Authorization: Bearer $AUTH_TOKEN" \
    "$API_BASE/api/projects/$selected_project_id/host-folder-mounts/$selected_mount_id/commands/remove" 2>/dev/null || true)"
  remove_http_code="$(echo "$remove_response" | tail -n1)"
  remove_result="$(echo "$remove_response" | sed '$d')"

  if [[ "$remove_http_code" != "200" ]]; then
    local err_msg
    err_msg="$(json_extract "$remove_result" "message" 2>/dev/null || echo "$remove_result")"
    fail "Failed to remove mount (HTTP $remove_http_code): $err_msg"
  fi

  local mount_script_ps1="$ROOT_DIR/scripts/guideants-host-mount.ps1"
  local mount_script_sh="$ROOT_DIR/scripts/guideants-host-mount.sh"
  if [[ "$OS" == "windows" && "$IS_WSL" == "0" && -f "$mount_script_ps1" ]]; then
    powershell.exe -ExecutionPolicy Bypass -File "$mount_script_ps1" remove -MountId "$selected_mount_id" -ProjectId "$selected_project_id" || true
  elif [[ -f "$mount_script_sh" ]]; then
    bash "$mount_script_sh" remove --mount-id "$selected_mount_id" --project-id "$selected_project_id" || true
  else
    warn "Host mount script not found. Run manually: guideants-host-mount.sh remove --mount-id $selected_mount_id"
  fi
  fix_crlf_containers

  log "Host folder mount removed successfully."
}

# =============================================================================
# 6. Compose up, health, browser
# =============================================================================
wait_for_health() {
  log "Waiting for GuideAnts at $HEALTH_URL ..."
  local i
  for i in $(seq 1 45); do
    if curl -fsS --connect-timeout 3 --max-time 5 "$HEALTH_URL" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

open_browser() {
  local url="${1:-$HEALTH_URL}"
  case "$OS" in
    macos)   open "$url" >/dev/null 2>&1 || true ;;
    windows)
      if [[ "$IS_WSL" == "1" ]]; then
        (have wslview && wslview "$url") >/dev/null 2>&1 \
          || (have cmd.exe && cmd.exe /c start "" "$url") >/dev/null 2>&1 || true
      else
        start "$url" 2>/dev/null || true
      fi
      ;;
    *)       have xdg-open && xdg-open "$url" >/dev/null 2>&1 || true ;;
  esac
}

save_state() {
  cat > "$STATE_FILE" <<EOF
BACKEND=${SELECTED_BACKEND:-}
COMPOSE_MODE=${COMPOSE_MODE}
COMPOSE_FILE=${COMPOSE_FILE}
HOST_MOUNT_OVERRIDE_FILE=${HOST_MOUNT_OVERRIDE_FILE}
DOCKER_DIRECTORY=${DOCKER_DIRECTORY}
START_COMMAND=guideants.sh
LAST_RUN_EPOCH=$(date +%s)
EOF
}

# =============================================================================
# main
# =============================================================================
hr
log "GuideAnts portable launcher"
WSL_NOTE=""; [[ "$IS_WSL" == "1" ]] && WSL_NOTE=" (WSL)"
log "OS: ${OS}${WSL_NOTE}   Arch: $ARCH"
[[ "$OS" == "macos" && "$ARCH" == "arm64" ]] && \
  warn "Apple Silicon: images run as linux/amd64 under emulation; 'slim' is recommended here."
hr

check_docker
report_resources
choose_backend
COMPOSE_FILE="$(compose_file_for "$SELECTED_BACKEND")"
log "Selected backend: $SELECTED_BACKEND  ->  docker/$COMPOSE_FILE"

select_vulkan_runtime

check_gpu_drivers "$SELECTED_BACKEND"

detect_prior_install
if [[ "$COMPOSE_MODE" == "ghcr" ]]; then
  plan_pull "$COMPOSE_FILE"
else
  log "Local compose mode — skipping registry update check."
  UPDATE_DECISION="skip"
fi

if [[ "$MODE" == "doctor" ]]; then
  hr
  log "Doctor mode complete. No changes were made."
  would_start="docker compose -f docker/$COMPOSE_FILE"
  [[ -f "$DOCKER_DIR/$HOST_MOUNT_OVERRIDE_FILE" ]] && would_start+=" -f docker/$HOST_MOUNT_OVERRIDE_FILE"
  log "Would start: $would_start up -d"
  log "Update decision: ${UPDATE_DECISION:-skip}"
  exit 0
fi

[[ "$OS" == "macos" && "$ARCH" == "arm64" && "$COMPOSE_MODE" != "local" ]] && export DOCKER_DEFAULT_PLATFORM=linux/amd64

# Ensure shell scripts have LF endings for Linux containers.
find "$DOCKER_DIR/build" -name '*.sh' -exec sed -i 's/\r$//' {} + 2>/dev/null || true

cd "$DOCKER_DIR"
compose_args=(-f "$COMPOSE_FILE")
if [[ -f "$HOST_MOUNT_OVERRIDE_FILE" ]]; then
  if docker compose -f "$COMPOSE_FILE" -f "$HOST_MOUNT_OVERRIDE_FILE" --env-file "$ENV_FILE" config >/dev/null 2>&1; then
    compose_args+=(-f "$HOST_MOUNT_OVERRIDE_FILE")
    log "Including host mount override: $HOST_MOUNT_OVERRIDE_FILE"
  else
    warn "Ignoring invalid host mount override docker/$HOST_MOUNT_OVERRIDE_FILE. Recreate mounts to regenerate it."
  fi
fi
if [[ "${UPDATE_DECISION:-skip}" == "pull" ]]; then
  log "Pulling images..."
  docker compose "${compose_args[@]}" --env-file "$ENV_FILE" pull
elif [[ "${UPDATE_DECISION:-skip}" == "pull_stale" && ${#STALE_SERVICES[@]} -gt 0 ]]; then
  log "Pulling updates for: ${STALE_SERVICES[*]}"
  docker compose "${compose_args[@]}" --env-file "$ENV_FILE" pull --policy always "${STALE_SERVICES[@]}"
fi
log "Starting the stack..."
docker compose "${compose_args[@]}" --env-file "$ENV_FILE" up -d
cd "$ROOT_DIR"

# Patch containers whose entrypoint scripts have Windows line endings (exit 127).
fix_crlf_containers() {
  sleep 3
  local container exit_code entrypoint src_script
  for container in $(docker ps -a --filter "label=com.docker.compose.project" --format '{{.Names}}' 2>/dev/null); do
    exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container" 2>/dev/null || echo 0)"
    [[ "$exit_code" == "127" ]] || continue
    entrypoint="$(docker inspect --format '{{join .Config.Entrypoint " "}}' "$container" 2>/dev/null || true)"
    # Extract the .sh path from the entrypoint (last argument)
    src_script="${entrypoint##* }"
    [[ "$src_script" == *.sh ]] || continue
    # Copy the script out, strip \r, copy back in
    if docker cp "$container:$src_script" "/tmp/_fix_crlf.sh" 2>/dev/null; then
      sed -i 's/\r$//' "/tmp/_fix_crlf.sh"
      docker cp "/tmp/_fix_crlf.sh" "$container:$src_script" >/dev/null 2>&1
      rm -f "/tmp/_fix_crlf.sh"
      docker restart "$container" >/dev/null 2>&1
    fi
  done
}
fix_crlf_containers

if wait_for_health; then
  hr
  log "GuideAnts is up: $HEALTH_URL"
  apply_host_mount
  remove_host_mount
  open_browser
else
  warn "Health check timed out. Inspect with: docker compose -f docker/$COMPOSE_FILE ps"
  if [[ -n "$MOUNT_PATH" || "$UNMOUNT" == "1" ]]; then
    warn "Skipping mount/unmount operations because the health check failed."
  fi
fi

save_state
