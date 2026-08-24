#!/bin/bash
set -e

export GA_QWEN_IMAGE_SKILL_HOST="${GA_QWEN_IMAGE_SKILL_HOST:-127.0.0.1}"
export GA_QWEN_IMAGE_SKILL_PORT="${GA_QWEN_IMAGE_SKILL_PORT:-8097}"
export GA_QWEN_IMAGE_SKILL_LOG_LEVEL="${GA_QWEN_IMAGE_SKILL_LOG_LEVEL:-warning}"
export GA_QWEN_IMAGE_SKILL_STAGING_DIR="${GA_QWEN_IMAGE_SKILL_STAGING_DIR:-/var/lib/guideants/qwen-image-skill/staging}"
export GA_QWEN_IMAGE_ADAPTER_HOST="${GA_QWEN_IMAGE_ADAPTER_HOST:-127.0.0.1}"
export GA_QWEN_IMAGE_ADAPTER_PORT="${GA_QWEN_IMAGE_ADAPTER_PORT:-8190}"

mkdir -p "$GA_QWEN_IMAGE_SKILL_STAGING_DIR"

if [ -z "${GA_QWEN_IMAGE_SKILL_TOKEN:-}" ]; then
    echo "qwen-image-skill-gateway: GA_QWEN_IMAGE_SKILL_TOKEN is unset; gateway will refuse requests with HTTP 503" >&2
fi

exec /opt/venv/bin/python /opt/guideants/comfyui-video/qwen-image-skill-gateway/skill_gateway.py
