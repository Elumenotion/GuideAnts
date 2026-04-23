#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://localhost:8110/llama-cpp}"
MODEL="${2:-qwen3.5-27b}"

curl -sS "$BASE_URL/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d "$(cat <<JSON
{
  "model": "$MODEL",
  "messages": [
    { "role": "user", "content": "Reply with exactly: pong" }
  ],
  "max_tokens": 32
}
JSON
)"
