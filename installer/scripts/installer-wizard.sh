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
    if [[ "$k" == "$key" ]]; then
      printf '%s' "$v"
      return 0
    fi
  done < "$file"
}

installer_legacy_state() {
  local cf
  DB_LAYOUT="$(installer_state_get DB_LAYOUT "$STATE_FILE")"
  AI_BACKEND="$(installer_state_get AI_BACKEND "$STATE_FILE")"
  COMPONENTS="$(installer_state_get COMPONENTS "$STATE_FILE")"
  COMPOSE_MODE="$(installer_state_get COMPOSE_MODE "$STATE_FILE")"
  COMPOSE_FILES="$(installer_state_get COMPOSE_FILES "$STATE_FILE")"
  if [[ -z "$DB_LAYOUT" ]]; then
    cf="$(installer_state_get COMPOSE_FILE "$STATE_FILE")"
    if [[ "$cf" == *ghcr-slim* || "$cf" == *docker-compose.slim* ]]; then DB_LAYOUT=bundled; else DB_LAYOUT=separate; fi
  fi
  if [[ -z "$AI_BACKEND" ]]; then
    AI_BACKEND="$(installer_state_get BACKEND "$STATE_FILE")"
    if [[ ! "$AI_BACKEND" =~ ^(none|slim|cpu|cuda13|rocm|vulkan)$ ]]; then
      AI_BACKEND=slim
    fi
  fi
  [[ -n "$COMPONENTS" ]] || COMPONENTS="docling,documentserver,plantuml,searxng"
  if [[ -z "$COMPOSE_FILES" ]]; then
    cf="$(installer_state_get COMPOSE_FILE "$STATE_FILE")"
    if [[ -n "$cf" ]]; then
      IFS=',' read -r -a comp_array <<< "$COMPONENTS"
      mapfile -t _legacy_fragments < <(installer_compose_fragments "$DB_LAYOUT" "$AI_BACKEND" "${comp_array[@]}")
      COMPOSE_FILES="$(IFS=,; echo "${_legacy_fragments[*]}")"
    fi
  fi
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
  [[ -n "${GA_MSSQL_IMAGE:-}" ]] && export GA_MSSQL_IMAGE="$GA_MSSQL_IMAGE"
  [[ -n "${GA_AI_SLIM_IMAGE:-}" ]] && export GA_AI_SLIM_GHCR_IMAGE="$GA_AI_SLIM_IMAGE"
  [[ -n "${GA_AI_CPU_IMAGE:-}" ]] && export GA_AI_CPU_GHCR_IMAGE="$GA_AI_CPU_IMAGE"
  [[ -n "${GA_AI_CUDA_IMAGE:-}" ]] && export GA_AI_CUDA_GHCR_IMAGE="$GA_AI_CUDA_IMAGE"
  [[ -n "${GA_AI_ROCM_IMAGE:-}" ]] && export GA_AI_ROCM_GHCR_IMAGE="$GA_AI_ROCM_IMAGE"
  [[ -n "${GA_AI_VULKAN_IMAGE:-}" ]] && export GA_AI_VULKAN_GHCR_IMAGE="$GA_AI_VULKAN_IMAGE"
  [[ -n "${GA_PLANTUML_IMAGE:-}" ]] && export GA_PLANTUML_GHCR_IMAGE="$GA_PLANTUML_IMAGE"
  [[ -n "${GA_SEARXNG_IMAGE:-}" ]] && export GA_SEARXNG_GHCR_IMAGE="$GA_SEARXNG_IMAGE"
}

# Optional release pin file (written into release zips; rewritten locally on update).
installer_images_env_path() {
  if [[ -n "${IMAGES_ENV_FILE:-}" ]]; then
    printf '%s\n' "$IMAGES_ENV_FILE"
  elif [[ -n "${DOCKER_DIR:-}" ]]; then
    printf '%s\n' "$DOCKER_DIR/images.env"
  else
    printf '%s\n' "images.env"
  fi
}

installer_compose_env_args() {
  COMPOSE_ENV_ARGS=(--env-file "$ENV_FILE")
  IMAGES_ENV_FILE="$(installer_images_env_path)"
  if [[ -f "$IMAGES_ENV_FILE" ]]; then
    COMPOSE_ENV_ARGS+=(--env-file "$IMAGES_ENV_FILE")
  fi
}

