#!/usr/bin/env bash
set -euo pipefail

FLAVOR="Full"
NO_CACHE=false
NO_RECREATE=false
USE_APP_BUILD_CACHE=true

usage() {
  cat <<'EOF'
Usage: build_webapi_ui.sh [options]

Options:
  --flavor <Full|Slim|Mssql>  Build flavor (default: Full)
  --no-cache                  Build without cache
  --no-recreate               Skip docker compose service recreate
  --use-app-build-cache       Allow docker cache for api-build stage (default)
  --no-app-build-cache        Rebuild api-build stage each run
  -h, --help                  Show help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --flavor)
      FLAVOR="${2:-}"
      shift 2
      ;;
    --no-cache)
      NO_CACHE=true
      shift
      ;;
    --no-recreate)
      NO_RECREATE=true
      shift
      ;;
    --use-app-build-cache)
      USE_APP_BUILD_CACHE=true
      shift
      ;;
    --no-app-build-cache)
      USE_APP_BUILD_CACHE=false
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
BUILD_CONTEXT="$REPO_ROOT"
DOCKERFILE_PATH="$SCRIPT_DIR/webapi-ui/Dockerfile"
CLIENT_ROOT="$REPO_ROOT/src/client"
CLIENT_NODE_MODULES="$CLIENT_ROOT/node_modules"
CLIENT_DIST_BROWSER="$CLIENT_ROOT/dist-browser"

