# Raw audiocpp_server engine API and config schema

Ground truth: GuideAnts repo `docker/build/guideants-ai/tts-service/tts_service.py`,
`asr-service/asr_service.py` (which spawn and proxy these engines), and audio.cpp
`app/server`. Everything below is the *engine* surface, not the wrapper contract.

## Where engines live in this deployment

| Engine | URL | Model id | Runs when |
|---|---|---|---|
| ASR (wrapper-spawned) | `http://127.0.0.1:18082` | always `qwen3-asr` (fixed id, whatever model is loaded) | ASR wrapper has a model loaded |
| TTS (wrapper-spawned) | `http://127.0.0.1:18084` | equals the catalog entry id (e.g. `chatterbox`) — read `catalogEntryId` from `http://127.0.0.1:8084/health` | TTS wrapper has a model loaded |
| Private (skill-spawned) | `http://127.0.0.1:18099` (default) | whatever `--model-id` was passed to `spawn_engine.py` (defaults to the model dir leaf name) | you spawned it |
| Host-native | `http://host.docker.internal:8080` or `AUDIOCPP_ENGINE_URL` | user's config | user runs it |

Engine ports are overridable at service start via `GA_ASR_ENGINE_PORT` /
`GA_TTS_ENGINE_PORT`; 18082/18084 are the compose defaults. If they don't answer,
re-check the wrapper `/health` first — no loaded model means no engine process.

## Endpoints

### `GET /health`
Liveness. 200 when the engine is up and models are loaded (with `lazy_load: false`,
readiness implies the model is in memory).

### `POST /v1/audio/speech` → raw WAV bytes

```json
{
  "model": "<engine model id — required>",
  "input": "<text to speak — required>",
  "voice": "<builtin speaker id or voice-preset id>",
  "voice_ref": "<absolute path to a reference WAV, readable by the engine process>",
  "reference_text": "<transcript of the reference clip (some cloning families want it)>",
  "language": "<language name/code, family-specific>",
  "instructions": "<voice-design description — vdes-task models only>",
  "seed": 42
}
```

All fields except `model` and `input` are optional and family-dependent; the
engine errors loudly on options the loaded family doesn't support — surface that
error text, it is usually self-explanatory.

