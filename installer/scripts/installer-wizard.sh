#!/usr/bin/env bash
# Shared installer wizard: component metadata, state, compose assembly, progressive pull.
# shellcheck disable=SC2034
# Sourced from guideants.sh and stop_guideants.sh.

INSTALLER_COMPOSE_DIR="compose"
INSTALLER_OPTIONAL_COMPONENTS=(docling documentserver plantuml searxng)
INSTALLER_AI_BACKENDS=(none slim cpu cuda13 rocm vulkan)

installer_log() {
  if declare -F installer_log_fn >/dev/null 2>&1; then installer_log_fn "$@"; else log "$@"; fi
}

installer_warn() {
  if declare -F installer_warn_fn >/dev/null 2>&1; then installer_warn_fn "$@"; else warn "$@"; fi
}

installer_docker() {
  if declare -F installer_docker_fn >/dev/null 2>&1; then installer_docker_fn "$@"; else docker "$@"; fi
}

installer_docling_fragment() {
  [[ "$1" == "cuda13" ]] && echo "docling-cuda.yml" || echo "docling-cpu.yml"
}

installer_compose_fragments() {
  local db_layout="$1" ai_backend="$2"
  shift 2
  local components=("$@")
  local files=(base.yml)
  if [[ "$db_layout" == "separate" ]]; then files+=(core-separate.yml); else files+=(core-bundled.yml); fi
  if [[ "$ai_backend" != "none" ]]; then files+=("ai-${ai_backend}.yml"); fi
  local c
  for c in "${components[@]}"; do
    case "$c" in
      docling) files+=("$(installer_docling_fragment "$ai_backend")") ;;
      documentserver|plantuml|searxng) files+=("${c}.yml") ;;
    esac
  done
  printf '%s\n' "${files[@]}"
}

installer_estimated_size_gb() {
  local db="$1" ai="$2"
  shift 2
  local total=0
  if [[ "$db" == "separate" ]]; then total=$((total + 76)); else total=$((total + 73)); fi
  case "$ai" in
    slim) total=$((total + 43)) ;;
    cpu) total=$((total + 82)) ;;
    cuda13) total=$((total + 140)) ;;
    rocm) total=$((total + 200)) ;;
    vulkan) total=$((total + 85)) ;;
  esac
  local c
  for c in "$@"; do
    case "$c" in
      docling) if [[ "$ai" == "cuda13" ]]; then total=$((total + 138)); else total=$((total + 71)); fi ;;
      documentserver) total=$((total + 72)) ;;
      plantuml) total=$((total + 7)) ;;
      searxng) total=$((total + 42)) ;;
    esac
  done
  awk -v t="$total" 'BEGIN { printf "%.1f", t/10 }'
}