installer_load_images_env_meta() {
  GA_UPDATE_CHANNEL="${GA_UPDATE_CHANNEL:-main}"
  GA_RELEASE_TAG="${GA_RELEASE_TAG:-}"
  IMAGES_ENV_FILE="$(installer_images_env_path)"
  [[ -f "$IMAGES_ENV_FILE" ]] || return 0
  local v
  v="$(installer_state_get GA_UPDATE_CHANNEL "$IMAGES_ENV_FILE")"
  [[ -n "$v" ]] && GA_UPDATE_CHANNEL="$v"
  v="$(installer_state_get GA_RELEASE_TAG "$IMAGES_ENV_FILE")"
  [[ -n "$v" ]] && GA_RELEASE_TAG="$v"
  export GA_UPDATE_CHANNEL GA_RELEASE_TAG
  if [[ -n "$GA_RELEASE_TAG" ]]; then
    installer_log "Release image pins: $GA_RELEASE_TAG (update channel :$GA_UPDATE_CHANNEL)"
  else
    installer_log "Image pins loaded from $(basename "$IMAGES_ENV_FILE") (update channel :$GA_UPDATE_CHANNEL)"
  fi
}

installer_image_repository() {
  local ref="$1"
  if [[ "$ref" == *@* ]]; then
    printf '%s\n' "${ref%%@*}"
    return 0
  fi
  if [[ "$ref" == */*:* ]]; then
    printf '%s\n' "${ref%:*}"
    return 0
  fi
  printf '%s\n' "$ref"
}

installer_update_channel_ref() {
  local image_ref="$1"
  local repo channel="${GA_UPDATE_CHANNEL:-main}"
  repo="$(installer_image_repository "$image_ref")"
  case "$repo" in
    ghcr.io/*/guideants-*|ghcr.io/*/mssql2025-express-fts)
      printf '%s:%s\n' "$repo" "$channel"
      ;;
    *)
      printf '%s\n' "$image_ref"
      ;;
  esac
}

installer_rewrite_image_pin() {
  local repo="$1" digest="$2"
  local file tmp key val vrepo
  file="$(installer_images_env_path)"
  [[ -f "$file" && -n "$digest" ]] || return 0
  tmp="$(mktemp)"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    if [[ "$line" =~ ^([A-Za-z0-9_]+)=(.*)$ ]]; then
      key="${BASH_REMATCH[1]}"
      val="${BASH_REMATCH[2]}"
      case "$key" in
        GA_*IMAGE*|GA_MSSQL_IMAGE)
          vrepo="$(installer_image_repository "$val")"
          if [[ "$vrepo" == "$repo" ]]; then
            printf '%s=%s@%s\n' "$key" "$repo" "$digest" >> "$tmp"
            continue
          fi
          ;;
      esac
    fi
    printf '%s\n' "$line" >> "$tmp"
  done < "$file"
  mv "$tmp" "$file"
}

installer_compose_args() {
  # Fragments live under docker/compose/, but host bind paths in .env
  # (./volumes/...) must resolve from docker/ — otherwise SearXNG mounts an
  # empty compose/volumes tree and crashes looking for settings.yml.
  COMPOSE_ARGS=(--project-directory "$DOCKER_DIR")
  local f rel
  while IFS= read -r f; do
    [[ -n "$f" ]] || continue
    rel="$DOCKER_DIR/$INSTALLER_COMPOSE_DIR/$f"
    [[ -f "$rel" ]] || fail "Compose fragment not found: $rel"
    COMPOSE_ARGS+=(-f "$rel")
  done < <(printf '%s\n' "${SELECTED_COMPOSE_FRAGMENTS[@]}")
}

installer_progressive_pull() {
  local images img l r channel_ref missing=0 stale=0 dig repo
  local -a missing_images=() stale_compose=() stale_channel=() pull_images=() update_channels=() pull_failures=()
  installer_compose_env_args
  installer_load_images_env_meta
  if ! images="$(installer_docker compose "${COMPOSE_ARGS[@]}" "${COMPOSE_ENV_ARGS[@]}" config --images 2>/dev/null)"; then
    fail "Could not resolve image list from compose fragments."
  fi
  installer_log "Checking for image updates (reads registry metadata only until pull)..."
  while IFS= read -r img; do
    [[ -n "$img" ]] || continue
    l="$(local_digest "$img" 2>/dev/null || true)"
    if [[ -z "$l" ]]; then
      missing=$((missing+1)); missing_images+=("$img"); continue
    fi
    if [[ "$COMPOSE_MODE" == "local" ]]; then continue; fi
    channel_ref="$(installer_update_channel_ref "$img")"
    r="$(remote_digest "$channel_ref" 2>/dev/null || true)"
    if [[ -n "$r" && "$r" != "$l" ]]; then
      stale=$((stale+1))
      stale_compose+=("$img")
      stale_channel+=("$channel_ref")
    fi
  done <<< "$images"

  if [[ "$COMPOSE_MODE" == "local" ]]; then
    if [[ "$missing" -eq 0 ]]; then installer_log "All local images are present."; return 0; fi
    installer_log "Pulling $missing missing local image(s)..."
    for img in "${missing_images[@]}"; do
      installer_log "  docker pull $img"
      installer_docker pull "$img"
    done
    return 0
  fi

  if [[ "$missing" -gt 0 ]]; then
    installer_log "$missing image(s) not present locally — will be downloaded."
    pull_images+=("${missing_images[@]}")
  fi

  if [[ "$stale" -gt 0 ]]; then
    installer_log "Updates available for $stale image(s) on channel :${GA_UPDATE_CHANNEL:-main}."
    if [[ "$ASSUME_YES" == "1" ]] || ask_yes_no "Update now before starting? [Y/n]" "Y"; then
      local i
      for ((i=0; i<stale; i++)); do
        update_channels+=("${stale_channel[$i]}")
      done
    else
      installer_log "Keeping current images for stale entries."
    fi
  fi

  if [[ ${#pull_images[@]} -eq 0 && ${#update_channels[@]} -eq 0 ]]; then
    installer_log "All images are up to date."
    return 0
  fi

  if [[ ${#pull_images[@]} -gt 0 ]]; then
    installer_log "Pulling ${#pull_images[@]} image(s) sequentially..."
    for img in "${pull_images[@]}"; do
      [[ -n "$img" ]] || continue
      installer_log "  docker pull $img"
      if ! installer_docker pull "$img"; then
        pull_failures+=("$img")
      fi
    done
  fi

  if [[ ${#update_channels[@]} -gt 0 ]]; then
    installer_log "Updating ${#update_channels[@]} image(s) from channel :${GA_UPDATE_CHANNEL:-main}..."
    for channel_ref in "${update_channels[@]}"; do
      [[ -n "$channel_ref" ]] || continue
      installer_log "  docker pull $channel_ref"
      if ! installer_docker pull "$channel_ref"; then
        pull_failures+=("$channel_ref")
        continue
      fi
      dig="$(local_digest "$channel_ref" 2>/dev/null || true)"
      repo="$(installer_image_repository "$channel_ref")"
      if [[ -n "$dig" ]]; then
        installer_rewrite_image_pin "$repo" "$dig"
        # Ensure compose digest refs resolve immediately after pin rewrite.
        installer_docker pull "${repo}@${dig}" >/dev/null 2>&1 || true
      fi
    done
  fi

  if [[ ${#pull_failures[@]} -gt 0 ]]; then
    if [[ "$SELECTED_AI_BACKEND" == "vulkan" ]]; then
      local has_vulkan_failure=0 u
      for u in "${pull_failures[@]}"; do [[ "$u" == *guideants-ai-vulkan* ]] && has_vulkan_failure=1; done
      if [[ "$has_vulkan_failure" == "1" ]]; then
        warn "The GHCR Vulkan AI image is not currently pullable:"
        printf '  - %s\n' "${pull_failures[@]}" >&2
        fail "Build locally, then rerun: ./docker/build/build_guideants_ai.sh --backend vulkan && ./installer/guideants.sh --backend vulkan --compose local --reconfigure"
      fi
    fi
    warn "One or more Compose images failed to pull:"
    printf '  - %s\n' "${pull_failures[@]}" >&2
    fail "If these are private images, run 'docker login' for the registry or switch to --compose local after building them locally."
  fi
}

installer_start_stack() {
  installer_active_services
  installer_compose_env_args
  installer_progressive_pull
  installer_log "Applying selected compose stack (remove-orphans): ${ACTIVE_SERVICES[*]}"
  installer_docker compose "${COMPOSE_ARGS[@]}" "${COMPOSE_ENV_ARGS[@]}" up -d --remove-orphans
}

installer_services_for_selection() {
  local db="$1" ai="$2"
  shift 2
  local components=("$@") services=()
  [[ "$db" == "separate" ]] && services+=(mssql-express)
  services+=(guideants-webapi-ui)
  [[ "$ai" != "none" ]] && services+=(guideants-ai)
  local c
  for c in "${components[@]}"; do
    case "$c" in
      docling) services+=(docling-serve) ;;
      documentserver) services+=(documentserver) ;;
      plantuml) services+=(plantuml) ;;
      searxng) services+=(searxng) ;;
    esac
  done
  printf '%s\n' "${services[@]}"
}

installer_active_services() {
  mapfile -t ACTIVE_SERVICES < <(installer_services_for_selection "$SELECTED_DB_LAYOUT" "$SELECTED_AI_BACKEND" "${SELECTED_COMPONENTS[@]}")
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
  COMPOSE_ARGS=(--project-directory "$root_dir/docker")
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
  [[ "$ai" != "none" ]] && services+=(guideants-ai)
  for c in "${components[@]}"; do
    case "$c" in
      plantuml) services+=(plantuml) ;;
    esac
  done
  printf '%s\n' "${services[@]}"
}

installer_select_ai_backend() {
  local intent c i n rec
  printf '\n  AI intent:\n'
  printf '    1) Cloud providers only — slim sandbox (~4.3 GB)\n'
  printf '    2) No AI container (~0 GB)\n'
  printf '    3) Local model runtime (pick CPU/GPU backend next)\n\n'
  if [[ "$ASSUME_YES" == "1" ]]; then echo slim; return 0; fi
  read -r -p 'Enter 1-3 [1=cloud/slim]: ' intent || intent=""
  case "$intent" in
    2) echo none; return 0 ;;
    3) ;;
    *) echo slim; return 0 ;;
  esac
  if declare -F recommend_backend >/dev/null 2>&1; then recommend_backend; rec="$RECOMMENDED"; else rec=cpu; fi
  installer_log "Recommended local backend: $rec"
  [[ -n "${REASON:-}" ]] && installer_log "  ($REASON)"
  local -a keys=() labels=()
  local major
  if major="$(nvidia_driver_major 2>/dev/null || true)" && [[ "$major" =~ ^[0-9]+$ && "$major" -ge 580 ]]; then
    keys+=(cuda13); labels+=("cuda13  NVIDIA CUDA 13 local runtime (~14 GB)")
  fi
  if declare -F amd_gpu_detected >/dev/null 2>&1 && amd_gpu_detected; then
    keys+=(rocm); labels+=("rocm    AMD ROCm local runtime (~20 GB)")
  fi
  keys+=(vulkan cpu)
  labels+=("vulkan  Vulkan local runtime (~8.5 GB)" "cpu     CPU local runtime (~8.2 GB)")
  n=${#keys[@]}
  printf '\n  Local AI backend:\n'
  for i in $(seq 0 $((n-1))); do
    printf '    %d) %s' "$((i+1))" "${labels[$i]}"
    [[ "${keys[$i]}" == "$rec" ]] && printf ' (recommended)'
    printf '\n'
  done
  printf '\n'
  read -r -p "Enter 1-${n}, or press Enter for recommended [$rec]: " c || c=""
  if [[ -z "$c" ]]; then echo "$rec"; return 0; fi
  if [[ "$c" =~ ^[0-9]+$ && "$c" -ge 1 && "$c" -le "$n" ]]; then echo "${keys[$((c-1))]}"; return 0; fi
  installer_warn "Unrecognized choice '$c'; using recommended ($rec)."
  echo "$rec"
}

installer_run_wizard() {
  local use_saved=0 prior_db="" prior_ai="" prior_components="" reconfigure_from_saved=0
  local -a prior_components_list=()
  if [[ -f "$STATE_FILE" ]]; then
    installer_legacy_state
    prior_db="$DB_LAYOUT"; prior_ai="$AI_BACKEND"; prior_components="$COMPONENTS"
    [[ -n "$prior_components" ]] && IFS=',' read -r -a prior_components_list <<< "$prior_components"
  fi

  if [[ "$RECONFIGURE" == "0" && -f "$STATE_FILE" ]]; then
    use_saved=1
  elif [[ -f "$STATE_FILE" ]]; then
    reconfigure_from_saved=1
  fi

  # DB layout is first-install only and immutable afterwards.
  if [[ -n "$prior_db" ]]; then
    SELECTED_DB_LAYOUT="$prior_db"
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

  if [[ -n "${AI_BACKEND_OVERRIDE:-}" ]]; then
    SELECTED_AI_BACKEND="$AI_BACKEND_OVERRIDE"
  elif [[ "$use_saved" == "1" && -n "$prior_ai" ]]; then
    SELECTED_AI_BACKEND="$prior_ai"
    installer_log "Using saved AI backend: $SELECTED_AI_BACKEND"
  elif [[ "$reconfigure_from_saved" == "1" && -n "$prior_ai" && "$ASSUME_YES" != "1" ]]; then
    installer_log "Current AI backend: $prior_ai"
    if ask_yes_no "Keep current AI backend ($prior_ai)? [Y/n]" "Y"; then
      SELECTED_AI_BACKEND="$prior_ai"
    else
      SELECTED_AI_BACKEND="$(installer_select_ai_backend)"
    fi
  else
    SELECTED_AI_BACKEND="$(installer_select_ai_backend)"
  fi

  if [[ ${#COMPONENTS_OVERRIDE[@]} -gt 0 ]]; then
    SELECTED_COMPONENTS=("${COMPONENTS_OVERRIDE[@]}")
  elif [[ "$use_saved" == "1" ]]; then
    IFS=',' read -r -a SELECTED_COMPONENTS <<< "$prior_components"
  else
    SELECTED_COMPONENTS=()
    local reply comp size impact p is_selected default_component_choice prompt_hint
    local running_total
    printf '\n  Optional components (y/n for each):\n'
    running_total="$(installer_estimated_size_gb "$SELECTED_DB_LAYOUT" "$SELECTED_AI_BACKEND" "${SELECTED_COMPONENTS[@]}")"
    installer_log "Current selected images ~ ${running_total} GB"
    for comp in "${INSTALLER_OPTIONAL_COMPONENTS[@]}"; do
      case "$comp" in
        docling)
          if [[ "$SELECTED_AI_BACKEND" == "cuda13" ]]; then size="~13.8 GB"; else size="~7.1 GB"; fi
          impact="Without DocLing and without Azure DI: document intelligence features will not work."
          ;;
        documentserver) size="~7.2 GB"; impact="Without it: DocumentServer open/edit will not work." ;;
        plantuml) size="~0.7 GB"; impact="Without it: PlantUML generation/rendering will not work." ;;
        searxng) size="~4.2 GB"; impact="Without it: web search / browser-render features will not work." ;;
        *) size=""; impact="" ;;
      esac
      printf '\n  %s (%s)\n' "$comp" "$size"
      [[ -n "$impact" ]] && printf '    Without it: %s\n' "$impact"
      is_selected=0
      if [[ "$reconfigure_from_saved" == "1" ]]; then
        for p in "${prior_components_list[@]}"; do
          p="${p#"${p%%[![:space:]]*}"}"
          p="${p%"${p##*[![:space:]]}"}"
          if [[ "$p" == "$comp" ]]; then is_selected=1; break; fi
        done
      fi
      if [[ "$ASSUME_YES" == "1" ]]; then
        SELECTED_COMPONENTS+=("$comp")
      else
        if [[ "$reconfigure_from_saved" == "1" && "$is_selected" -eq 0 ]]; then
          default_component_choice="N"; prompt_hint="[y/N]"
        else
          default_component_choice="Y"; prompt_hint="[Y/n]"
        fi
        read -r -p "  Include $comp? $prompt_hint " reply || reply=""
        reply="${reply:-$default_component_choice}"
        [[ "$reply" =~ ^[Yy] ]] && SELECTED_COMPONENTS+=("$comp")
      fi
      running_total="$(installer_estimated_size_gb "$SELECTED_DB_LAYOUT" "$SELECTED_AI_BACKEND" "${SELECTED_COMPONENTS[@]}")"
      installer_log "Current selected images ~ ${running_total} GB"
    done
  fi

  mapfile -t SELECTED_COMPOSE_FRAGMENTS < <(installer_compose_fragments "$SELECTED_DB_LAYOUT" "$SELECTED_AI_BACKEND" "${SELECTED_COMPONENTS[@]}")
  if [[ -n "$prior_ai" && "$prior_ai" != "$SELECTED_AI_BACKEND" ]]; then
    installer_log "AI backend changed: $prior_ai -> $SELECTED_AI_BACKEND"
  fi
  local est; est="$(installer_estimated_size_gb "$SELECTED_DB_LAYOUT" "$SELECTED_AI_BACKEND" "${SELECTED_COMPONENTS[@]}")"
  installer_log "Selected images ~ ${est} GB (not including model weights downloaded later inside the AI container)."
}
