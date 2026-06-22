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
#   4. Walks you through which backend to use (cpu / cuda13 / rocm / slim).
#   5. Checks the registry for newer images and asks before updating.
#   6. Starts the stack, waits for health, opens your browser.
#
# Flags:
#   --doctor                 Run checks only; change nothing.
#   --backend <cpu|cuda13|rocm|slim>   Skip the backend prompt.
#   --mount <path>           Mount a host folder into a project (requires prior login).
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
BACKEND_OVERRIDE=""       # cpu | cuda13 | rocm | slim
ASSUME_YES="0"            # 0 | 1
RECONFIGURE="0"           # 0 | 1
MOUNT_PATH=""             # host folder to bind-mount

# --- logging helpers ---------------------------------------------------------
log()  { printf '[guideants] %s\n' "$*"; }
warn() { printf '[guideants][warn] %s\n' "$*" >&2; }
fail() { printf '[guideants][error] %s\n' "$*" >&2; exit 1; }
hr()   { printf '%s\n' "----------------------------------------------------------------"; }

usage() {
  sed -n '3,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
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
    --mount)
      [[ $# -ge 2 ]] || fail "Missing value for --mount"
      MOUNT_PATH="$2"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) fail "Unknown option: $1 (try --help)" ;;
  esac
  shift
done

[[ -z "$BACKEND_OVERRIDE" || "$BACKEND_OVERRIDE" =~ ^(cpu|cuda13|rocm|slim)$ ]] \
  || fail "--backend must be cpu, cuda13, rocm, or slim"

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

MIN_NVIDIA_DRIVER="580.0"
MIN_CUDA_VERSION="13.0"
MIN_ROCM_VERSION="6.0.0"

check_gpu_drivers() {
  local backend="$1"
  if [[ "$backend" == "cuda13" ]]; then
    hr
    log "Checking NVIDIA / CUDA driver versions..."
    local drv cuda_ver
    if ! drv="$(nvidia_driver_full)"; then
      warn "nvidia-smi not found or not working. CUDA backend requires NVIDIA drivers."
      warn "Install the latest NVIDIA drivers: https://www.nvidia.com/Download/index.aspx"
      return
    fi
    log "NVIDIA driver version: $drv"
    if ! version_gte "$drv" "$MIN_NVIDIA_DRIVER"; then
      warn "NVIDIA driver $drv is below the minimum ($MIN_NVIDIA_DRIVER) for CUDA 13."
      warn "Update your drivers: https://www.nvidia.com/Download/index.aspx"
      case "$OS" in
        linux)
          warn "  Ubuntu/Debian: sudo apt update && sudo apt install --upgrade nvidia-driver-580"
          warn "  RHEL/Fedora:   sudo dnf upgrade nvidia-driver"
          ;;
        windows)
          warn "  Download the latest driver from the NVIDIA website, or use GeForce Experience to update."
          ;;
      esac
      if ! ask_yes_no "Continue anyway with outdated NVIDIA driver? [y/N]" "N"; then
        fail "Aborting. Update your NVIDIA drivers and rerun."
      fi
    else
      log "NVIDIA driver $drv meets minimum requirement (>= $MIN_NVIDIA_DRIVER)."
    fi
    if cuda_ver="$(nvidia_cuda_version)"; then
      log "CUDA version reported by driver: $cuda_ver"
      if ! version_gte "$cuda_ver" "$MIN_CUDA_VERSION"; then
        warn "CUDA $cuda_ver is below the minimum ($MIN_CUDA_VERSION)."
        warn "Update your NVIDIA drivers to get CUDA $MIN_CUDA_VERSION+ support."
        if ! ask_yes_no "Continue anyway with older CUDA version? [y/N]" "N"; then
          fail "Aborting. Update your NVIDIA drivers and rerun."
        fi
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
    if [[ "$saved_backend" =~ ^(cpu|cuda13|rocm|slim)$ ]]; then
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