get_running_compose_file_args() {
  local docker_root="$1"
  local project_name="${2:-guideants}"
  local line name status config_files resolved
  local found=false

  COMPOSE_FILE_ARGS=()

  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    IFS='|' read -r name status config_files <<< "$line"
    [[ "$name" == "$project_name" ]] || continue
    [[ "$status" =~ ^running ]] || continue
    found=true

    IFS=',' read -ra files <<< "${config_files:-}"
    for cfg in "${files[@]}"; do
      cfg="$(echo "$cfg" | xargs)"
      [[ -z "$cfg" ]] && continue
      if [[ "$cfg" = /* ]]; then
        resolved="$cfg"
      else
        resolved="$docker_root/$cfg"
      fi
      if [[ ! -f "$resolved" ]]; then
        echo "Warning: Running compose project references missing config file '$resolved'; skipping it." >&2
        continue
      fi
      COMPOSE_FILE_ARGS+=(-f "$resolved")
    done
    break
  done < <(docker compose ls --format '{{.Name}}|{{.Status}}|{{.ConfigFiles}}')

  if [[ "$found" != "true" ]]; then
    echo "No running Docker Compose project named '$project_name' was found. Start the stack before rebuilding with recreate enabled, or pass --no-recreate." >&2
    return 1
  fi

  if [[ ${#COMPOSE_FILE_ARGS[@]} -eq 0 ]]; then
    echo "None of the config files for running Docker Compose project '$project_name' exist on disk." >&2
    return 1
  fi
}

case "$FLAVOR" in
  Full|full)
    FLAVOR="Full"
    DOCKER_TARGET="runtime"
    IMAGE_REPOSITORY="guideants-webapi-ui"
    IMAGE_ENV_KEY="GA_WEBAPI_UI_IMAGE"
    SERVICE_NAME="guideants-webapi-ui"
    COMPOSE_FILE_NAME=""
    USE_RUNNING_COMPOSE_STACK=true
    USE_COMPOSE_FILE=false
    ;;
  Slim|slim)
    FLAVOR="Slim"
    DOCKER_TARGET="runtime-slim"
    IMAGE_REPOSITORY="guideants-webapi-ui-slim"
    IMAGE_ENV_KEY="GA_WEBAPI_UI_SLIM_IMAGE"
    SERVICE_NAME="guideants-webapi-ui-slim"
    COMPOSE_FILE_NAME="docker-compose.slim.yml"
    USE_RUNNING_COMPOSE_STACK=false
    USE_COMPOSE_FILE=true
    ;;
  Mssql|mssql)
    FLAVOR="Mssql"
    DOCKER_TARGET="runtime-mssql"
    IMAGE_REPOSITORY="guideants-webapi-ui-mssql"
    IMAGE_ENV_KEY="GA_WEBAPI_UI_MSSQL_IMAGE"
    SERVICE_NAME="guideants-webapi-ui-mssql"
    COMPOSE_FILE_NAME="docker-compose.mssql.yml"
    USE_RUNNING_COMPOSE_STACK=false
    USE_COMPOSE_FILE=true
    ;;
  *)
    echo "Invalid --flavor '$FLAVOR' (expected Full|Slim|Mssql)" >&2
    exit 1
    ;;
esac

[[ -f "$DOCKERFILE_PATH" ]] || { echo "Dockerfile not found at $DOCKERFILE_PATH" >&2; exit 1; }
[[ -f "$CLIENT_ROOT/package.json" ]] || { echo "Client package.json not found at $CLIENT_ROOT/package.json" >&2; exit 1; }

JULIAN_DAY="$(date +%y%j)"
TIME_STAMP="$(date +%H%M)"
IMAGE_TAG="${IMAGE_REPOSITORY}:${JULIAN_DAY}.${TIME_STAMP}"

echo "============================================"
echo "  Building GuideAnts API + Browser UI ($FLAVOR)"
echo "============================================"
echo "Image tag: $IMAGE_TAG"
echo "Target:    $DOCKER_TARGET"
echo "No cache:  $NO_CACHE"
echo "App cache: $USE_APP_BUILD_CACHE"
if [[ "$NO_RECREATE" == "true" ]]; then
  echo "Recreate:  false"
else
  echo "Recreate:  true"
fi
echo

echo "Building browser UI locally (src/client)..."
(
  cd "$CLIENT_ROOT"
  if [[ ! -d "$CLIENT_NODE_MODULES" ]]; then
    echo "Installing client dependencies (npm ci)..."
    npm ci
  fi
  npm run browser:build:docker
)

[[ -d "$CLIENT_DIST_BROWSER" ]] || { echo "Expected browser build output was not found at $CLIENT_DIST_BROWSER" >&2; exit 1; }
echo "Browser UI build complete: $CLIENT_DIST_BROWSER"
echo

DOCKER_ARGS=(build)
if [[ "$NO_CACHE" == "true" ]]; then
  DOCKER_ARGS+=(--no-cache)
elif [[ "$USE_APP_BUILD_CACHE" != "true" ]]; then
  DOCKER_ARGS+=(--no-cache-filter api-build)
fi
DOCKER_ARGS+=(
  --target "$DOCKER_TARGET"
  -t "$IMAGE_TAG"
  -f "$DOCKERFILE_PATH"
  "$BUILD_CONTEXT"
)
docker "${DOCKER_ARGS[@]}"

ENV_FILE="$DOCKER_ROOT/.env"
declare -A ENV_MAP=()
declare -a ENV_ORDER=()

if [[ -f "$ENV_FILE" ]]; then
  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ -z "${line//[[:space:]]/}" ]] && continue
    [[ "$line" =~ ^[[:space:]]*# ]] && continue
    if [[ "$line" =~ ^[[:space:]]*([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]]; then
      key="${BASH_REMATCH[1]}"
      value="${BASH_REMATCH[2]}"
      if [[ -z "${ENV_MAP[$key]+x}" ]]; then
        ENV_ORDER+=("$key")
      fi
      ENV_MAP["$key"]="$value"
    fi
  done < "$ENV_FILE"
fi

if [[ -z "${ENV_MAP[$IMAGE_ENV_KEY]+x}" ]]; then
  ENV_ORDER+=("$IMAGE_ENV_KEY")
fi
ENV_MAP["$IMAGE_ENV_KEY"]="$IMAGE_TAG"

{
  for key in "${ENV_ORDER[@]}"; do
    printf "%s=%s\n" "$key" "${ENV_MAP[$key]}"
  done
} > "$ENV_FILE"

if ! grep -q "^${IMAGE_ENV_KEY}=${IMAGE_TAG}\$" "$ENV_FILE"; then
  echo "Failed to persist ${IMAGE_ENV_KEY}=$IMAGE_TAG to $ENV_FILE" >&2
  exit 1
fi

echo "Image built: $IMAGE_TAG"
echo "Wrote ${IMAGE_ENV_KEY}=$IMAGE_TAG to $ENV_FILE"
echo

COMPOSE_FILE=""
if [[ -n "$COMPOSE_FILE_NAME" ]]; then
  COMPOSE_FILE="$DOCKER_ROOT/$COMPOSE_FILE_NAME"
fi

if [[ "$NO_RECREATE" != "true" && ( "$USE_RUNNING_COMPOSE_STACK" == "true" || -f "$COMPOSE_FILE" ) ]]; then
  echo "Recreating $SERVICE_NAME to apply the new image tag..."
  (
    cd "$DOCKER_ROOT"
    compose_args=(compose)
    if [[ "$USE_RUNNING_COMPOSE_STACK" == "true" ]]; then
      get_running_compose_file_args "$DOCKER_ROOT" "guideants"
      compose_args+=("${COMPOSE_FILE_ARGS[@]}")
    elif [[ "$USE_COMPOSE_FILE" == "true" ]]; then
      compose_args+=(-f "$COMPOSE_FILE_NAME")
    fi
    compose_args+=(up -d --no-deps --force-recreate "$SERVICE_NAME")
    if ! docker "${compose_args[@]}"; then
      echo "Failed to recreate $SERVICE_NAME." >&2
      if [[ "$USE_RUNNING_COMPOSE_STACK" == "true" ]]; then
        echo "Use: rerun this script after confirming the 'guideants' compose stack is running and its config files exist." >&2
      elif [[ "$USE_COMPOSE_FILE" == "true" ]]; then
        echo "Use: docker compose -f $COMPOSE_FILE_NAME up -d --no-deps --force-recreate $SERVICE_NAME" >&2
      fi
      exit 1
    fi
  )
  echo "Recreated $SERVICE_NAME with image $IMAGE_TAG"
elif [[ "$NO_RECREATE" == "true" ]]; then
  echo "Skipping compose service recreate (--no-recreate)."
  echo "To apply this image to an existing container, run:"
  if [[ "$USE_RUNNING_COMPOSE_STACK" == "true" ]]; then
    echo "docker compose <running stack config files> up -d --no-deps --force-recreate $SERVICE_NAME"
  elif [[ "$USE_COMPOSE_FILE" == "true" ]]; then
    echo "docker compose -f $COMPOSE_FILE_NAME up -d --no-deps --force-recreate $SERVICE_NAME"
  fi
else
  echo "$COMPOSE_FILE_NAME not found at $COMPOSE_FILE; image was built but not applied to a running service."
fi
