#!/bin/bash
set -e

export GA_TALKING_HEAD_SKILL_HOST="${GA_TALKING_HEAD_SKILL_HOST:-127.0.0.1}"
export GA_TALKING_HEAD_SKILL_PORT="${GA_TALKING_HEAD_SKILL_PORT:-8098}"
export GA_TALKING_HEAD_SKILL_LOG_LEVEL="${GA_TALKING_HEAD_SKILL_LOG_LEVEL:-warning}"
export GA_TALKING_HEAD_SKILL_STAGING_DIR="${GA_TALKING_HEAD_SKILL_STAGING_DIR:-/var/lib/guideants/talking-head-skill/staging}"
export GA_TALKING_HEAD_ADAPTER_HOST="${GA_TALKING_HEAD_ADAPTER_HOST:-127.0.0.1}"
export GA_TALKING_HEAD_ADAPTER_PORT="${GA_TALKING_HEAD_ADAPTER_PORT:-8190}"

mkdir -p "$GA_TALKING_HEAD_SKILL_STAGING_DIR"

if [ -z "${GA_TALKING_HEAD_SKILL_TOKEN:-}" ]; then
    echo "talking-head-skill-gateway: GA_TALKING_HEAD_SKILL_TOKEN is unset; gateway will refuse requests with HTTP 503" >&2
fi

exec /opt/venv/bin/python /opt/guideants/comfyui-video/talking-head-skill-gateway/skill_gateway.py
