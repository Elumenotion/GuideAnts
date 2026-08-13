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

video_gpu_backend="${VIDEO_GPU_BACKEND:-cuda13}"
case "$video_gpu_backend" in
  cuda13)
    [[ -n "${CUDA_VISIBLE_DEVICES:-}" ]] || die "CUDA_VISIBLE_DEVICES must select exactly one GPU"
    [[ "$CUDA_VISIBLE_DEVICES" != *,* ]] || die "CUDA_VISIBLE_DEVICES must select one GPU, not a list"
    ;;
  rocm)
    [[ -n "${HIP_VISIBLE_DEVICES:-}" ]] || die "HIP_VISIBLE_DEVICES must select exactly one GPU"
    [[ "$HIP_VISIBLE_DEVICES" != *,* ]] || die "HIP_VISIBLE_DEVICES must select one GPU, not a list"
    export HSA_ENABLE_SDMA="${HSA_ENABLE_SDMA:-0}"
    export HSA_USE_SVM="${HSA_USE_SVM:-0}"
    export PYTORCH_HIP_ALLOC_CONF="${PYTORCH_HIP_ALLOC_CONF:-backend:native,expandable_segments:True,garbage_collection_threshold:0.7,max_split_size_mb:256}"
    export TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL="${TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL:-1}"
    export TORCHINDUCTOR_CACHE_DIR="${TORCHINDUCTOR_CACHE_DIR:-/cache/torch_inductor_comfy}"
    export TORCHINDUCTOR_FX_GRAPH_CACHE="${TORCHINDUCTOR_FX_GRAPH_CACHE:-1}"
    ;;
  *)
    die "unsupported VIDEO_GPU_BACKEND: $video_gpu_backend (expected cuda13 or rocm)"
    ;;
esac
install -d -o "$RUN_USER" -g "$RUN_GROUP" \
  /app/ContentFiles /cache /models /run/nginx \
  /var/lib/guideants/script-agent-admin /var/lib/guideants/script-agent-admin/scopes \
  /var/lib/guideants/comfyui-video /var/lib/guideants/comfyui-video/jobs
chown "$RUN_USER:$RUN_GROUP" /cache /models /var/lib/guideants/comfyui-video \
  /var/lib/guideants/comfyui-video/jobs

rm -rf /opt/ComfyUI/models
ln -s /models /opt/ComfyUI/models

gosu "$RUN_USER:$RUN_GROUP" python /opt/guideants/comfyui-video/scripts/verify-install.py

declare -a comfy_args=(
  --listen 127.0.0.1
  --port 8188
  --disable-auto-launch
)
if [[ "$video_gpu_backend" == "rocm" ]]; then
  comfy_args+=(--gpu-only --disable-smart-memory)
fi
gosu "$RUN_USER:$RUN_GROUP" \
  python /opt/ComfyUI/main.py "${comfy_args[@]}" &
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
