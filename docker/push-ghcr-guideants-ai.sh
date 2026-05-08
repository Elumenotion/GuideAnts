#!/usr/bin/env bash
set -euo pipefail

OWNER=""
REGISTRY="ghcr.io"
COMPOSE_TAG="main"
USERNAME="${GHCR_USERNAME:-}"
TOKEN="${CR_PAT:-${GHCR_PAT:-${GITHUB_TOKEN:-}}}"
SKIP_LOGIN=false
DRY_RUN=false

usage() {
  cat <<'EOF'
Usage: push-ghcr-guideants-ai.sh [options]

Options:
  --owner <owner>          GHCR owner (defaults to git origin owner)
  --registry <registry>    Registry hostname (default: ghcr.io)
  --compose-tag <tag>      Compose tag (default: main)
  --username <username>    GHCR username (default: GHCR_USERNAME)
  --token <token>          GHCR token (default: CR_PAT | GHCR_PAT | GITHUB_TOKEN)
  --skip-login             Skip docker login
  --dry-run                Print docker commands only
  -h, --help               Show help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --owner)
      OWNER="${2:-}"
      shift 2
      ;;
    --registry)
      REGISTRY="${2:-}"
      shift 2
      ;;
    --compose-tag)
      COMPOSE_TAG="${2:-}"
      shift 2
      ;;
    --username)
      USERNAME="${2:-}"
      shift 2
      ;;
    --token)
      TOKEN="${2:-}"
      shift 2
      ;;
    --skip-login)
      SKIP_LOGIN=true
      shift
      ;;
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Invalid argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

docker_cmd() {
  if [[ "$DRY_RUN" == "true" ]]; then
    echo "[dry-run] docker $*"
    return 0
  fi
  docker "$@"
}

get_owner_from_git_remote() {
  local remote
  remote="$(git remote get-url origin 2>/dev/null || true)"
  [[ -n "$remote" ]] || return 1
  if [[ "$remote" =~ github\.com[:/]([^/]+)/([^/.]+)(\.git)?$ ]]; then
    echo "${BASH_REMATCH[1],,}"
    return 0
  fi
  return 1
}

