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
