#!/usr/bin/env bash
# Rewrites docker/docker-compose.host-mounts.generated.yml and restarts affected services.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
STATE_FILE="$ROOT_DIR/.installer_state.env"
DEFAULT_API_BASE="http://localhost:5107"
API_PLAN_PATH="/api/internal/host-folder-mounts"
AFFECTED_MOUNT_SERVICE_NAMES=(guideants-webapi-ui guideants-ai plantuml)

usage() {
  cat <<'EOF'
Usage:
  guideants-host-mount.sh apply --mount-id <id> [--host-path <path>] [--project-id <id>]
  guideants-host-mount.sh remove --mount-id <id> [--project-id <id>]

Reads .installer_state.env for compose context. Apply requires a mount plan from the
GuideAnts API (D3 api-plan) or an explicit --host-path on the command line when the
API compose-plan endpoint is not yet available.
EOF
}

log() { printf '[guideants-host-mount] %s\n' "$*"; }
warn() { printf '[guideants-host-mount][warn] %s\n' "$*" >&2; }
fail() { printf '[guideants-host-mount][error] %s\n' "$*" >&2; exit 1; }

trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

load_installer_state() {
  COMPOSE_FILE=""
  HOST_MOUNT_OVERRIDE_FILE="docker-compose.host-mounts.generated.yml"
  DOCKER_DIRECTORY="docker"

  if [[ ! -f "$STATE_FILE" ]]; then
    fail "Missing $STATE_FILE. Run a start_* launcher first."
  fi

  while IFS= read -r line || [[ -n "$line" ]]; do
    line="$(trim "$line")"
    [[ -z "$line" || "$line" == \#* ]] && continue
    if [[ "$line" != *=* ]]; then
      continue
    fi
    key="${line%%=*}"
    value="${line#*=}"
    value="$(trim "$value")"
    case "$key" in
      COMPOSE_FILE) COMPOSE_FILE="$value" ;;
      HOST_MOUNT_OVERRIDE_FILE) HOST_MOUNT_OVERRIDE_FILE="$value" ;;
      DOCKER_DIRECTORY) DOCKER_DIRECTORY="$value" ;;
    esac
  done <"$STATE_FILE"

  if [[ -z "$COMPOSE_FILE" ]]; then
    fail "COMPOSE_FILE is missing from $STATE_FILE. Run a start_* launcher first."
  fi
}

