#!/bin/bash
set -e

export GA_ASR_HOST="${GA_ASR_HOST:-127.0.0.1}"
export GA_ASR_PORT="${GA_ASR_PORT:-8082}"
export GA_ASR_LOG_LEVEL="${GA_ASR_LOG_LEVEL:-info}"
export GA_ASR_MODEL_DIR="${GA_ASR_MODEL_DIR:-/models-local/asr}"
export GA_ASR_DEFAULT_MODEL_ID="${GA_ASR_DEFAULT_MODEL_ID:-Qwen/Qwen3-ASR-0.6B}"
export GA_ASR_TIMEOUT_SECONDS="${GA_ASR_TIMEOUT_SECONDS:-300}"
export GA_ASR_DEVICE_MAP="${GA_ASR_DEVICE_MAP:-auto}"
export GA_ASR_WARMUP_AUDIO_PATH="${GA_ASR_WARMUP_AUDIO_PATH:-/app/asr-service/warmup.webm}"

exec /opt/venv/bin/python /app/asr-service/asr_service.py
