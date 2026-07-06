#!/usr/bin/env bash
set -euo pipefail

REBUILD_BASE=false
BUILD_ALL=false
BACKEND=""

usage() {
  cat <<'EOF'
Usage: build_guideants_ai.sh [options]

Options:
  --rebuild-base         Rebuild dependency/base layers without cache
  --all                  Removed; use build_support_images.sh after backend builds
  --backend <value>      Backend: cpu | cuda13 | rocm | slim | vulkan
  -h, --help             Show help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rebuild-base)
      REBUILD_BASE=true
      shift
      ;;
    --all)
      BUILD_ALL=true
      shift
      ;;
    --backend)
      BACKEND="${2:-}"
      shift 2
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

if [[ "$BUILD_ALL" == "true" ]]; then
  echo "The --all support-image build was split out. Run build_support_images.sh separately after backend builds." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$DOCKER_ROOT/.." && pwd)"
SERVER_PATH="$REPO_ROOT/src/server"
BUILD_CONTEXT="$SCRIPT_DIR/guideants-ai"
DEPS_CACHE_PATH="$DOCKER_ROOT/.buildx-cache-deps"
FINAL_CACHE_PATH="$DOCKER_ROOT/.buildx-cache-final"
DEPS_CACHE_PATH_NEW="${DEPS_CACHE_PATH}-new"
FINAL_CACHE_PATH_NEW="${FINAL_CACHE_PATH}-new"

export DOCKER_BUILDKIT=1

mkdir -p "$DEPS_CACHE_PATH" "$FINAL_CACHE_PATH"
rm -rf "$DEPS_CACHE_PATH_NEW" "$FINAL_CACHE_PATH_NEW"

promote_local_cache() {
  local current_path="$1"
  local new_path="$2"

  [[ -d "$new_path" ]] || return 0
  rm -rf "$current_path"
  mv "$new_path" "$current_path"
}

buildx_supports_cache_export() {
  local inspect_output
  if ! inspect_output="$(docker buildx inspect --bootstrap 2>/dev/null)"; then
    echo "WARNING: Could not inspect Docker Buildx builder. Disabling local cache export (--cache-to) for this run." >&2
    return 1
  fi

  local driver
  driver="$(printf '%s\n' "$inspect_output" | awk -F': ' '/^[[:space:]]*Driver:/ {print tolower($2); exit}')"
  if [[ -z "$driver" ]]; then
    echo "WARNING: Could not determine Buildx driver. Disabling local cache export (--cache-to) for this run." >&2
    return 1
  fi

  if [[ "$driver" == "docker" ]]; then
    echo "WARNING: Buildx driver 'docker' does not support cache export. Continuing without --cache-to." >&2
    return 1
  fi

  return 0
}

BUILDX_CACHE_EXPORT_SUPPORTED=false
if buildx_supports_cache_export; then
  BUILDX_CACHE_EXPORT_SUPPORTED=true
fi

get_combined_hash() {
  local -a paths=("$@")
  local line
  local joined=""

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

docker_image_exists() {
  local image_tag="$1"
  docker image inspect "$image_tag" >/dev/null 2>&1
}

if [[ -z "$BACKEND" ]]; then
  echo "Select backend:"
  echo "  1) CPU-only"
  echo "  2) CUDA 13"
  echo "  3) ROCm"
  echo "  4) Slim"
  echo "  5) Vulkan"
  read -r -p "Enter choice [1-5]: " choice
else
  case "$BACKEND" in
    cpu) choice="1" ;;
    cuda13) choice="2" ;;
    rocm) choice="3" ;;
    slim) choice="4" ;;
    vulkan) choice="5" ;;
    *)
      echo "Invalid backend: $BACKEND (expected cpu|cuda13|rocm|slim|vulkan)" >&2
      exit 1
      ;;
  esac
fi

