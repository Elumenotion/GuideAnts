# Hybrid emb/audio: this PC points at :8110, Max serves guideants-ai as video-ai on :8112

**Date:** 2026-08-18  
**Hosts:** developer workstation (GuideAnts webapi) → Max (`192.168.0.111`)  
**Status:** broken / misconfigured routing

## Symptom (workstation)

- Settings → Overview stays on **Loading overview...**
- `guideants-webapi-ui` logs: `Connection refused (192.168.0.111:8110)` from `LocalAiWarmupOrchestrationClient` / `LocalAiRuntimeWatchdogHostedService`
- Hybrid overlay on the workstation sets embeddings + audiocpp (ASR/TTS) to Max:

```text
LocalServiceHosts__EmbeddingsBaseUrl=http://192.168.0.111:8110
LocalServiceHosts__SpeechTranscriptionBaseUrl=http://192.168.0.111:8110
LocalServiceHosts__SpeechSynthesisBaseUrl=http://192.168.0.111:8110
```

(from `docker-compose.hybrid-max-emb-audio.local.yml` + `GA_REMOTE_AUDIO_EMB_BASE_URL` in `docker/.env`)

Overview probes every service mode for readiness. When emb/ASR/TTS hosts are unreachable, those probes stall and the Overview UI never finishes loading.

## What is actually running on Max (2026-08-18)

| Container | Status | Ports | Role |
|-----------|--------|-------|------|
| `guideants-video-ai` | Up (healthy) | **0.0.0.0:8112→80** | `guideants-ai:rocm-latest` — **intended** emb / ASR / TTS (and related AI gateway paths) for hybrid |
| `guideants-video-comfyui` | Up (healthy) | 127.0.0.1:8189→80 | ComfyUI video |
| Full GuideAnts ROCm stack (`docker-compose.rocm.yml` / `start_windows.cmd`) | **Not running** | nothing on **:8110** | Classic gateway port; images exist, stack is down |

Verified on Max localhost:

- `http://127.0.0.1:8110/emb/health` → connection failure (nothing listening)
- `http://127.0.0.1:8112/emb/health` → OK (`loaded: false` at check time)
- `http://127.0.0.1:8112/asr/health` → OK (`loaded: false` at check time)

Compose project in use: `guideants-video-stack` → `C:\repos\guideants-video-stack\compose.yml` (not the full `C:\repos\GuideAnts` ROCm stack).

Installer state on Max still records `BACKEND=rocm`, `COMPOSE_FILE=docker-compose.rocm.yml` (expects **8110**), but that stack is not currently up.

## Root cause

**Port / stack mismatch**, not a missing AI image:

1. The hybrid workstation config was written for the **full GuideAnts AI gateway on Max :8110**.
2. Max is instead running a **configured `guideants-ai` instance as `guideants-video-ai` on :8112**.
3. After main’s local-AI lifecycle changes, the API also treats Max URLs as warmup **stack** hosts and repeatedly hits `:8110`, which fails hard.

So the video-ai container **should** provide embeddings and audiocpp; the workstation is simply pointed at the wrong host port (and the full :8110 stack is not running).

## Expected hybrid topology

| Workload | Where |
|----------|--------|
| Chat LLM, SD, media, sandbox | Local workstation `guideants-ai` |
| Embeddings, ASR, TTS (audiocpp) | Max `guideants-video-ai` at **`http://192.168.0.111:8112`** |

## Fix options (choose one; do not do both blindly)

1. **Retarget hybrid on the workstation** (preferred if video-ai is the durable Max AI provider):
   - Set `GA_REMOTE_AUDIO_EMB_BASE_URL=http://192.168.0.111:8112`
   - Keep `GA_REMOTE_AUDIO_EMB_HOST_IP=192.168.0.111`
   - Recreate `guideants-webapi-ui` so env picks up the new base URL
   - Confirm emb/ASR/TTS warm on Max video-ai as needed

2. **Bring up full GuideAnts on Max :8110** only if that is still the intended long-term gateway — may contend with `guideants-video-stack` for GPU/ROCm; coordinate with video stack owners before starting.

3. **Temporary workstation relief:** remove/disable `docker-compose.hybrid-max-emb-audio.local.yml` (and related env) so emb/ASR/TTS fall back to local `guideants-ai` until Max routing is corrected.

## Follow-ups

- Align docs/scripts so hybrid example defaults match whatever Max actually runs (`:8112` video-ai vs `:8110` full stack).
- Warmup/admin lifecycle must use the **same** stack base as inference for emb/ASR/TTS (already partially addressed on main via stack host splitting; still fails if the configured URL is dead).
- After retarget, verify Settings Overview returns, and that index/query embeddings + Doug TTS hit Max video-ai.

## Related local files (workstation)

- `docker/docker-compose.hybrid-max-emb-audio.local.yml` (gitignored)
- `docker/docker-compose.hybrid-max-emb-audio.local.example.yml`
- `start_windows.cmd` (includes hybrid overlay when present)
- `docker/.env` → `GA_REMOTE_AUDIO_EMB_*`