get_latest_variant_image() {
  local variant="$1"
  local pattern
  case "$variant" in
    cpu) pattern='^cpu-([0-9]{5}\.[0-9]{4})$' ;;
    cuda13) pattern='^cuda13-([0-9]{5}\.[0-9]{4})$' ;;
    rocm) pattern='^rocm-([0-9]{5}\.[0-9]{4})$' ;;
    *) echo "Unsupported variant: $variant" >&2; return 1 ;;
  esac

  local rows
  rows="$(docker image ls guideants-ai --format '{{.Repository}}|{{.Tag}}' || true)"
  [[ -n "$rows" ]] || { echo "Unable to enumerate local guideants-ai images." >&2; return 1; }

  local best_sort=-1
  local best_source=""
  local best_build=""
  local row repo tag build sort_key

  while IFS= read -r row; do
    [[ -z "$row" ]] && continue
    repo="${row%%|*}"
    tag="${row#*|}"
    [[ -n "$tag" && "$tag" != "<none>" ]] || continue
    if [[ "$tag" =~ $pattern ]]; then
      build="${BASH_REMATCH[1]}"
      sort_key="${build/./}"
      if (( 10#$sort_key > best_sort )); then
        best_sort=$((10#$sort_key))
        best_source="${repo}:${tag}"
        best_build="$build"
      fi
    fi
  done <<< "$rows"

  if [[ -z "$best_source" ]]; then
    echo "No local guideants-ai:${variant}-* image found. Build that variant first with docker/build/build_guideants_ai.sh." >&2
    return 1
  fi

  echo "${best_source}|${best_build}"
}

get_local_image_ref() {
  local repository="$1"
  local tag="${2:-latest}"
  local missing_message="$3"
  local rows row repo row_tag

  rows="$(docker image ls "$repository" --format '{{.Repository}}|{{.Tag}}' || true)"
  [[ -n "$rows" ]] || { echo "Unable to enumerate local image '$repository'." >&2; return 1; }

  while IFS= read -r row; do
    [[ -z "$row" ]] && continue
    repo="${row%%|*}"
    row_tag="${row#*|}"
    if [[ "${repo,,}" == "${repository,,}" && "${row_tag,,}" == "${tag,,}" ]]; then
      echo "${repo}:${row_tag}"
      return 0
    fi
  done <<< "$rows"

  echo "$missing_message" >&2
  return 1
}

if [[ -z "$OWNER" ]]; then
  OWNER="$(get_owner_from_git_remote || true)"
fi
[[ -n "$OWNER" ]] || { echo "Unable to determine GHCR owner. Pass --owner (for example: --owner elumenotion)." >&2; exit 1; }
OWNER="${OWNER,,}"

if [[ "$SKIP_LOGIN" != "true" ]]; then
  [[ -n "$USERNAME" ]] || { echo "GHCR username is required. Pass --username or set GHCR_USERNAME." >&2; exit 1; }
  [[ -n "$TOKEN" ]] || { echo "GHCR token is required. Pass --token or set CR_PAT / GHCR_PAT / GITHUB_TOKEN." >&2; exit 1; }

  if [[ "$DRY_RUN" == "true" ]]; then
    echo "[dry-run] docker login $REGISTRY -u $USERNAME --password-stdin"
  else
    printf "%s" "$TOKEN" | docker login "$REGISTRY" -u "$USERNAME" --password-stdin
  fi
fi

cpu_info="$(get_latest_variant_image cpu)"
cuda_info="$(get_latest_variant_image cuda13)"
rocm_info=""
if ! rocm_info="$(get_latest_variant_image rocm 2>/dev/null)"; then
  echo "Warning: No local ROCm image found; skipping ROCm push. Build it first with docker/build/build_guideants_ai.sh --backend rocm." >&2
fi

CPU_SOURCE="${cpu_info%%|*}"
CPU_BUILD="${cpu_info#*|}"
CUDA_SOURCE="${cuda_info%%|*}"
CUDA_BUILD="${cuda_info#*|}"

targets=(
  "cpu|guideants-ai-cpu|$CPU_SOURCE|$CPU_BUILD"
  "cuda13|guideants-ai-cuda13|$CUDA_SOURCE|$CUDA_BUILD"
)
if [[ -n "$rocm_info" ]]; then
  ROCM_SOURCE="${rocm_info%%|*}"
  ROCM_BUILD="${rocm_info#*|}"
  targets+=("rocm|guideants-ai-rocm|$ROCM_SOURCE|$ROCM_BUILD")
fi

for target in "${targets[@]}"; do
  IFS='|' read -r variant package source_ref build_tag <<< "$target"
  build_ref="$REGISTRY/$OWNER/$package:$build_tag"
  latest_ref="$REGISTRY/$OWNER/$package:latest"
  compose_ref="$REGISTRY/$OWNER/$package:$COMPOSE_TAG"

  echo
  echo "Pushing $variant image"
  echo "  Source:      $source_ref"
  echo "  Build tag:   $build_ref"
  echo "  Compose tag: $compose_ref"
  echo "  Latest tag:  $latest_ref"

  docker_cmd tag "$source_ref" "$build_ref"
  docker_cmd push "$build_ref"
  docker_cmd tag "$source_ref" "$compose_ref"
  docker_cmd push "$compose_ref"
  docker_cmd tag "$source_ref" "$latest_ref"
  docker_cmd push "$latest_ref"
done

plantuml_source_ref="$(get_local_image_ref 'plantuml-1.2025.2' 'latest' 'No local plantuml-1.2025.2:latest image found. Build it first with docker/build/build_guideants_ai.sh --all.')"
mssql_source_ref="$(get_local_image_ref 'mssql2025-express-fts' 'latest' 'No local mssql2025-express-fts:latest image found. Build it first with docker/build/build_guideants_ai.sh --all.')"
searxng_source_ref="$(get_local_image_ref 'guideants-searxng' 'latest' 'No local guideants-searxng:latest image found. Build it first with docker/build/build_guideants_ai.sh --all.')"

extra_targets=(
  "plantuml|$plantuml_source_ref|guideants-plantuml|$CPU_BUILD,1.2025.2,$COMPOSE_TAG,latest"
  "mssql|$mssql_source_ref|mssql2025-express-fts|$CPU_BUILD,$COMPOSE_TAG,latest"
  "searxng|$searxng_source_ref|guideants-searxng|$CPU_BUILD,$COMPOSE_TAG,latest"
)

for target in "${extra_targets[@]}"; do
  IFS='|' read -r name source_ref package tags_csv <<< "$target"
  IFS=',' read -ra tags <<< "$tags_csv"

  echo
  echo "Pushing $name image"
  echo "  Source:      $source_ref"
  for tag in "${tags[@]}"; do
    target_ref="$REGISTRY/$OWNER/$package:$tag"
    echo "  Target tag:  $target_ref"
    docker_cmd tag "$source_ref" "$target_ref"
    docker_cmd push "$target_ref"
  done
done

echo
if [[ -n "$rocm_info" ]]; then
  echo "Done. Pushed latest local CPU, CUDA13, and ROCm GuideAnts AI images plus PlantUML, MSSQL FTS, and SearXNG images to GHCR owner '$OWNER'."
else
  echo "Done. Pushed latest local CPU and CUDA13 GuideAnts AI images plus PlantUML, MSSQL FTS, and SearXNG images to GHCR owner '$OWNER'."
fi
