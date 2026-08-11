#!/usr/bin/env bash
set -Eeuo pipefail

rebuild_base=false
run_smoke_tests=false
cuda_visible_devices="${GA_COMFYUI_VIDEO_CUDA_VISIBLE_DEVICES:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --rebuild-base) rebuild_base=true ;;
    --run-smoke-tests) run_smoke_tests=true ;;
    --cuda-visible-devices)
      shift
      cuda_visible_devices="${1:-}"
      ;;
    -h|--help)
      printf '%s\n' "Usage: build_comfyui_video.sh [--rebuild-base] [--run-smoke-tests] [--cuda-visible-devices UUID_OR_INDEX]"
      exit 0
      ;;
    *) printf 'Unknown argument: %s\n' "$1" >&2; exit 2 ;;
  esac
  shift
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
docker_root="$(cd "$script_dir/.." && pwd)"
repo_root="$(cd "$docker_root/.." && pwd)"
context="$script_dir/comfyui-video"
state="$docker_root/.build-state"
publish="$state/scriptexecutionagent-comfyui-video-publish"
agent_project="$repo_root/src/server/ScriptExecutionAgent"
agent_dest="$context/ScriptExecutionAgent"
exec_dest="$context/script-agent-exec"
exec_source="$script_dir/guideants-ai/script-agent-exec/ga-script-exec.c"
dockerfile="$context/Dockerfile.cuda"
lock="$context/source-lock.json"
mkdir -p "$state"

python - "$lock" <<'PY'
import json
import re
import sys

lock = json.load(open(sys.argv[1], encoding="utf-8"))
if not re.search(r"@sha256:[0-9a-f]{64}$", lock["baseImage"]["reference"]):
    raise SystemExit("source-lock.json base image is unresolved; release builds require a verified digest")
if lock["baseImage"]["reference"].rsplit("@", 1)[1] != lock["baseImage"]["platformDigest"]:
    raise SystemExit("source-lock.json base image reference must use the linux/amd64 platform digest")
if lock["pytorch"] != {
    "version": "2.11.0+cu130",
    "index": "https://download.pytorch.org/whl/cu130",
    "attention": "sdpa",
}:
    raise SystemExit("source-lock.json must select torch 2.11.0+cu130 and SDPA")
PY
[[ -f "$exec_source" ]] || {
  printf 'ga-script-exec source not found: %s\n' "$exec_source" >&2
  exit 1
}

dotnet restore "$agent_project"
dotnet publish "$agent_project" -c Release -o "$publish" --no-restore

cleanup() {
  rm -rf "$agent_dest" "$exec_dest"
}
trap cleanup EXIT
rm -rf "$agent_dest" "$exec_dest"
cp -R "$publish" "$agent_dest"
mkdir -p "$exec_dest"
cp "$exec_source" "$exec_dest/"

deps_hash="$(
  sha256sum \
    "$dockerfile" \
    "$context/constraints/common.txt" \
    "$context/constraints/cuda13.txt" \
    "$lock" |
    sha256sum |
    awk '{print substr($1, 1, 12)}'
)"
deps_tag="guideants-comfyui-deps:cuda13-$deps_hash"
deps_cache_tag="guideants-comfyui-deps:cuda13-cache"
image_tag="guideants-comfyui:cuda13-latest"

if [[ "$rebuild_base" == true ]] || ! docker image inspect "$deps_tag" >/dev/null 2>&1; then
  deps_args=(buildx build --load --target deps-cuda13 -t "$deps_tag" -t "$deps_cache_tag" -f "$dockerfile")
  if [[ "$rebuild_base" == true ]]; then deps_args+=(--no-cache); fi
  deps_args+=("$context")
  docker "${deps_args[@]}"
fi

docker buildx build --load --target final-cuda13 \
  --build-arg "GA_COMFYUI_VIDEO_DEPS_IMAGE=$deps_tag" \
  --cache-from "$deps_cache_tag" \
  -t "$image_tag" -f "$dockerfile" "$context"

if [[ "$run_smoke_tests" == true ]]; then
  [[ -n "$cuda_visible_devices" && "$cuda_visible_devices" != *,* ]] || {
    printf '%s\n' "Smoke tests require exactly one GPU via --cuda-visible-devices or GA_COMFYUI_VIDEO_CUDA_VISIBLE_DEVICES." >&2
    exit 1
  }
  docker run --rm --gpus all \
    --env "CUDA_VISIBLE_DEVICES=$cuda_visible_devices" \
    --entrypoint python "$image_tag" \
    /opt/guideants/comfyui-video/scripts/verify-install.py
fi
printf 'Built %s (dependencies: %s)\n' "$image_tag" "$deps_tag"
