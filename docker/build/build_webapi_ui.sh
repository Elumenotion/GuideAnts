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

get_compose_context_for_container() {
  local container_name="$1"
  local docker_root="$2"
  local repo_root="$3"
  local project_name="guideants"
  local config_files=""
  local label_json cfg resolved state_file compose_file override_file docker_directory compose_root
  local -a resolved_files=()

  if ! docker inspect "$container_name" >/dev/null 2>&1; then
    echo "Container '$container_name' was not found. Start the stack before rebuilding with recreate enabled, or pass --no-recreate." >&2
    return 1
  fi

  project_name="$(docker inspect -f '{{ index .Config.Labels "com.docker.compose.project" }}' "$container_name" 2>/dev/null || true)"
  config_files="$(docker inspect -f '{{ index .Config.Labels "com.docker.compose.project.config_files" }}' "$container_name" 2>/dev/null || true)"
  if [[ -z "$project_name" ]]; then
    project_name="guideants"
  fi

  if [[ -z "$config_files" ]]; then
    while IFS='|' read -r name _status cfg; do
      [[ "$name" == "$project_name" ]] || continue
      config_files="$cfg"
      break
    done < <(docker compose ls --format '{{.Name}}|{{.Status}}|{{.ConfigFiles}}')
  fi

  if [[ -z "$config_files" ]]; then
    state_file="$repo_root/.installer_state.env"
    if [[ -f "$state_file" ]]; then
      compose_file=""
      override_file=""
      docker_directory="docker"
      while IFS= read -r line; do
        line="${line%%$'\r'}"
        [[ -z "$line" || "$line" =~ ^[[:space:]]*# ]] && continue
        case "$line" in
          COMPOSE_FILE=*) compose_file="${line#COMPOSE_FILE=}" ;;
          HOST_MOUNT_OVERRIDE_FILE=*) override_file="${line#HOST_MOUNT_OVERRIDE_FILE=}" ;;
          DOCKER_DIRECTORY=*) docker_directory="${line#DOCKER_DIRECTORY=}" ;;
        esac
      done < "$state_file"
      if [[ -n "$compose_file" ]]; then
        if [[ "$docker_directory" = /* ]]; then
          compose_root="$docker_directory"
        else
          compose_root="$repo_root/$docker_directory"
        fi
        config_files="$compose_root/$compose_file"
        if [[ -n "$override_file" ]]; then
          config_files="$config_files,$compose_root/$override_file"
        fi
      fi
    fi
  fi

  IFS=',' read -ra files <<< "${config_files:-}"
  for cfg in "${files[@]}"; do
    cfg="$(echo "$cfg" | xargs)"
    [[ -z "$cfg" ]] && continue
    if [[ "$cfg" = /* ]]; then
      resolved="$cfg"
    else
      resolved="$docker_root/$cfg"
    fi
    if [[ -f "$resolved" ]]; then
      resolved_files+=("$resolved")
    else
      echo "Warning: Skipping missing compose file '$resolved'." >&2
    fi
  done

  if [[ ${#resolved_files[@]} -eq 0 ]]; then
    echo "Could not resolve any compose config files for container '$container_name'." >&2
    return 1
  fi

  COMPOSE_PROJECT_NAME="$project_name"
  COMPOSE_FILE_ARGS=()
  for resolved in "${resolved_files[@]}"; do
    COMPOSE_FILE_ARGS+=(-f "$resolved")
  done
}

recreate_compose_service_with_image() {
  local docker_root="$1"
  local service_name="$2"
  local image_tag="$3"
  local override_path="$docker_root/.build-webapi-ui-image.override.yml"

  cat > "$override_path" <<EOF
services:
  ${service_name}:
    image: ${image_tag}
    pull_policy: never
EOF

  (
    cd "$docker_root"
    compose_args=(compose -p "$COMPOSE_PROJECT_NAME")
    compose_args+=("${COMPOSE_FILE_ARGS[@]}")
    compose_args+=(-f "$override_path" up -d --no-deps --force-recreate --pull never "$service_name")
    docker "${compose_args[@]}"
  )
  local exit_code=$?
  rm -f "$override_path"
  return "$exit_code"
}

case "$FLAVOR" in
  Full|full)
    FLAVOR="Full"
    DOCKER_TARGET="runtime"
    IMAGE_REPOSITORY="guideants-webapi-ui"
    IMAGE_ENV_KEY="GA_WEBAPI_UI_IMAGE"
    SERVICE_NAME="guideants-webapi-ui"
    CONTAINER_NAME="guideants-webapi-ui"
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
    COMPOSE_FILE_NAME=""
    USE_RUNNING_COMPOSE_STACK=false
    USE_COMPOSE_FILE=false
    ;;
  Mssql|mssql)
    FLAVOR="Mssql"
    DOCKER_TARGET="runtime-mssql"
    IMAGE_REPOSITORY="guideants-webapi-ui-mssql"
    IMAGE_ENV_KEY="GA_WEBAPI_UI_MSSQL_IMAGE"
    SERVICE_NAME="guideants-webapi-ui-mssql"
    CONTAINER_NAME="guideants-webapi-ui-mssql"
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

# Build a unique tag per build, and also maintain a stable latest tag.
JULIAN_DAY="$(date +%y%j)"
TIME_STAMP="$(date +%H%M)"
IMAGE_TAG="${IMAGE_REPOSITORY}:${JULIAN_DAY}.${TIME_STAMP}"
LATEST_IMAGE_TAG="${IMAGE_REPOSITORY}:latest"

echo "============================================"
echo "  Building GuideAnts API + Browser UI ($FLAVOR)"
echo "============================================"
echo "Image tag: $IMAGE_TAG"
echo "Latest alias: $LATEST_IMAGE_TAG"
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
  -t "$LATEST_IMAGE_TAG"
  -f "$DOCKERFILE_PATH"
  "$BUILD_CONTEXT"
)
docker "${DOCKER_ARGS[@]}"

ENV_FILE="$DOCKER_ROOT/.env"
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

if ! grep -q "^${IMAGE_ENV_KEY}=${LATEST_IMAGE_TAG}\$" "$ENV_FILE"; then
  echo "Failed to persist ${IMAGE_ENV_KEY}=$LATEST_IMAGE_TAG to $ENV_FILE" >&2
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
  echo "Recreating ${CONTAINER_NAME:-$SERVICE_NAME} to apply image $LATEST_IMAGE_TAG..."
  if [[ "$USE_RUNNING_COMPOSE_STACK" == "true" ]]; then
    get_compose_context_for_container "$CONTAINER_NAME" "$DOCKER_ROOT" "$REPO_ROOT"
    recreate_compose_service_with_image "$DOCKER_ROOT" "$SERVICE_NAME" "$LATEST_IMAGE_TAG"
  else
    COMPOSE_PROJECT_NAME="guideants"
    COMPOSE_FILE_ARGS=(-f "$COMPOSE_FILE")
    recreate_compose_service_with_image "$DOCKER_ROOT" "$SERVICE_NAME" "$LATEST_IMAGE_TAG"
  fi
  echo "Recreated ${CONTAINER_NAME:-$SERVICE_NAME} with image $LATEST_IMAGE_TAG"
elif [[ "$NO_RECREATE" == "true" ]]; then
  echo "Skipping compose service recreate (--no-recreate)."
  echo "To apply this image to an existing container, rerun without --no-recreate while container '${CONTAINER_NAME:-$SERVICE_NAME}' exists."
else
  echo "Image was built but not applied to a compose service. The standalone guideants-webapi-ui-slim image is orthogonal to docker-compose.slim.yml."
fi
