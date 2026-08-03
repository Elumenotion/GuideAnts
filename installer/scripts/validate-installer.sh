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
  args=()
  for f in "${files[@]}"; do args+=(-f "$f"); done
  docker compose "${args[@]}" --env-file .env config --quiet
  echo "PASS compose: ${files[-1]}"
done

echo "All installer validation checks passed."