case "$choice" in
  1)
    BACKEND="cpu"
    FULL_TARGET="final-cpu"
    DEPS_TARGET="deps-cpu"
    DEPS_IMAGE_ARG="GA_DEPS_CPU_IMAGE"
    REQUIREMENTS_SRC="$SCRIPT_DIR/Sandboxes/python311TorchCPU/requirements.txt"
    DOCKERFILE_PATH="$BUILD_CONTEXT/Dockerfile.cpu"
    ;;
  2)
    BACKEND="cuda13"
    FULL_TARGET="final-cuda13"
    DEPS_TARGET="deps-cuda13"
    DEPS_IMAGE_ARG="GA_DEPS_CUDA13_IMAGE"
    REQUIREMENTS_SRC="$SCRIPT_DIR/Sandboxes/python311TorchCUDA/requirements.txt"
    DOCKERFILE_PATH="$BUILD_CONTEXT/Dockerfile.cuda"
    ;;
  3)
    BACKEND="rocm"
    FULL_TARGET="final-rocm"
    DEPS_TARGET="deps-rocm"
    DEPS_IMAGE_ARG="GA_DEPS_ROCM_IMAGE"
    REQUIREMENTS_SRC="$SCRIPT_DIR/Sandboxes/python311TorchROCM/requirements.txt"
    DOCKERFILE_PATH="$BUILD_CONTEXT/Dockerfile.rocm"
    ;;
  4)
    BACKEND="slim"
    FULL_TARGET="final-slim"
    DEPS_TARGET="deps-slim"
    DEPS_IMAGE_ARG="GA_DEPS_SLIM_IMAGE"
    REQUIREMENTS_SRC="$SCRIPT_DIR/Sandboxes/python311Slim/requirements.txt"
    DOCKERFILE_PATH="$BUILD_CONTEXT/Dockerfile.slim"
    ;;
  5)
    BACKEND="vulkan"
    FULL_TARGET="final-vulkan"
    DEPS_TARGET="deps-vulkan"
    DEPS_IMAGE_ARG="GA_DEPS_VULKAN_IMAGE"
    REQUIREMENTS_SRC="$SCRIPT_DIR/Sandboxes/python311TorchVulkan/requirements.txt"
    DOCKERFILE_PATH="$BUILD_CONTEXT/Dockerfile.vulkan"
    ;;
  *)
    echo "Invalid choice." >&2
    exit 1
    ;;
esac

# Build a unique tag per build, and also maintain a stable backend-specific latest tag.
JULIAN_DAY="$(date +%y%j)"
TIME_STAMP="$(date +%H%M)"
IMAGE_TAG="guideants-ai:${BACKEND}-${JULIAN_DAY}.${TIME_STAMP}"
LATEST_IMAGE_TAG="guideants-ai:${BACKEND}-latest"

echo "============================================"
echo "  Building GuideAnts AI"
echo "============================================"
echo "Backend:       $BACKEND"
echo "Target stage:  $FULL_TARGET"
echo "Image tag:     $IMAGE_TAG"
echo "Latest alias:  $LATEST_IMAGE_TAG"
echo "Deps target:   $DEPS_TARGET"
echo "Rebuild base:  $REBUILD_BASE"
if [[ "$BUILD_ALL" == "true" ]]; then
  echo "All images:    Yes"
fi
echo

[[ -f "$REQUIREMENTS_SRC" ]] || { echo "Requirements file not found at $REQUIREMENTS_SRC" >&2; exit 1; }
[[ -f "$DOCKERFILE_PATH" ]] || { echo "Dockerfile not found at $DOCKERFILE_PATH" >&2; exit 1; }

SCRIPT_AGENT_PROJECT="$SERVER_PATH/ScriptExecutionAgent"
[[ -d "$SCRIPT_AGENT_PROJECT" ]] || { echo "ScriptExecutionAgent directory not found at $SCRIPT_AGENT_PROJECT" >&2; exit 1; }

PUBLISH_OUTPUT="$SCRIPT_AGENT_PROJECT/publish"
rm -rf "$PUBLISH_OUTPUT"

(
  cd "$SCRIPT_AGENT_PROJECT"
  dotnet restore
  dotnet publish -c Release -o ./publish
)
echo "ScriptExecutionAgent built successfully."

AGENT_DEST="$BUILD_CONTEXT/ScriptExecutionAgent"
REQ_DEST="$BUILD_CONTEXT/requirements.txt"
cleanup() {
  rm -rf "$AGENT_DEST"
  rm -f "$REQ_DEST"
}
trap cleanup EXIT

rm -rf "$AGENT_DEST"
cp -R "$PUBLISH_OUTPUT" "$AGENT_DEST"

# Sandbox requirements.txt is copied as-is: torch/torchaudio/torchvision/torchtext were
# removed from the sandbox requirements files (Tier B torch removal, 2026-07-02); the AI
# services never depended on torch (facades over native audio.cpp/llama.cpp/sd.cpp binaries).
cp "$REQUIREMENTS_SRC" "$REQ_DEST"

echo "Build context staged."

DEPS_HASH_INPUTS=(
  "$DOCKERFILE_PATH"
  "$BUILD_CONTEXT/asr-requirements.txt"
  "$BUILD_CONTEXT/tts-requirements.txt"
  "$BUILD_CONTEXT/emb-requirements.txt"
  "$REQ_DEST"
)
DEPS_HASH="$(get_combined_hash "${DEPS_HASH_INPUTS[@]}")"
DEPS_HASH="${DEPS_HASH:0:12}"
DEPS_TAG="guideants-ai-deps:${BACKEND}-${DEPS_HASH}"
DEPS_CACHE_TAG="guideants-ai-deps:${BACKEND}-cache"

echo "Dependency image tag: $DEPS_TAG"
echo "Dependency cache tag: $DEPS_CACHE_TAG"

