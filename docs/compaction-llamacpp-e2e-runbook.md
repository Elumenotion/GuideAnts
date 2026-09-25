# Running the compaction end-to-end test against a real llama.cpp

`CompactionLlamaCppEndToEndTests` exercises the path the hermetic suite cannot: llama-server's real
`exceed_context_size_error` response, classified by `LlamaCppChatClient`, surfacing as `chat_context_overflow`.
It is opt-in and skipped (Inconclusive) unless `GA_COMPACTION_E2E_LLAMA_URL` is set.

This starts a **separate, throwaway** llama-server on host port 8199. It does not touch the `guideants-ai`
container.

## 1. Get a small model (~400 MB, once)

```bash
mkdir -p ~/models
curl -L -o ~/models/qwen2.5-0.5b-instruct-q4_k_m.gguf \
  https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf
```

## 2. Start llama-server with a 2048-token window

```bash
docker run --rm -d --name ga-compaction-e2e -p 8199:8080 -v ~/models:/models \
  ghcr.io/ggml-org/llama.cpp:server \
  -m /models/qwen2.5-0.5b-instruct-q4_k_m.gguf -c 2048 --host 0.0.0.0 --port 8080 --jinja
```

`--jinja` lets the server accept OpenAI-style `tools`. Do not pass `--context-shift`: with it the server
silently drops old tokens instead of rejecting, and the test can never see an overflow.

## 3. Verify the server rejects an oversized prompt (the test depends on this)

```bash
python3 -c "import json;print(json.dumps({'messages':[{'role':'user','content':'x '*6000}]}))" \
  | curl -s -o /dev/stderr -w '%{http_code}\n' -H 'Content-Type: application/json' \
      -d @- http://localhost:8199/v1/chat/completions
```

Expected: HTTP `400`, with a body containing `exceed_context_size_error`. If you get `200`, the build has
context shift on by default: restart with `--no-context-shift` added.

## 4. Run the test

From `src/server`:

```bash
GA_INTEGRATION_TEST_MSSQL_IMAGE=ghcr.io/elumenotion/mssql2025-express-fts:main \
GA_COMPACTION_E2E_LLAMA_URL=http://localhost:8199 \
GA_COMPACTION_E2E_LLAMA_MODEL=qwen2.5-0.5b-instruct \
  dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj \
  --filter "FullyQualifiedName~CompactionLlamaCppEndToEndTests"
```

Expected: `Passed: 1`. A CPU-only run takes a few minutes.

## 5. Clean up

```bash
docker stop ga-compaction-e2e
```