Because the sandbox shares the container filesystem with the wrapper-spawned
engines, `voice_ref` can point at a workspace file — pass an **absolute** path
(`os.path.abspath`), not a relative one (the engine's CWD is not yours).

### `POST /v1/audio/transcriptions`

```json
{"model": "qwen3-asr", "audio": "<absolute server-local path>", "language": "en"}
```

Returns `{"text": ..., "language"?: ...}`. Path-based, no upload — works from the
sandbox against the *container* engines (shared fs), but **not** against a
host-native engine (it can't see sandbox files).

### `POST /v1/tasks/run`

Generic framework route (verified in audio.cpp release-0.2 `app/server/runtime.cpp`
`handle_generic_run`) — the way to reach tasks the OpenAI-style endpoints can't
express, e.g. **diarization** and VAD. `/v1/tasks/stream` is the streaming twin.

```json
{
  "model": "<engine model id — required>",
  "request": {
    "audio": "<absolute server-local path to a WAV>",
    "text": "...", "voice_id": "...", "voice_ref": "...", "language": "...",
    "options": {"<request option>": "..."}
  }
}
```

`request` uses the `audiocpp_cli` request-sequence fields; for a diar model only
`audio` matters. **The raw engine does not resample** — `sortformer_diar` throws
`sample_rate mismatch` unless the WAV matches the model's `processor_config.json`
rate (16 kHz, mono). Response is a flat JSON object whose fields depend on the
task; for diarization:

```json
{"speaker_turns": [{"start_sample": 0, "end_sample": 48000, "speaker_id": "…", "confidence": 0.93}]}
```

`start_sample`/`end_sample` are in samples at the model's rate (÷16000 for
seconds). VAD models return `speech_segments` with the same span shape instead.
`scripts/diarize.py` wraps this endpoint end to end.

### `GET /v1/audio/voices?model=<id>`
Lists voice/speaker ids the loaded model exposes. Known gap: Qwen3 CustomVoice
builtin speakers live in the model's own config and may **not** appear here —
that gap is exactly why GuideAnts deferred the model. Fall back to inspecting the
downloaded model's `config.json` / `generation_config.json` for a speaker list.

## Engine config file (what `spawn_engine.py` writes)

Mirrors what the wrappers generate (`build_server_config_json`):

```json
{
  "host": "127.0.0.1",
  "port": 18099,
  "backend": "cuda",
  "device": 0,
  "threads": 8,
  "lazy_load": false,
  "models": [
    {
      "id": "<model id used in requests>",
      "family": "<loader family: qwen3_tts | chatterbox | omnivoice | pocket_tts | vibevoice | miotts | voxcpm2 | vevo2 | qwen3_asr | citrinet_asr | sortformer_diar | silero_vad | marblenet_vad>",
      "path": "<absolute model directory>",
      "task": "<see task tokens below>",
      "mode": "offline",
      "load_options": {"<family-specific>": "..."},
      "session_options": {"<family-specific>": "..."}
    }
  ]
}
```

Launch: `audiocpp_server --config <file> --host 127.0.0.1 --port <port> --device <n> --threads <n>`.

- `backend` must match the flavors compiled into the binary (this image: `cuda`;
  `cpu` may or may not be compiled in — a backend mismatch fails every load with
  a clear error).
- Family names must match audio.cpp's loader registry. GuideAnts' catalog uses the
  same names, so the values above are safe anchors; for deferred families confirm
  against the audio.cpp README / `model_manager.py` when in doubt.

## Task tokens (`task` field)

From audio.cpp `parse_voice_task_kind` (via GuideAnts `resolve_engine_task`):

| Token | Route | Used by |
|---|---|---|
| `tts` | general synthesis | qwen3_tts (Base/CustomVoice), omnivoice, pocket_tts, most others |
| `clon` | reference-clip cloning | chatterbox |
| `vdes` | voice design (instructions) | qwen3_tts VoiceDesign |
| `asr` | transcription | qwen3_asr, citrinet_asr |
| `diar` | speaker diarization (`/v1/tasks/run` → `speaker_turns`) | sortformer_diar |
| `vad` | voice activity detection (`/v1/tasks/run` → `speech_segments`) | silero_vad, marblenet_vad |

Known `load_options` from GuideAnts production configs:
- chatterbox: `{"language": "en"}`
- pocket_tts: `{"language": "english"}` (or another downloaded language)
- qwen3_asr/citrinet weight override is session-scoped: `{"<family>.weight_type": "..."}`

`sortformer_diar` session options (audio.cpp `docs/speech_analysis.md`; pass via
`spawn_engine.py --option key=value`): `speaker_threshold` (default 0.5),
`speaker_min_frames` (0), `speaker_pad_frames` (0), `session_len_sec` (20.0).

## Wrapper admin surface (context)

- ASR wrapper `127.0.0.1:8082`: `/health`, `/admin/load` (accepts non-catalog bare
  dir names under `/models-local/asr` — family assumed `qwen3_asr` unless the
  service was started with `GA_ASR_ENGINE_FAMILY`), `/admin/unload`, `/admin/models`.
- TTS wrapper `127.0.0.1:8084`: `/health` (has `catalogEntryId`), `/admin/load`
  (**catalog-only** — both `model_id` and `model_path` are validated against the
  manifest), `/admin/unload`, `/admin/voice-pack`, `/admin/voices`.

## Environment notes

- Sandbox scripts get a curated, non-inherited environment: the container's
  `HF_TOKEN` etc. are NOT automatically visible. Set needed vars (e.g. `HF_TOKEN`
  for gated repos, `AUDIOCPP_ENGINE_URL`) in the guide editor's Environment
  variables UI.
- Model volume: `/models-local` (`tts/`, `asr/`, `emb/`, `sd/`, `llama/`). The
  probe reports whether the sandbox user can write there; if not, download into
  the workspace and point `--path` at that instead (same filesystem, works fine —
  it just won't be shared with the wrappers or persist with the models volume).