DEPS_EXISTS=false
DEPS_CACHE_EXISTS=false
if docker_image_exists "$DEPS_TAG"; then DEPS_EXISTS=true; fi
if docker_image_exists "$DEPS_CACHE_TAG"; then DEPS_CACHE_EXISTS=true; fi

if [[ "$REBUILD_BASE" == "true" || "$DEPS_EXISTS" != "true" ]]; then
  if [[ "$REBUILD_BASE" == "true" ]]; then
    echo "Rebuilding dependency image without cache..."
  else
    echo "Dependency image not found. Building $DEPS_TAG..."
  fi

  DEPS_BUILD_ARGS=(buildx build --load)
  if [[ "$REBUILD_BASE" == "true" ]]; then
    DEPS_BUILD_ARGS+=(--no-cache)
  else
    DEPS_BUILD_ARGS+=(
      --cache-from "type=local,src=$DEPS_CACHE_PATH"
      --cache-from "type=local,src=$FINAL_CACHE_PATH"
    )
    if [[ "$DEPS_CACHE_EXISTS" == "true" ]]; then
      DEPS_BUILD_ARGS+=(--cache-from "$DEPS_CACHE_TAG")
    fi
  fi
  DEPS_BUILD_ARGS+=(
    --target "$DEPS_TARGET"
    -t "$DEPS_TAG"
    -t "$DEPS_CACHE_TAG"
    -f "$DOCKERFILE_PATH"
    "$BUILD_CONTEXT"
  )
  if [[ "$BUILDX_CACHE_EXPORT_SUPPORTED" == "true" ]]; then
    DEPS_BUILD_ARGS+=(
      --cache-to "type=local,dest=$DEPS_CACHE_PATH_NEW,mode=min"
    )
  fi
  docker "${DEPS_BUILD_ARGS[@]}"
  promote_local_cache "$DEPS_CACHE_PATH" "$DEPS_CACHE_PATH_NEW"
else
  echo "Reusing cached dependency image: $DEPS_TAG"
  docker tag "$DEPS_TAG" "$DEPS_CACHE_TAG"
fi

DOCKER_ARGS=(buildx build --load)
if [[ "$REBUILD_BASE" == "true" ]]; then
  DOCKER_ARGS+=(--no-cache)
fi
DOCKER_ARGS+=(
  --cache-from "type=local,src=$DEPS_CACHE_PATH"
  --cache-from "type=local,src=$FINAL_CACHE_PATH"
  --cache-from "$DEPS_CACHE_TAG"
  --build-arg "${DEPS_IMAGE_ARG}=$DEPS_TAG"
  --target "$FULL_TARGET"
  -t "$IMAGE_TAG"
  -t "$LATEST_IMAGE_TAG"
  -f "$DOCKERFILE_PATH"
  "$BUILD_CONTEXT"
)
if [[ "$BUILDX_CACHE_EXPORT_SUPPORTED" == "true" ]]; then
  DOCKER_ARGS+=(
    --cache-to "type=local,dest=$FINAL_CACHE_PATH_NEW,mode=min"
  )
fi
docker "${DOCKER_ARGS[@]}"
promote_local_cache "$FINAL_CACHE_PATH" "$FINAL_CACHE_PATH_NEW"

echo "Image built: $IMAGE_TAG"

ENV_FILE="$DOCKER_ROOT/.env"
case "$BACKEND" in
  cuda13) IMAGE_ENV_KEY="GA_AI_CUDA_IMAGE" ;;
  rocm) IMAGE_ENV_KEY="GA_AI_ROCM_IMAGE" ;;
  slim) IMAGE_ENV_KEY="GA_AI_SLIM_IMAGE" ;;
  vulkan) IMAGE_ENV_KEY="GA_AI_VULKAN_IMAGE" ;;
  *) IMAGE_ENV_KEY="GA_AI_CPU_IMAGE" ;;
esac
ENV_LINE="$IMAGE_ENV_KEY=$LATEST_IMAGE_TAG"

if [[ -f "$ENV_FILE" ]]; then
  if grep -qE "^[[:space:]]*${IMAGE_ENV_KEY}=" "$ENV_FILE"; then
    awk -v key="$IMAGE_ENV_KEY" -v line="$ENV_LINE" '
      BEGIN { replaced = 0 }
      $0 ~ "^[[:space:]]*" key "=" && replaced == 0 { print line; replaced = 1; next }
      { print }
      END { if (replaced == 0) print line }
    ' "$ENV_FILE" > "${ENV_FILE}.tmp"
    mv "${ENV_FILE}.tmp" "$ENV_FILE"
  else
    printf "%s\n" "$ENV_LINE" >> "$ENV_FILE"
  fi
else
  printf "%s\n" "$ENV_LINE" > "$ENV_FILE"
fi
echo "Wrote $ENV_LINE to $ENV_FILE"

echo
echo "============================================"
echo "  Build complete: $IMAGE_TAG"
echo "  Latest alias:   $LATEST_IMAGE_TAG"
echo "============================================"