installer_state_get() {
  local key="$1" file="$2" line k v
  [[ -f "$file" ]] || return 0
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%%#*}"
    line="${line#"${line%%[![:space:]]*}"}"
    line="${line%"${line##*[![:space:]]}"}"
    [[ -n "$line" ]] || continue
    k="${line%%=*}"
    v="${line#*=}"
    [[ "$k" == "$key" ]] && { printf '%s' "$v"; return 0; }
  done < "$file"
}

installer_legacy_state() {
  DB_LAYOUT="$(installer_state_get DB_LAYOUT "$STATE_FILE")"
  AI_BACKEND="$(installer_state_get AI_BACKEND "$STATE_FILE")"
  COMPONENTS="$(installer_state_get COMPONENTS "$STATE_FILE")"
  COMPOSE_MODE="$(installer_state_get COMPOSE_MODE "$STATE_FILE")"
  COMPOSE_FILES="$(installer_state_get COMPOSE_FILES "$STATE_FILE")"
  [[ -n "$DB_LAYOUT" ]] || {
    local cf; cf="$(installer_state_get COMPOSE_FILE "$STATE_FILE")"
    if [[ "$cf" == *ghcr-slim* || "$cf" == *docker-compose.slim* ]]; then DB_LAYOUT=bundled; else DB_LAYOUT=separate; fi
  }
  [[ -n "$AI_BACKEND" ]] || {
    AI_BACKEND="$(installer_state_get BACKEND "$STATE_FILE")"
    [[ "$AI_BACKEND" =~ ^(none|slim|cpu|cuda13|rocm|vulkan)$ ]] || AI_BACKEND=slim
  }
  [[ -n "$COMPONENTS" ]] || COMPONENTS="docling,documentserver,plantuml,searxng"
  [[ -n "$COMPOSE_MODE" ]] || COMPOSE_MODE=ghcr
}

installer_save_state() {
  local db="$1" ai="$2" components_csv="$3" compose_files_csv="$4" mode="$5" start_cmd="$6"
  local epoch; epoch="$(date +%s)"
  cat > "$STATE_FILE" <<EOF
DB_LAYOUT=${db}
AI_BACKEND=${ai}
BACKEND=${ai}
COMPONENTS=${components_csv}
COMPOSE_MODE=${mode}
COMPOSE_FILES=${compose_files_csv}
COMPOSE_FILE=${compose_files_csv}
HOST_MOUNT_OVERRIDE_FILE=docker-compose.host-mounts.generated.yml
VOICE_PACK_OVERRIDE_FILE=docker-compose.voice-pack.local.yml
DOCKER_DIRECTORY=docker
START_COMMAND=${start_cmd}
LAST_RUN_EPOCH=${epoch}
EOF
}

installer_set_local_image_env() {
  [[ "$COMPOSE_MODE" == "local" ]] || return 0
  [[ -n "${GA_WEBAPI_UI_MSSQL_IMAGE:-}" ]] && export GA_WEBAPI_UI_MSSQL_GHCR_IMAGE="$GA_WEBAPI_UI_MSSQL_IMAGE"
  [[ -n "${GA_WEBAPI_UI_SLIM_IMAGE:-}" ]] && export GA_WEBAPI_UI_SLIM_GHCR_IMAGE="$GA_WEBAPI_UI_SLIM_IMAGE"
  [[ -n "${GA_AI_SLIM_IMAGE:-}" ]] && export GA_AI_SLIM_GHCR_IMAGE="$GA_AI_SLIM_IMAGE"
  case "$SELECTED_AI_BACKEND" in
    cpu) [[ -n "${GA_AI_CPU_IMAGE:-}" ]] && export GA_AI_GHCR_IMAGE="$GA_AI_CPU_IMAGE" ;;
    cuda13) [[ -n "${GA_AI_CUDA_IMAGE:-}" ]] && export GA_AI_GHCR_IMAGE="$GA_AI_CUDA_IMAGE" ;;
    rocm) [[ -n "${GA_AI_ROCM_IMAGE:-}" ]] && export GA_AI_GHCR_IMAGE="$GA_AI_ROCM_IMAGE" ;;
    vulkan) [[ -n "${GA_AI_VULKAN_IMAGE:-}" ]] && export GA_AI_GHCR_IMAGE="$GA_AI_VULKAN_IMAGE" ;;
  esac
}

installer_compose_args() {
  COMPOSE_ARGS=()
  local f rel
  while IFS= read -r f; do
    [[ -n "$f" ]] || continue
    rel="$DOCKER_DIR/$INSTALLER_COMPOSE_DIR/$f"
    [[ -f "$rel" ]] || fail "Compose fragment not found: $rel"
    COMPOSE_ARGS+=(-f "$rel")
  done < <(printf '%s\n' "${SELECTED_COMPOSE_FRAGMENTS[@]}")
}

installer_progressive_pull() {
  local images img
  mapfile -t images < <(installer_docker compose "${COMPOSE_ARGS[@]}" --env-file "$ENV_FILE" config --images 2>/dev/null | sed '/^$/d' | sort -u)
  installer_log "Pulling ${#images[@]} image(s) sequentially..."
  for img in "${images[@]}"; do
    [[ -n "$img" ]] || continue
    installer_log "  docker pull $img"
    installer_docker pull "$img"
  done
}

installer_active_services() {
  ACTIVE_SERVICES=()
  [[ "$SELECTED_DB_LAYOUT" == "separate" ]] && ACTIVE_SERVICES+=(mssql-express)
  ACTIVE_SERVICES+=(guideants-webapi-ui)
  [[ "$SELECTED_AI_BACKEND" != "none" ]] && ACTIVE_SERVICES+=(guideants-ai)
  local c
  for c in "${SELECTED_COMPONENTS[@]}"; do
    case "$c" in
      docling) ACTIVE_SERVICES+=(docling-serve) ;;
      documentserver) ACTIVE_SERVICES+=(documentserver) ;;
      plantuml) ACTIVE_SERVICES+=(plantuml) ;;
      searxng) ACTIVE_SERVICES+=(readweb-searxng) ;;
    esac
  done
}