sanitize_mount_key() {
  local mount_id="$1"
  if [[ "$mount_id" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
    printf '%s' "${mount_id//-/}" | tr '[:upper:]' '[:lower:]'
    return
  fi

  local key
  key="$(printf '%s' "$mount_id" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9-]+/-/g; s/^-+//; s/-+$//')"
  if [[ -z "$key" ]]; then
    fail "Mount id '$mount_id' does not produce a filesystem-safe mount key."
  fi
  printf '%s' "$key"
}

is_wsl() {
  command -v wslpath >/dev/null 2>&1 && return 0
  [[ -n "${WSL_DISTRO_NAME:-}" ]] && return 0
  [[ -r /proc/version ]] && grep -qiE 'microsoft|wsl' /proc/version && return 0
  return 1
}

# Produces a bind source the Docker daemon this script talks to can actually use.
# This script runs in bash (WSL or native Linux), so a Windows drive path must
# become a Linux path. Under WSL we use wslpath (e.g. D:\repos\GuideAnts ->
# /mnt/d/repos/GuideAnts); on a non-WSL Linux host a drive path is unbindable.
normalize_host_path_for_compose() {
  local path="$1"

  # Already a POSIX absolute path (native Linux or pre-converted WSL path).
  if [[ "$path" == /* ]]; then
    printf '%s' "$path"
    return
  fi

  # Windows drive path, e.g. D:\repos\GuideAnts or D:/repos/GuideAnts.
  if [[ "$path" =~ ^[A-Za-z]:[\\/] ]]; then
    if command -v wslpath >/dev/null 2>&1; then
      local converted
      converted="$(wslpath -u "$path")"
      printf '%s' "$converted"
      return
    fi
    if is_wsl; then
      local drive rest
      drive="$(printf '%s' "${path:0:1}" | tr '[:upper:]' '[:lower:]')"
      rest="${path:2}"
      rest="${rest//\\//}"
      printf '/mnt/%s/%s' "$drive" "${rest#/}"
      return
    fi
    fail "Host path '$path' is a Windows drive path, but this Docker host is not WSL/Docker Desktop. Provide a native Linux path (for example /srv/data) the daemon can bind."
  fi

  printf '%s' "${path//\\//}"
}

api_base_url() {
  printf '%s' "${GUIDEANTS_API_BASE:-$DEFAULT_API_BASE}"
}

json_field() {
  local file="$1"
  local field="$2"
  local python_cmd=""
  if command -v python3 >/dev/null 2>&1; then
    python_cmd=python3
  elif command -v python >/dev/null 2>&1; then
    python_cmd=python
  else
    fail "Python is required to parse the API compose plan response."
  fi

  "$python_cmd" - "$file" "$field" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as handle:
    data = json.load(handle)
value = data.get(sys.argv[2])
if value is None or value == "":
    raise SystemExit(1)
print(value)
PY
}

fetch_compose_plan_from_api() {
  local mount_id="$1"
  local api_base url tmp http_code
  api_base="$(api_base_url)"
  url="${api_base%/}${API_PLAN_PATH}/${mount_id}/compose-override-plan"
  tmp="$(mktemp)"

  local curl_args=(-sS -o "$tmp" -w "%{http_code}")
  if [[ -n "${GUIDEANTS_MOUNT_API_TOKEN:-}" ]]; then
    curl_args+=(-H "Authorization: Bearer ${GUIDEANTS_MOUNT_API_TOKEN}")
  fi

  http_code="$(curl "${curl_args[@]}" "$url" 2>/dev/null || printf '000')"
  if [[ "$http_code" != "200" ]]; then
    rm -f "$tmp"
    return 1
  fi

  PLAN_MOUNT_ID="$(json_field "$tmp" mountId)" || { rm -f "$tmp"; return 1; }
  PLAN_MOUNT_KEY="$(json_field "$tmp" mountKey)" || { rm -f "$tmp"; return 1; }
  PLAN_SOURCE_KIND="$(json_field "$tmp" sourceKind)" || { rm -f "$tmp"; return 1; }
  PLAN_HOST_PATH="$(json_field "$tmp" hostPath)" || { rm -f "$tmp"; return 1; }
  PLAN_PROJECT_ID=""
  PLAN_PROJECT_ID="$(json_field "$tmp" projectId 2>/dev/null || true)"
  rm -f "$tmp"
  return 0
}

resolve_apply_plan() {
  local mount_id="$1"
  local host_path="$2"

  if fetch_compose_plan_from_api "$mount_id"; then
    local api_source
    api_source="$(normalize_host_path_for_compose "$PLAN_HOST_PATH")"
    if [[ -z "${api_source//[[:space:]]/}" ]]; then
      fail "Compose plan host path is empty."
    fi
    if [[ -n "$host_path" ]]; then
      local cli_source
      cli_source="$(normalize_host_path_for_compose "$host_path")"
      if [[ "$cli_source" != "$api_source" ]]; then
        fail "Host path on the command line does not match the API mount plan."
      fi
    fi
    PLAN_HOST_PATH="$api_source"
    return 0
  fi

  if [[ -z "$host_path" ]]; then
    fail "Could not fetch compose plan from the GuideAnts API and --host-path was not provided."
  fi

  PLAN_MOUNT_ID="$mount_id"
  PLAN_MOUNT_KEY="$(sanitize_mount_key "$mount_id")"
  PLAN_SOURCE_KIND="LocalPath"
  PLAN_HOST_PATH="$(normalize_host_path_for_compose "$host_path")"
  PLAN_PROJECT_ID="${PROJECT_ID:-}"
}

declare -A MOUNT_ORDER=()
MOUNT_KEYS=()

parse_existing_override() {
  local override_path="$1"
  MOUNT_KEYS=()
  unset MOUNT_ORDER
  declare -gA MOUNT_ORDER=()

  [[ -f "$override_path" ]] || return 0

  local current_mount_id="" current_mount_key="" current_source_kind="" current_host_path=""
  local in_bind_block=0

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" =~ guideants-host-mount:[[:space:]]mount-id=([^[:space:]]+)[[:space:]]mount-key=([^[:space:]]+)[[:space:]]source-kind=([^[:space:]]+) ]]; then
      current_mount_id="${BASH_REMATCH[1]}"
      current_mount_key="${BASH_REMATCH[2]}"
      current_source_kind="${BASH_REMATCH[3]}"
      current_host_path=""
      in_bind_block=0
      continue
    fi

    if [[ "$current_mount_key" != "" && "$line" =~ ^[[:space:]]*source:[[:space:]]*(.+)$ ]]; then
      current_host_path="${BASH_REMATCH[1]}"
      in_bind_block=1
      continue
    fi

    if [[ "$in_bind_block" -eq 1 && "$line" =~ ^[[:space:]]*target:[[:space:]]*/app/HostMounts/ ]]; then
      MOUNT_ORDER["$current_mount_key"]="${current_mount_id}|${current_mount_key}|${current_source_kind}|${current_host_path}"
      if [[ " ${MOUNT_KEYS[*]:-} " != *" $current_mount_key "* ]]; then
        MOUNT_KEYS+=("$current_mount_key")
      fi
      current_mount_id=""
      current_mount_key=""
      current_source_kind=""
      current_host_path=""
      in_bind_block=0
    fi
  done <"$override_path"
}

sort_mount_keys() {
  if [[ ${#MOUNT_KEYS[@]} -eq 0 ]]; then
    return 0
  fi
  IFS=$'\n' MOUNT_KEYS=($(printf '%s\n' "${MOUNT_KEYS[@]}" | sort))
  unset IFS
}

write_local_bind_block() {
  local mount_id="$1"
  local mount_key="$2"
  local host_path="$3"
  if [[ -z "${host_path//[[:space:]]/}" ]]; then
    fail "Mount '$mount_id' has an empty host path; refusing to write an invalid compose override."
  fi
  printf '      # guideants-host-mount: mount-id=%s mount-key=%s source-kind=LocalPath\n' "$mount_id" "$mount_key"
  printf '      - type: bind\n'
  printf '        source: %s\n' "$host_path"
  printf '        target: /app/HostMounts/%s\n' "$mount_key"
  printf '        read_only: false\n'
}

write_override_file() {
  local override_path="$1"
  sort_mount_keys

  if [[ ${#MOUNT_KEYS[@]} -eq 0 ]]; then
    rm -f "$override_path"
    return 0
  fi

  local tmp
  tmp="$(mktemp)"
  {
    printf '%s\n' "# Generated by scripts/guideants-host-mount.* — do not edit manually."
    printf '%s\n' "# Host folder mount Compose override. Managed idempotently by guideants-host-mount scripts."
    printf '\n'
    printf '%s\n' "services:"

    local service
    for service in "${AFFECTED_MOUNT_SERVICE_NAMES[@]}"; do
      printf '  %s:\n' "$service"
      printf '    volumes:\n'
      local key
      for key in "${MOUNT_KEYS[@]}"; do
        IFS='|' read -r mount_id mount_key source_kind host_path <<<"${MOUNT_ORDER[$key]}"
        if [[ "$source_kind" == "LocalPath" ]]; then
          write_local_bind_block "$mount_id" "$mount_key" "$host_path"
        else
          fail "Unsupported source kind '$source_kind' in override rewrite (SMB branch not implemented)."
        fi
      done
    done
  } >"$tmp"

  mv "$tmp" "$override_path"
}

apply_mount() {
  local mount_id="$1"
  local host_path="$2"
  resolve_apply_plan "$mount_id" "$host_path"

  if [[ "$PLAN_SOURCE_KIND" != "LocalPath" ]]; then
    fail "Only LocalPath mounts are implemented in this phase."
  fi

  local override_path="$ROOT_DIR/$DOCKER_DIRECTORY/$HOST_MOUNT_OVERRIDE_FILE"
  parse_existing_override "$override_path"
  MOUNT_ORDER["$PLAN_MOUNT_KEY"]="${PLAN_MOUNT_ID}|${PLAN_MOUNT_KEY}|${PLAN_SOURCE_KIND}|${PLAN_HOST_PATH}"
  if [[ " ${MOUNT_KEYS[*]:-} " != *" $PLAN_MOUNT_KEY "* ]]; then
    MOUNT_KEYS+=("$PLAN_MOUNT_KEY")
  fi
  write_override_file "$override_path"
}

remove_mount() {
  local mount_id="$1"
  local override_path="$ROOT_DIR/$DOCKER_DIRECTORY/$HOST_MOUNT_OVERRIDE_FILE"
  parse_existing_override "$override_path"

  local removed=0
  local new_keys=()
  local key
  for key in "${MOUNT_KEYS[@]:-}"; do
    IFS='|' read -r existing_mount_id _ _ _ <<<"${MOUNT_ORDER[$key]}"
    if [[ "$existing_mount_id" == "$mount_id" ]]; then
      unset 'MOUNT_ORDER[$key]'
      removed=1
    else
      new_keys+=("$key")
    fi
  done
  MOUNT_KEYS=("${new_keys[@]:-}")

  if [[ "$removed" -eq 0 ]]; then
    warn "No override block found for mount id '$mount_id'."
  fi

  write_override_file "$override_path"
}

restart_affected_services() {
  local override_path="$ROOT_DIR/$DOCKER_DIRECTORY/$HOST_MOUNT_OVERRIDE_FILE"
  local compose_args=(-f "$COMPOSE_FILE")
  if [[ -f "$override_path" ]]; then
    compose_args+=(-f "$HOST_MOUNT_OVERRIDE_FILE")
  fi

  log "Restarting affected services (--no-deps): ${AFFECTED_MOUNT_SERVICE_NAMES[*]}"
  (
    cd "$ROOT_DIR/$DOCKER_DIRECTORY"
    docker compose "${compose_args[@]}" up -d --no-deps "${AFFECTED_MOUNT_SERVICE_NAMES[@]}"
  )
}

VERB=""
MOUNT_ID=""
HOST_PATH=""
PROJECT_ID=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    apply|remove)
      VERB="$1"
      shift
      ;;
    --mount-id)
      [[ $# -ge 2 ]] || fail "Missing value for --mount-id"
      MOUNT_ID="$2"
      shift 2
      ;;
    --host-path)
      [[ $# -ge 2 ]] || fail "Missing value for --host-path"
      HOST_PATH="$2"
      shift 2
      ;;
    --project-id)
      [[ $# -ge 2 ]] || fail "Missing value for --project-id"
      PROJECT_ID="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      fail "Unknown argument: $1"
      ;;
  esac
done

[[ -n "$VERB" ]] || { usage; fail "Missing verb (apply or remove)."; }
[[ -n "$MOUNT_ID" ]] || fail "--mount-id is required."

load_installer_state

case "$VERB" in
  apply)
    apply_mount "$MOUNT_ID" "$HOST_PATH"
    restart_affected_services
    ;;
  remove)
    remove_mount "$MOUNT_ID"
    restart_affected_services
    ;;
esac

log "Done."
