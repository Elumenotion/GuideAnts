#!/bin/bash
set -e

export GA_LLAMA_ADMIN_HOST="${GA_LLAMA_ADMIN_HOST:-127.0.0.1}"
export GA_LLAMA_ADMIN_PORT="${GA_LLAMA_ADMIN_PORT:-8086}"
export GA_LLAMA_ADMIN_LOG_LEVEL="${GA_LLAMA_ADMIN_LOG_LEVEL:-info}"
export GA_LLAMA_MODEL_DIR="${GA_LLAMA_MODEL_DIR:-/models-local/llama}"
export GA_LLAMA_ROUTER_CONFIG_PATH="${GA_LLAMA_ROUTER_CONFIG_PATH:-${GA_LLAMA_MODELS_PRESET:-/models-local/router-models.ini}}"

exec /opt/venv/bin/python /app/llama-admin-service/llama_admin_service.py
