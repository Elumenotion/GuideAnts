#!/usr/bin/env bash
# Write installer/docker/images.env with release-tag pins.
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

pin_line() {
  local key="$1" package="$2"
  printf '%s=%s/%s/%s:%s\n' "$key" "$REGISTRY" "$OWNER" "$package" "$RELEASE_TAG"
}

mkdir -p "$(dirname "$OUT_FILE")"

{
  cat <<EOT
# Generated for GuideAnts release $RELEASE_TAG
# Pins are release tags. Update detection compares local image digest to :$CHANNEL.
# The file is stable; updates are tracked locally via docker tags, not file rewrites.
GA_RELEASE_TAG=$RELEASE_TAG
GA_UPDATE_CHANNEL=$CHANNEL

EOT
  pin_line GA_WEBAPI_UI_MSSQL_GHCR_IMAGE guideants-webapi-ui-mssql
  pin_line GA_WEBAPI_UI_SLIM_GHCR_IMAGE guideants-webapi-ui-slim
  pin_line GA_MSSQL_IMAGE mssql2025-express-fts
  pin_line GA_AI_SLIM_GHCR_IMAGE guideants-ai-slim
  pin_line GA_AI_CPU_GHCR_IMAGE guideants-ai-cpu
  pin_line GA_AI_CUDA_GHCR_IMAGE guideants-ai-cuda13
  pin_line GA_AI_ROCM_GHCR_IMAGE guideants-ai-rocm
  pin_line GA_AI_VULKAN_GHCR_IMAGE guideants-ai-vulkan
  pin_line GA_AI_SPARK_IMAGE guideants-ai-spark
  pin_line GA_PLANTUML_GHCR_IMAGE guideants-plantuml
  pin_line GA_SEARXNG_GHCR_IMAGE guideants-searxng
} > "$OUT_FILE"

echo "Wrote $OUT_FILE" >&2
