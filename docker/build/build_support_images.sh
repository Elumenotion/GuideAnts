#!/usr/bin/env bash
set -euo pipefail

REBUILD_BASE=false

usage() {
  cat <<'EOF'
Usage: build_support_images.sh [options]

Options:
  --rebuild-base         Rebuild support images without cache where supported
  -h, --help             Show help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rebuild-base)
      REBUILD_BASE=true
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$DOCKER_ROOT/.." && pwd)"
SERVER_PATH="$REPO_ROOT/src/server"

export DOCKER_BUILDKIT=1

docker_build_args=()
if [[ "$REBUILD_BASE" == "true" ]]; then
  docker_build_args+=(--no-cache)
fi

echo "============================================"
echo "  Building GuideAnts Support Images"
echo "============================================"
echo "Rebuild base:  $REBUILD_BASE"
echo

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

PLANTUML_CONTAINER_PATH="$SCRIPT_DIR/Sandboxes/PlantUml"
PLANTUML_SCRIPT_AGENT_PATH="$PLANTUML_CONTAINER_PATH/ScriptExecutionAgent"
if [[ -d "$PUBLISH_OUTPUT" && -d "$PLANTUML_CONTAINER_PATH" ]]; then
  rm -rf "$PLANTUML_SCRIPT_AGENT_PATH"
  cp -R "$PUBLISH_OUTPUT" "$PLANTUML_SCRIPT_AGENT_PATH"
  echo "Copied ScriptExecutionAgent to PlantUML container directory"
fi

timestamp="$(date +%Y%m%d%H%M%S)"
docker build "${docker_build_args[@]}" -t plantuml-1.2025.2 -f "$SCRIPT_DIR/Sandboxes/PlantUml/dockerfile" --build-arg "SCRIPT_AGENT_VERSION=$timestamp" "$SCRIPT_DIR/Sandboxes/PlantUml"

MSSQL_BUILD_CONTEXT="$SCRIPT_DIR/mssql-fts"
MSSQL_DOCKERFILE_PATH="$MSSQL_BUILD_CONTEXT/Dockerfile"
[[ -f "$MSSQL_DOCKERFILE_PATH" ]] || { echo "MSSQL Dockerfile not found at $MSSQL_DOCKERFILE_PATH" >&2; exit 1; }
echo "Building mssql image: mssql2025-express-fts"
docker build "${docker_build_args[@]}" -t mssql2025-express-fts -f "$MSSQL_DOCKERFILE_PATH" --build-arg MSSQL_PID=Express "$MSSQL_BUILD_CONTEXT"

SEARXNG_DOCKERFILE_PATH="$SCRIPT_DIR/searxng/Dockerfile"
[[ -f "$SEARXNG_DOCKERFILE_PATH" ]] || { echo "SearXNG Dockerfile not found at $SEARXNG_DOCKERFILE_PATH" >&2; exit 1; }
echo "Building searxng image: guideants-searxng:latest"
docker build "${docker_build_args[@]}" -t guideants-searxng:latest -f "$SEARXNG_DOCKERFILE_PATH" "$REPO_ROOT"

WEBAPI_UI_BUILD_SCRIPT="$SCRIPT_DIR/build_webapi_ui.sh"
[[ -f "$WEBAPI_UI_BUILD_SCRIPT" ]] || { echo "WebAPI+UI build script not found at $WEBAPI_UI_BUILD_SCRIPT" >&2; exit 1; }
echo "Building WebAPI+UI image via build_webapi_ui.sh"
if [[ "$REBUILD_BASE" == "true" ]]; then
  bash "$WEBAPI_UI_BUILD_SCRIPT" --no-cache --no-recreate
else
  bash "$WEBAPI_UI_BUILD_SCRIPT" --no-recreate
fi

echo
echo "============================================"
echo "  Support image build complete"
echo "============================================"
