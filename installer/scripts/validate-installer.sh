#!/usr/bin/env bash
# Validates installer bash syntax + compose fragment merges.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOCKER_DIR="$ROOT_DIR/docker"

sh_files=(
  "$ROOT_DIR/guideants.sh"
  "$ROOT_DIR/stop_guideants.sh"
  "$ROOT_DIR/scripts/installer-wizard.sh"
  "$ROOT_DIR/scripts/guideants-host-mount.sh"
  "$ROOT_DIR/scripts/rocm-runtime-compose.sh"
  "$ROOT_DIR/scripts/rocm-probe.sh"
  "$ROOT_DIR/scripts/install-rocm-wsl.sh"
)

for f in "${sh_files[@]}"; do
  [[ -f "$f" ]] || { echo "FAIL missing: $f" >&2; exit 1; }
  bash -n "$f"
  echo "PASS bash -n: $(basename "$f")"
done

if ! command -v docker >/dev/null 2>&1; then
  echo "SKIP compose config: docker not found"
  exit 0
fi

SEARXNG_SETTINGS="$DOCKER_DIR/volumes/searxng/config/settings.yml"
SEARXNG_LIMITER="$DOCKER_DIR/volumes/searxng/config/limiter.toml"
[[ -f "$SEARXNG_SETTINGS" ]] || { echo "FAIL missing SearXNG settings seed: $SEARXNG_SETTINGS" >&2; exit 1; }
[[ -f "$SEARXNG_LIMITER" ]] || { echo "FAIL missing SearXNG limiter seed: $SEARXNG_LIMITER" >&2; exit 1; }
echo "PASS searxng config seeds present"

combos=(
  "compose/base.yml compose/core-bundled.yml"
  "compose/base.yml compose/core-separate.yml"
  "compose/base.yml compose/core-bundled.yml compose/ai-slim.yml"
  "compose/base.yml compose/core-separate.yml compose/ai-cuda13.yml compose/docling-cuda.yml compose/documentserver.yml compose/plantuml.yml compose/searxng.yml"
)

cd "$DOCKER_DIR"
for combo in "${combos[@]}"; do
  # shellcheck disable=SC2206
  files=($combo)
  args=(--project-directory "$DOCKER_DIR")
  for f in "${files[@]}"; do args+=(-f "$f"); done
  docker compose "${args[@]}" --env-file .env config --quiet
  echo "PASS compose: ${files[-1]}"
done

rendered="$(docker compose --project-directory "$DOCKER_DIR" \
  -f compose/base.yml -f compose/core-bundled.yml -f compose/searxng.yml \
  --env-file .env config)"
expected="$DOCKER_DIR/volumes/searxng/config"
wrong="$DOCKER_DIR/compose/volumes/searxng/config"
case "$rendered" in
  *"$expected"*) ;;
  *) echo "FAIL searxng bind must resolve under docker/volumes/searxng/config" >&2; exit 1 ;;
esac
case "$rendered" in
  *"$wrong"*) echo "FAIL searxng bind incorrectly resolves under compose/volumes/" >&2; exit 1 ;;
esac
echo "PASS searxng bind path resolves to docker/volumes/searxng"

echo "All installer validation checks passed."
