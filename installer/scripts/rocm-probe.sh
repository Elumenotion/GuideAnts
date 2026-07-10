#!/usr/bin/env bash
# Shared ROCm / WSL probe helpers for GuideAnts installer bash scripts.
# Source from guideants.sh, rocm-runtime-compose.sh, or stop_guideants.sh.

wsl_user_distros() {
  if ! have wsl.exe; then return; fi
  local line name
  while IFS= read -r line; do
    name="$(printf '%s' "$line" | tr -d '\0' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    [[ -z "$name" ]] && continue
    [[ "$name" =~ ^(docker-desktop|docker-desktop-data)$ ]] && continue
    printf '%s\n' "$name"
  done < <(wsl.exe -l -q 2>/dev/null || true)
}

wsl_user_probe() {
  local probe="$1" distro line
  for distro in $(wsl_user_distros); do
    line="$(wsl.exe -d "$distro" sh -lc "$probe" 2>/dev/null | head -n1 | tr -d '\r' || true)"
    if [[ -n "$line" ]]; then
      printf '%s' "$line"
      return 0
    fi
  done
  return 1
}

preferred_wsl_distro() {
  local d
  for d in $(wsl_user_distros); do
    [[ "$d" =~ [Uu]buntu-24.04 ]] && { printf '%s' "$d"; return 0; }
  done
  for d in $(wsl_user_distros); do
    [[ "$d" =~ [Uu]buntu ]] && { printf '%s' "$d"; return 0; }
  done
  wsl_user_distros | head -n1
}

windows_path_to_wsl() {
  local p="$1" drive rest
  case "$p" in
    /[A-Za-z]/*)
      drive="$(printf '%s' "${p:1:1}" | tr '[:upper:]' '[:lower:]')"
      rest="${p:2}"
      printf '/mnt/%s%s' "$drive" "$rest"
      ;;
    [A-Za-z]:*)
      drive="$(printf '%s' "${p:0:1}" | tr '[:upper:]' '[:lower:]')"
      rest="${p:2}"
      rest="${rest//\\//}"
      printf '/mnt/%s/%s' "$drive" "$rest"
      ;;
    *) printf '%s' "$p" ;;
  esac
}

rocm_install_command_hint() {
  local root_dir="${1:-.}" distro script_path
  distro="$(preferred_wsl_distro)"
  [[ -z "$distro" ]] && distro="Ubuntu-24.04"
  script_path="$(windows_path_to_wsl "$root_dir/scripts/install-rocm-wsl.sh")"
  printf 'wsl -d %s -u root bash %s' "$distro" "$script_path"
}

check_wsl2_status() {
  [[ "${OS:-}" != "windows" || "${IS_WSL:-0}" == "1" ]] && return 0
  have wsl.exe || fail "WSL is not installed. Install WSL2: https://learn.microsoft.com/windows/wsl/install"

  local status
  status="$(wsl.exe --status 2>&1)" || fail "Could not read WSL status. Install WSL2: https://learn.microsoft.com/windows/wsl/install"

  if ! grep -qiE 'Default Version:[[:space:]]*2(\.|$|[[:space:]])' <<<"$status"; then
    fail "WSL2 is required on Windows. Run: wsl --set-default-version 2, then reinstall or upgrade your Linux distro (e.g. wsl --install -d Ubuntu-24.04). Enable WSL integration in Docker Desktop: Settings -> Resources -> WSL integration."
  fi
}

amd_gpu_detected() {
  [[ -e /dev/kfd ]] && return 0
  if have rocminfo && rocminfo >/dev/null 2>&1; then return 0; fi

  if [[ "${OS:-}" == "windows" && "${IS_WSL:-0}" == "0" ]] && have wsl.exe; then
    local distro
    for distro in $(wsl_user_distros); do
      wsl.exe -d "$distro" sh -lc 'test -e /dev/dxg || test -e /dev/kfd' >/dev/null 2>&1 && return 0
      wsl.exe -d "$distro" sh -lc 'command -v rocminfo >/dev/null 2>&1 && HSA_ENABLE_DXG_DETECTION=1 rocminfo >/dev/null 2>&1' >/dev/null 2>&1 && return 0
    done
  fi
  return 1
}

rocm_version() {
  local v
  if [[ -f /opt/rocm/.info/version ]]; then
    v="$(head -n1 /opt/rocm/.info/version 2>/dev/null | tr -d ' ')"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
  fi
  if have rocminfo; then
    v="$(rocminfo 2>/dev/null | grep -oP 'ROCm Runtime Version:\s*\K[0-9]+\.[0-9]+(\.[0-9]+)?' | head -n1 || true)"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
  fi
  if have apt && dpkg -l rocm-core 2>/dev/null | grep -q '^ii'; then
    v="$(dpkg -l rocm-core 2>/dev/null | awk '/^ii/{print $3}' | head -n1)"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
  fi
  if [[ "${OS:-}" == "windows" && "${IS_WSL:-0}" == "0" ]] && have wsl.exe; then
    v="$(wsl_user_probe 'if [ -f /opt/rocm/.info/version ]; then head -n1 /opt/rocm/.info/version; elif command -v dpkg >/dev/null 2>&1; then dpkg -l rocm-core 2>/dev/null | awk '"'"'/^ii/{print $3; exit}'"'"'; fi' || true)"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
  fi
  return 1
}

install_rocm_wsl_from_host() {
  local root_dir="${1:-.}" distro script_path
  distro="$(preferred_wsl_distro)"
  [[ -n "$distro" ]] || fail "No user WSL distro found. Install one: wsl --install -d Ubuntu-24.04"
  script_path="$(windows_path_to_wsl "$root_dir/scripts/install-rocm-wsl.sh")"
  log "Installing ROCm in WSL distro '$distro'..."
  local -a wsl_args=(-d "$distro" -u root bash "$script_path")
  [[ "${ASSUME_YES:-0}" == "1" ]] && wsl_args+=(--yes)
  wsl.exe "${wsl_args[@]}"
}
