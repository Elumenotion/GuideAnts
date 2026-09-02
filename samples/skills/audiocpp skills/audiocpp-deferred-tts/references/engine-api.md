# Raw audiocpp_server engine API (via GPU host LAN gateway)

Ground truth: GuideAnts `docker/build/guideants-ai/audiocpp-skill-gateway/skill_gateway.py`
and audio.cpp `app/server`. This is the **full** engine surface â€” not a curated subset.

## PC sandbox â†’ GPU host raw gateway

```text
AUDIOCPP_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as the GPU host GA_AUDIOCPP_SKILL_TOKEN>
```

The gateway is a **transparent reverse proxy** to `audiocpp_server`:

| Client path | Upstream (inside GPU host AI container) |
|---|---|
| `{BASE}/asr/{any}` | `127.0.0.1:18082/{any}` â€” wrapper ASR engine |
| `{BASE}/tts/{any}` | `127.0.0.1:18084/{any}` â€” wrapper TTS engine |
| `{BASE}/private/{any}` | `127.0.0.1:18099/{any}` â€” skill-spawned private engine |

Auth header on every call: `X-Audiocpp-Skill-Token`.

Gateway-owned helpers (engines cannot do these over LAN alone):

| Path | Purpose |
|---|---|
| `GET /health` | Gateway liveness (wrappers HTTP + engines TCP). Does **not** wait on busy engines. |
| `GET /ready` | Deep parallel probes; `state=busy` means listening but inference stalled HTTP health |
| `POST /files` | Multipart upload â†’ host-local absolute `path` for path-based JSON fields |
| `POST /admin/models/fetch` | HF download into `/models-local/skill/â€¦` |
| `POST /admin/private/start` | Spawn private `audiocpp_server` |
| `GET /admin/private/status` | Private engine status |
| `POST /admin/private/stop` | Stop private engine |

Skill scripts (`engine_tool.py`, `fetch_model.py`, `spawn_engine.py`, `diarize.py`)
use this automatically when `AUDIOCPP_SKILL_BASE_URL` is set.

Do **not** curl `127.0.0.1:18082/18084/18099` from a PC sandbox â€” those exist only
inside the GPU host AI container. Use `{BASE}/asr|tts|private/...` instead.

## Full engine endpoints (proxied as-is)

Whatever your GPU host `audiocpp_server` build exposes is available under the matching
prefix. Typical surface:

### `GET /health`
Liveness when models are loaded (`lazy_load: false`).

### `POST /v1/audio/speech` â†’ raw WAV

```json
{
  "model": "<engine model id â€” required>",
  "input": "<text â€” required>",
  "voice": "<builtin or preset id>",
  "voice_ref": "<absolute path on the GPU host>",
  "reference_text": "<optional>",
  "language": "<family-specific>",
  "instructions": "<vdes-task only>",
  "seed": 42
}
```

Remote clients: `POST /files` first, put returned `path` in `voice_ref`, then
`POST /tts/v1/audio/speech` (or `/private/...`).

### `POST /v1/audio/transcriptions`

```json
{ "model": "qwen3-asr", "audio": "<absolute path on the GPU host>", "language": "en" }
```

Remote: stage with `/files`, then `POST /asr/v1/audio/transcriptions`.

### `POST /v1/tasks/run`

Generic tasks (diarization, VAD). Same path-based audio field. Remote: stage then
`POST /private/v1/tasks/run`. Raw engine does **not** resample â€”
`sortformer_diar` needs 16 kHz mono WAV.

### `GET /v1/audio/voices?model=<id>`
### `GET /v1/models` (when the build exposes it)
### Any other engine path your binary implements

Example: `GET http://<gpu-host>:8112/audiocpp-skill/tts/v1/models` with the skill token.

## Private engine (`spawn_engine.py` / `/admin/private/*`)

Written on the GPU host; mirrors wrapper `build_server_config_json`. After start, call the
engine through `{BASE}/private/...` â€” not a sandbox loopback URL.

## Product path (unchanged)

GuideAntsApi / ServiceModes / nginx `/asr` `/tts` wrappers stay as they are.
This gateway is a side-channel for agents and skills that need the **full**
engine API without expanding product contracts.
