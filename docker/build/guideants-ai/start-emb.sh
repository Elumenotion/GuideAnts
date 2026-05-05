#!/bin/bash
set -e

export GA_EMB_HOST="${GA_EMB_HOST:-127.0.0.1}"
export GA_EMB_PORT="${GA_EMB_PORT:-8085}"
export GA_EMB_LOG_LEVEL="${GA_EMB_LOG_LEVEL:-info}"
export GA_EMB_MODEL_DIR="${GA_EMB_MODEL_DIR:-/models-local/emb}"
export GA_EMB_DEFAULT_MODEL_PATH="${GA_EMB_DEFAULT_MODEL_PATH:-}"
export GA_EMB_DEVICE="${GA_EMB_DEVICE:-cuda}"
export GA_EMB_TARGET_DEVICES="${GA_EMB_TARGET_DEVICES:-cuda:0,cuda:1}"
export GA_EMB_FIX_MISTRAL_REGEX="${GA_EMB_FIX_MISTRAL_REGEX:-1}"
export GA_EMB_AUTO_LOAD_ON_STARTUP="${GA_EMB_AUTO_LOAD_ON_STARTUP:-0}"
export GA_EMB_WARMUP_ON_LOAD="${GA_EMB_WARMUP_ON_LOAD:-1}"

exec /opt/venv/bin/python /app/emb-service/emb_service.py
