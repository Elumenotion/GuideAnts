# Content-addressed hash of files, keyed by repo-relative paths with forward slashes.
# Absolute checkout paths must not affect the digest: git worktrees and relocated
# clones have to share Docker image tags for the same inputs.

get_stable_repo_relative_path() {
  local path="$1"
  local relative_to="$2"
  local full root prefix

  full="$(cd "$(dirname -- "$path")" && pwd -P)/$(basename -- "$path")"
  root="$(cd -- "$relative_to" && pwd -P)"
  root="${root%/}"

  if [[ "$full" == "$root" ]]; then
    printf '%s' '.'
    return 0
  fi

  prefix="$root/"
  if [[ "$full" != "$prefix"* ]]; then
    echo "Hash input '$path' is not under repo root '$relative_to'" >&2
    return 1
  fi

  printf '%s' "${full#"$prefix"}"
}

get_combined_hash() {
  local relative_to="$1"
  shift
  local -a paths=("$@")
  local -a lines=()
  local path relative hash sorted

  if [[ ${#paths[@]} -eq 0 ]]; then
    echo "Hash input file list is empty" >&2
    return 1
  fi

  for path in "${paths[@]}"; do
    if [[ ! -f "$path" ]]; then
      echo "Hash input file not found: $path" >&2
      return 1
    fi
    relative="$(get_stable_repo_relative_path "$path" "$relative_to")" || return 1
    hash="$(sha256sum "$path" | awk '{print tolower($1)}')" || return 1
    lines+=("$relative|$hash")
  done

  sorted="$(printf '%s\n' "${lines[@]}" | LC_ALL=C sort)"
  sorted="${sorted%$'\n'}"
  printf "%s" "$sorted" | sha256sum | awk '{print $1}'
}

get_legacy_absolute_combined_hash() {
  local -a paths=("$@")
  local path line joined=""

  if [[ ${#paths[@]} -eq 0 ]]; then
    echo "Hash input file list is empty" >&2
    return 1
  fi

  for path in "${paths[@]}"; do
    if [[ ! -f "$path" ]]; then
      echo "Hash input file not found: $path" >&2
      return 1
    fi
    line="$path|$(sha256sum "$path" | awk '{print tolower($1)}')"
    if [[ -z "$joined" ]]; then
      joined="$line"
    else
      joined+=$'\n'"$line"
    fi
  done

  printf "%s" "$joined" | sha256sum | awk '{print $1}'
}

find_reusable_deps_image() {
  local canonical_tag="$1"
  local legacy_tag="$2"
  local canonical_full_hash="$3"
  local backend="$4"
  shift 4
  local -a candidates=("$@")
  local tag label

  if docker_image_exists "$canonical_tag"; then
    printf '%s' "$canonical_tag"
    return 0
  fi
  if docker_image_exists "$legacy_tag"; then
    printf '%s' "$legacy_tag"
    return 0
  fi

  for tag in "${candidates[@]}"; do
    [[ -n "$tag" ]] || continue
    [[ "$tag" == "guideants-ai-deps:${backend}-"* ]] || continue
    label="$(docker inspect --format "{{index .Config.Labels \"org.guideants.deps-input-hash\"}}" "$tag" 2>/dev/null || true)"
    if [[ "$label" == "$canonical_full_hash" ]]; then
      printf '%s' "$tag"
      return 0
    fi
  done

  local cache_tag="guideants-ai-deps:${backend}-cache"
  if docker_image_exists "$cache_tag"; then
    printf '%s' "$cache_tag"
    return 0
  fi

  return 1
}
