#!/bin/bash
set -e

export GA_AUDIOCPP_SKILL_HOST="${GA_AUDIOCPP_SKILL_HOST:-127.0.0.1}"
export GA_AUDIOCPP_SKILL_PORT="${GA_AUDIOCPP_SKILL_PORT:-8096}"
export GA_AUDIOCPP_SKILL_LOG_LEVEL="${GA_AUDIOCPP_SKILL_LOG_LEVEL:-warning}"
export GA_AUDIOCPP_SKILL_STAGING_DIR="${GA_AUDIOCPP_SKILL_STAGING_DIR:-/var/lib/guideants/audiocpp-skill/staging}"
export GA_AUDIOCPP_SKILL_MODELS_DIR="${GA_AUDIOCPP_SKILL_MODELS_DIR:-/models-local/skill}"
export GA_AUDIOCPP_SKILL_STATE_DIR="${GA_AUDIOCPP_SKILL_STATE_DIR:-/var/lib/guideants/audiocpp-skill/private}"
export GA_AUDIOCPP_SKILL_PRIVATE_PORT="${GA_AUDIOCPP_SKILL_PRIVATE_PORT:-18099}"

mkdir -p "$GA_AUDIOCPP_SKILL_STAGING_DIR" "$GA_AUDIOCPP_SKILL_MODELS_DIR" "$GA_AUDIOCPP_SKILL_STATE_DIR"

if [ -z "${GA_AUDIOCPP_SKILL_TOKEN:-}" ]; then
    echo "audiocpp-skill-gateway: GA_AUDIOCPP_SKILL_TOKEN is unset; gateway will refuse requests with HTTP 503" >&2
fi

exec /opt/venv/bin/python /app/audiocpp-skill-gateway/skill_gateway.py