installer_prune_deselected() {
  SELECTED_DB_LAYOUT="${SELECTED_DB_LAYOUT:-bundled}"
  installer_active_services
  local all=(mssql-express guideants-webapi-ui guideants-ai docling-serve documentserver plantuml readweb-searxng)
  local remove=() s keep=0
  for s in "${all[@]}"; do
    keep=0
    for k in "${ACTIVE_SERVICES[@]}"; do [[ "$k" == "$s" ]] && keep=1; done
    [[ "$keep" -eq 0 ]] && remove+=("$s")
  done
  [[ ${#remove[@]} -eq 0 ]] && return 0
  installer_log "Stopping deselected services: ${remove[*]}"
  installer_docker compose "${COMPOSE_ARGS[@]}" --env-file "$ENV_FILE" stop "${remove[@]}" >/dev/null 2>&1 || true
  installer_docker compose "${COMPOSE_ARGS[@]}" --env-file "$ENV_FILE" rm -f "${remove[@]}" >/dev/null 2>&1 || true
}

installer_build_compose_args_from_state() {
  local root_dir="$1" state_file="$2"
  local include_host="${3:-0}" include_voice="${4:-0}" include_rocm="${5:-0}"
  STATE_FILE="$state_file"
  installer_legacy_state
  local files=() comp_array=()
  if [[ -n "${COMPOSE_FILES:-}" ]]; then
    IFS=',' read -r -a files <<< "$COMPOSE_FILES"
  else
    [[ -n "${COMPONENTS:-}" ]] && IFS=',' read -r -a comp_array <<< "$COMPONENTS"
    mapfile -t files < <(installer_compose_fragments "$DB_LAYOUT" "$AI_BACKEND" "${comp_array[@]}")
  fi
  COMPOSE_ARGS=()
  local f rel trimmed
  for f in "${files[@]}"; do
    trimmed="${f#"${f%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    [[ -n "$trimmed" ]] || continue
    rel="$root_dir/docker/$INSTALLER_COMPOSE_DIR/$trimmed"
    [[ -f "$rel" ]] || fail "Compose fragment not found: $rel"
    COMPOSE_ARGS+=(-f "$rel")
  done
  if [[ "$include_host" == "1" ]]; then
    local hm="$root_dir/docker/docker-compose.host-mounts.generated.yml"
    [[ -f "$hm" ]] && COMPOSE_ARGS+=(-f "$hm")
  fi
  if [[ "$include_voice" == "1" ]]; then
    local vp="$root_dir/docker/docker-compose.voice-pack.local.yml"
    [[ -f "$vp" ]] && COMPOSE_ARGS+=(-f "$vp")
  fi
  if [[ "$include_rocm" == "1" && "$AI_BACKEND" == "rocm" ]]; then
    local rocm="$root_dir/docker/docker-compose.rocm-runtime.generated.yml"
    [[ -f "$rocm" ]] && COMPOSE_ARGS+=(-f "$rocm")
  fi
}

installer_mount_restart_services() {
  local db="$1" ai="$2"
  shift 2
  local components=("$@") services=(guideants-webapi-ui) c
  [[ "$db" == "separate" ]] && services=(mssql-express "${services[@]}")
  [[ "$ai" != "none" ]] && services+=(guideants-ai)
  for c in "${components[@]}"; do
    case "$c" in
      plantuml) services+=(plantuml) ;;
    esac
  done
  printf '%s\n' "${services[@]}"
}

installer_run_wizard() {
  local use_saved=0
  if [[ "$RECONFIGURE" == "0" && -f "$STATE_FILE" ]]; then use_saved=1; fi

  if [[ -n "$DB_LAYOUT_OVERRIDE" ]]; then
    SELECTED_DB_LAYOUT="$DB_LAYOUT_OVERRIDE"
  elif [[ "$use_saved" == "1" ]]; then
    installer_legacy_state
    SELECTED_DB_LAYOUT="$DB_LAYOUT"
    installer_log "Using saved DB layout: $SELECTED_DB_LAYOUT"
  else
    printf '\n  Database layout:\n'
    printf '    1) Bundled webapi-ui-mssql (~7.3 GB)\n'
    printf '    2) Separate webapi-ui-slim + mssql-express (~7.6 GB)\n\n'
    if [[ "$ASSUME_YES" == "1" ]]; then SELECTED_DB_LAYOUT=bundled
    else
      local c; read -r -p 'Enter 1-2 [1=bundled]: ' c || c=""
      [[ "$c" == "2" ]] && SELECTED_DB_LAYOUT=separate || SELECTED_DB_LAYOUT=bundled
    fi
  fi

  if [[ -n "$AI_BACKEND_OVERRIDE" ]]; then
    SELECTED_AI_BACKEND="$AI_BACKEND_OVERRIDE"
  elif [[ "$use_saved" == "1" ]]; then
    installer_legacy_state
    SELECTED_AI_BACKEND="$AI_BACKEND"
    installer_log "Using saved AI backend: $SELECTED_AI_BACKEND"
  else
    printf '\n  AI container (sandbox, skills, local MCP servers, local models):\n'
    printf '    1) none (~0 GB)\n'
    printf '    2) slim (~4.3 GB) — sandbox only, no local model runtime\n'
    printf '    3) cpu (~8.2 GB)\n    4) cuda13 (~14 GB)\n    5) rocm (~20 GB)\n    6) vulkan (~8.5 GB)\n\n'
    if [[ "$ASSUME_YES" == "1" ]]; then SELECTED_AI_BACKEND=slim
    else
      local c; read -r -p 'Enter 1-6 [2=slim]: ' c || c=""
      case "$c" in
        1) SELECTED_AI_BACKEND=none ;;
        3) SELECTED_AI_BACKEND=cpu ;;
        4) SELECTED_AI_BACKEND=cuda13 ;;
        5) SELECTED_AI_BACKEND=rocm ;;
        6) SELECTED_AI_BACKEND=vulkan ;;
        *) SELECTED_AI_BACKEND=slim ;;
      esac
    fi
  fi

  if [[ ${#COMPONENTS_OVERRIDE[@]} -gt 0 ]]; then
    SELECTED_COMPONENTS=("${COMPONENTS_OVERRIDE[@]}")
  elif [[ "$use_saved" == "1" ]]; then
    installer_legacy_state
    IFS=',' read -r -a SELECTED_COMPONENTS <<< "$COMPONENTS"
  else
    SELECTED_COMPONENTS=()
    local c reply comp size impact
    printf '\n  Optional components (y/n for each):\n'
    for comp in "${INSTALLER_OPTIONAL_COMPONENTS[@]}"; do
      case "$comp" in
        docling) size="~7.1 GB"; impact="Without DocLing and without Azure DI: document intelligence features will not work." ;;
        documentserver) size="~7.2 GB"; impact="Without it: DocumentServer open/edit will not work." ;;
        plantuml) size="~0.7 GB"; impact="Without it: PlantUML generation/rendering will not work." ;;
        searxng) size="~4.2 GB"; impact="Without it: web search / browser-render features will not work." ;;
        *) size=""; impact="" ;;
      esac
      printf '\n  %s (%s)\n' "$comp" "$size"
      [[ -n "$impact" ]] && printf '    Without it: %s\n' "$impact"
      if [[ "$ASSUME_YES" == "1" ]]; then SELECTED_COMPONENTS+=("$comp"); continue; fi
      read -r -p "  Include $comp? [Y/n] " reply || reply=""
      reply="${reply:-Y}"
      [[ "$reply" =~ ^[Yy] ]] && SELECTED_COMPONENTS+=("$comp")
    done
  fi

  mapfile -t SELECTED_COMPOSE_FRAGMENTS < <(installer_compose_fragments "$SELECTED_DB_LAYOUT" "$SELECTED_AI_BACKEND" "${SELECTED_COMPONENTS[@]}")
  local est; est="$(installer_estimated_size_gb "$SELECTED_DB_LAYOUT" "$SELECTED_AI_BACKEND" "${SELECTED_COMPONENTS[@]}")"
  installer_log "Selected images ~ ${est} GB (not including model weights downloaded later inside the AI container)."
}
