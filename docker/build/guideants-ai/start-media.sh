#!/bin/bash
set -e

export GA_MEDIA_HOST="${GA_MEDIA_HOST:-127.0.0.1}"
export GA_MEDIA_PORT="${GA_MEDIA_PORT:-8087}"
export GA_MEDIA_LOG_LEVEL="${GA_MEDIA_LOG_LEVEL:-info}"

exec /opt/venv/bin/python /app/media-service/media_service.py