compose_file_for() {
  case "$1" in
    slim)   echo "docker-compose.ghcr-slim.yml" ;;
    cuda13) echo "docker-compose.ghcr-cuda13.yml" ;;
    rocm)   echo "docker-compose.ghcr-rocm.yml" ;;
    *)      echo "docker-compose.ghcr-cpu.yml" ;;
  esac
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
CLI_TOKEN_FILE="$DOCKER_DIR/volumes/content-files/.cli-auth-token"

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
  # Try saved token first
  if [[ -f "$CLI_TOKEN_FILE" ]]; then
    local saved_token
    saved_token="$(cat "$CLI_TOKEN_FILE")"
    if [[ -n "$saved_token" ]]; then
      local check_code
      check_code="$(curl -sS -o /dev/null -w "%{http_code}" \
        -H "Authorization: Bearer $saved_token" "$API_BASE/api/auth/me" 2>/dev/null || echo "000")"
      if [[ "$check_code" == "200" ]]; then
        log "Using saved login."
        AUTH_TOKEN="$saved_token"
        return 0
      fi
    fi
  fi

  # Prompt for credentials
  hr
  log "Login required to mount a host folder."
  local email password
  read -r -p "Email: " email || fail "Login cancelled."
  [[ -n "$email" ]] || fail "Email is required."
  read -r -s -p "Password: " password || fail "Login cancelled."
  printf '\n'
  [[ -n "$password" ]] || fail "Password is required."

  local tmp_cookie tmp_body login_http_code
  tmp_cookie="$(mktemp)"
  tmp_body="$(mktemp)"

  local python_cmd=""
  if command -v python3 >/dev/null 2>&1; then python_cmd=python3
  elif command -v python >/dev/null 2>&1; then python_cmd=python
  else fail "Python is required for --mount."; fi

  local login_json
  login_json="$("$python_cmd" -c "import json,sys; print(json.dumps({'email':sys.argv[1],'password':sys.argv[2]}))" "$email" "$password")"

  login_http_code="$(curl -sS -o "$tmp_body" -w "%{http_code}" \
    -c "$tmp_cookie" \
    -X POST \
    -H "Content-Type: application/json" \
    -d "$login_json" \
    "$API_BASE/api/auth/login" 2>/dev/null || echo "000")"

  if [[ "$login_http_code" != "200" ]]; then
    local err_msg
    err_msg="$(json_extract "$(cat "$tmp_body")" "message" 2>/dev/null || echo "Invalid credentials.")"
    rm -f "$tmp_cookie" "$tmp_body"
    fail "Login failed (HTTP $login_http_code): $err_msg"
  fi

  local cookie_token
  cookie_token="$(awk '/GuideAnts.Auth/{print $NF}' "$tmp_cookie" 2>/dev/null || true)"
  rm -f "$tmp_cookie" "$tmp_body"

  if [[ -z "$cookie_token" ]]; then
    fail "Login succeeded but could not extract auth token."
  fi

  AUTH_TOKEN="$cookie_token"

  # Save for future runs
  mkdir -p "$(dirname "$CLI_TOKEN_FILE")"
  printf '%s' "$AUTH_TOKEN" > "$CLI_TOKEN_FILE"
  log "Login successful. Token saved for future runs."
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

  log "Creating host mount..."
  local create_body create_response create_http_code create_result
  create_body="{\"scope\":\"Project\",\"hostPath\":\"$MOUNT_PATH\"}"
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

  local mount_script="$ROOT_DIR/scripts/guideants-host-mount.sh"
  if [[ -f "$mount_script" ]]; then
    bash "$mount_script" apply --mount-id "$mount_id" --host-path "$MOUNT_PATH" --project-id "$selected_project_id"
  else
    warn "Host mount script not found. Run manually: $apply_command"
  fi

  log "Host folder mounted successfully."
}

# =============================================================================
# 6. Compose up, health, browser
# =============================================================================
wait_for_health() {
  log "Waiting for GuideAnts at $HEALTH_URL ..."
  local i
  for i in $(seq 1 120); do
    if curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

open_browser() {
  case "$OS" in
    macos)   open "$HEALTH_URL" >/dev/null 2>&1 || true ;;
    windows) (have cmd.exe && cmd.exe /c start "" "$HEALTH_URL") >/dev/null 2>&1 \
               || (have explorer.exe && explorer.exe "$HEALTH_URL") >/dev/null 2>&1 || true ;;
    *)       have xdg-open && xdg-open "$HEALTH_URL" >/dev/null 2>&1 || true ;;
  esac
}

save_state() {
  cat > "$STATE_FILE" <<EOF
BACKEND=${SELECTED_BACKEND:-}
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

check_gpu_drivers "$SELECTED_BACKEND"

detect_prior_install
plan_pull "$COMPOSE_FILE"

if [[ "$MODE" == "doctor" ]]; then
  hr
  log "Doctor mode complete. No changes were made."
  if [[ -f "$DOCKER_DIR/$HOST_MOUNT_OVERRIDE_FILE" ]]; then
    log "Would start: docker compose -f docker/$COMPOSE_FILE -f docker/$HOST_MOUNT_OVERRIDE_FILE up -d"
  else
    log "Would start: docker compose -f docker/$COMPOSE_FILE up -d"
  fi
  log "Update decision: ${UPDATE_DECISION:-skip}"
  exit 0
fi

[[ "$OS" == "macos" && "$ARCH" == "arm64" ]] && export DOCKER_DEFAULT_PLATFORM=linux/amd64

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

if wait_for_health; then
  hr
  log "GuideAnts is up: $HEALTH_URL"
  apply_host_mount
  open_browser
else
  warn "Health check timed out. Inspect with: docker compose -f docker/$COMPOSE_FILE ps"
  if [[ -n "$MOUNT_PATH" ]]; then
    warn "Skipping --mount because the health check failed."
  fi
fi

save_state
