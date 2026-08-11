#!/usr/bin/env bash
set -Eeuo pipefail

readonly RUN_USER=guideants
readonly RUN_GROUP=guideants
declare -a children=()

die() {
  echo "comfyui-video: $*" >&2
  exit 1
}

require_secret() {
  local name="$1"
  [[ -n "${!name:-}" ]] || die "$name must be set"
}

terminate() {
  local pid
  for pid in "${children[@]:-}"; do
    kill -TERM "$pid" 2>/dev/null || true
  done
  wait || true
}
trap terminate TERM INT EXIT

[[ "$(id -u)" -eq 0 ]] || die "entrypoint initialization requires root"
require_secret SCRIPT_EXECUTION_AGENT_TOKEN
require_secret SCRIPT_EXECUTION_ADMIN_TOKEN
require_secret VIDEO_ADMIN_TOKEN
[[ -n "${CUDA_VISIBLE_DEVICES:-}" ]] || die "CUDA_VISIBLE_DEVICES must select exactly one GPU"
[[ "$CUDA_VISIBLE_DEVICES" != *,* ]] || die "CUDA_VISIBLE_DEVICES must select one GPU, not a list"
install -d -o "$RUN_USER" -g "$RUN_GROUP" \
  /app/ContentFiles /cache /models /run/nginx \
  /var/lib/guideants/script-agent-admin /var/lib/guideants/script-agent-admin/scopes \
  /var/lib/guideants/comfyui-video /var/lib/guideants/comfyui-video/jobs
chown "$RUN_USER:$RUN_GROUP" /cache /models /var/lib/guideants/comfyui-video \
  /var/lib/guideants/comfyui-video/jobs

rm -rf /opt/ComfyUI/models
ln -s /models /opt/ComfyUI/models

gosu "$RUN_USER:$RUN_GROUP" python /opt/guideants/comfyui-video/scripts/verify-install.py

gosu "$RUN_USER:$RUN_GROUP" \
  python /opt/ComfyUI/main.py \
    --listen 127.0.0.1 \
    --port 8188 \
    --disable-auto-launch &
children+=("$!")

for _ in $(seq 1 60); do
  if curl --fail --silent http://127.0.0.1:8188/object_info >/dev/null; then
    break
  fi
  sleep 1
done
curl --fail --silent http://127.0.0.1:8188/object_info >/dev/null \
  || die "ComfyUI did not become healthy on loopback"

gosu "$RUN_USER:$RUN_GROUP" \
  python /opt/guideants/comfyui-video/scripts/smoke-workflow.py

[[ -f /app/adapter/guideants_video_adapter/app.py ]] || die "video adapter payload is missing"
gosu "$RUN_USER:$RUN_GROUP" \
  uvicorn guideants_video_adapter.app:APP \
    --app-dir /app/adapter --host 127.0.0.1 --port 8190 &
children+=("$!")

dotnet /app/script-agent/ScriptExecutionAgent.dll \
    --urls http://127.0.0.1:8081 &
children+=("$!")

nginx -g "daemon off;" &
children+=("$!")

set +e
wait -n "${children[@]}"
status=$?
set -e
die "a managed process exited with status $status"
