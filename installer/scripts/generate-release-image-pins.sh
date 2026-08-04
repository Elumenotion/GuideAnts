#!/usr/bin/env bash
# Resolve current GHCR channel digests and write installer/docker/images.env.
#
# Usage:
#   ./installer/scripts/generate-release-image-pins.sh <release-tag> [owner] [channel]
#
# Example:
#   ./installer/scripts/generate-release-image-pins.sh v1.2.3 elumenotion main

set -euo pipefail

RELEASE_TAG="${1:-}"
OWNER="${2:-elumenotion}"
CHANNEL="${3:-main}"
REGISTRY="${GA_REGISTRY:-ghcr.io}"

[[ -n "$RELEASE_TAG" ]] || {
  echo "usage: $0 <release-tag> [owner] [channel]" >&2
  exit 1
}

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_FILE="${GA_IMAGES_ENV_OUT:-$ROOT_DIR/installer/docker/images.env}"
OWNER="$(printf '%s' "$OWNER" | tr '[:upper:]' '[:lower:]')"

resolve_digest() {
  local image_ref="$1" out
  if docker buildx version >/dev/null 2>&1; then
    out="$(docker buildx imagetools inspect "$image_ref" 2>/dev/null || true)"
    if [[ "$out" =~ Digest:[[:space:]]+(sha256:[a-f0-9]+) ]]; then
      printf '%s\n' "${BASH_REMATCH[1]}"
      return 0
    fi
  fi
  out="$(docker manifest inspect -v "$image_ref" 2>/dev/null || true)"
  if [[ "$out" =~ \"digest\"[[:space:]]*:[[:space:]]*\"(sha256:[a-f0-9]+)\" ]]; then
    printf '%s\n' "${BASH_REMATCH[1]}"
    return 0
  fi
  return 1
}

pin_line() {
  local key="$1" package="$2" ref digest
  ref="$REGISTRY/$OWNER/$package:$CHANNEL"
  echo "Resolving $ref ..." >&2
  digest="$(resolve_digest "$ref")" || {
    echo "error: could not resolve digest for $ref" >&2
    exit 1
  }
  printf '%s=%s/%s/%s@%s\n' "$key" "$REGISTRY" "$OWNER" "$package" "$digest"
}

mkdir -p "$(dirname "$OUT_FILE")"

{
  cat <<EOF
# Generated for GuideAnts release $RELEASE_TAG
# Pins are immutable digests. Update detection compares local digests to :$CHANNEL.
# The installer may rewrite this file when the user accepts an image update.
GA_RELEASE_TAG=$RELEASE_TAG
GA_UPDATE_CHANNEL=$CHANNEL

EOF
  pin_line GA_WEBAPI_UI_MSSQL_GHCR_IMAGE guideants-webapi-ui-mssql
  pin_line GA_WEBAPI_UI_SLIM_GHCR_IMAGE guideants-webapi-ui-slim
  pin_line GA_MSSQL_IMAGE mssql2025-express-fts
  pin_line GA_AI_SLIM_GHCR_IMAGE guideants-ai-slim
  pin_line GA_AI_CPU_GHCR_IMAGE guideants-ai-cpu
  pin_line GA_AI_CUDA_GHCR_IMAGE guideants-ai-cuda13
  pin_line GA_AI_ROCM_GHCR_IMAGE guideants-ai-rocm
  pin_line GA_AI_VULKAN_GHCR_IMAGE guideants-ai-vulkan
  pin_line GA_PLANTUML_GHCR_IMAGE guideants-plantuml
  pin_line GA_SEARXNG_GHCR_IMAGE guideants-searxng
} > "$OUT_FILE"

echo "Wrote $OUT_FILE" >&2
