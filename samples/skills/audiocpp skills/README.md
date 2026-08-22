# audio.cpp skills

Experimental GuideAnts skills that reach **full** raw `audiocpp_server`
capabilities (OpenAI-style audio APIs, `/v1/tasks/*`, cloning, seeds, deferred
families, diarization) **without GuideAntsApi / ServiceModes changes**.

**Default deployment is a PC sandbox talking to Max** over the audiocpp raw
gateway — a token-gated transparent reverse proxy to the engines inside the AI
container. Scripts read `AUDIOCPP_SKILL_BASE_URL` and stage files / manage
private engines on Max. Do not assume `127.0.0.1` engines exist in the sandbox.

Deliverables land in `Output/` as WAV/text/JSON. Nothing here touches the live
phone/voice path or the GuideAnts voice picker.

## Required Environment (PC → Max)

```text
AUDIOCPP_SKILL_BASE_URL=http://<max-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as Max GA_AUDIOCPP_SKILL_TOKEN>
```

Optional: `HF_TOKEN` for gated Hugging Face downloads (executed on Max).

With those set, `probe.py` / `preflight.py` report the remote gateway open, and
`engine_tool.py`, `fetch_model.py`, `spawn_engine.py`, and `diarize.py` use it
automatically. Models land under `/models-local/skill/` on Max.

### Raw access shape

| Path | Meaning |
|---|---|
| `{BASE}/asr/...` | Full ASR engine API (transcriptions = text; timed fields use `/private` `tasks/run`) |
| `{BASE}/tts/...` | Full TTS engine API |
| `{BASE}/private/...` | Full private engine API (align / diar / deferred) |
| `{BASE}/files` | Upload → Max path for path-based JSON fields |
| `{BASE}/admin/...` | Fetch models / private start\|stop |

Any curl/agent can use the same paths with `X-Audiocpp-Skill-Token`.

## Skills

| Skill | What it does |
|---|---|
| [`audiocpp`](audiocpp/) | Umbrella skill + probe |
| [`audiocpp-tts-controls`](audiocpp-tts-controls/) | `seed`, `language`, `instructions`, voice list |
| [`audiocpp-voice-clone`](audiocpp-voice-clone/) | Clone from a reference clip |
| [`audiocpp-asr`](audiocpp-asr/) | Transcribe with language hint |
| [`audiocpp-timed-transcript`](audiocpp-timed-transcript/) | Long-form SRT/WebVTT/JSON (ASR + ForcedAligner; 30 s chunks) |
| [`audiocpp-diarize`](audiocpp-diarize/) | Who-spoke-when (overlap windows) + optional speaker SRT/RTTM merge |
| [`audiocpp-deferred-tts`](audiocpp-deferred-tts/) | Non-catalog TTS families via private engine on Max |
| [`audiocpp-host-tts`](audiocpp-host-tts/) | User’s own host-native `audiocpp_server` (not Max gateway) |

## Common rules

- Run preflight/probe first; trust it over these docs.
- Voice cloning only with speaker consent.
- Always `spawn_engine.py stop` when a private engine was started.
- Script budget ~5 minutes; poll/resume for downloads and warmups.
- Report honestly what worked and what was blocked.

## Limits

- Loaders missing from the Max container binary need `audiocpp-host-tts`.
- Diarization: max 4 speakers; offline; windows ≤ ~120 s with overlap stitch for longer files; labels are arbitrary.
- Product `/asr` `/tts` and ServiceModes are intentionally untouched.
