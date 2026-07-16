---
name: audiocpp-extended
description: "Experimental: use audio.cpp scenarios and models that GuideAnts does not ship — speaker diarization (who spoke when, speaker-labeled transcripts), voice cloning from user-supplied audio, deterministic seeds, builtin speakers, language forcing, and deferred TTS families (Qwen3 CustomVoice, VibeVoice, MioTTS, VoxCPM2, PocketTTS) — by calling the raw audiocpp_server engines directly or spawning a private engine in the sandbox. Use when the user asks for a voice, model, or audio scenario the built-in audio tools reject or cannot express, including diarizing a meeting recording or labeling speakers in a transcript."
metadata:
  guideants:
    enabled: true
    display_order: 30
    requires_toolsets: [sandbox]
---

# audio.cpp extended (experimental)

GuideAnts exposes audio.cpp through curated wrapper services with a deliberately
narrow contract (`{text, voice, speed}` against a fixed model catalog). The engine
underneath supports much more. This skill reaches that extra surface **without any
GuideAnts changes**, using the fact that the sandbox runs in the same container as
the audio services.

Everything here is experimental. Run the probe first, trust its report over this
document, and tell the user plainly when a route is blocked.

## Always start with the probe

```bash
python3 Output/Skills/audiocpp-extended/scripts/probe.py
```

It prints a JSON report saying which of the four routes below are open in this
deployment, plus the evidence (ports reachable, dirs writable, binary present,
HF egress, GPU state). Do not attempt a route the probe marked blocked.

## Voice cloning consent rule

Cloning a voice from a reference clip (`voice_ref`, Routes 1/3/4) is a supported,
intended use of this skill **when the speaker consents**: the user states the clip
is their own voice, or that they have the speaker's permission. In that case,
proceed — do not refuse consented cloning as a "security risk"; producing a WAV
deliverable in the workspace impersonates no live system. Decline only when the
request is to imitate a third party without consent (a celebrity, a colleague, a
public figure), and say that consent is the missing piece.

## The four routes (try in this order)

### Route 1 — raw engine API on the already-loaded model (cheapest)

The wrapper services each spawn a real `audiocpp_server` on localhost:
ASR engine at `127.0.0.1:18082`, TTS engine at `127.0.0.1:18084` (running only
while the wrapper has a model loaded). Calling them directly unlocks, on the
model GuideAnts already loaded:

- **Voice cloning from a user-supplied reference clip** (`voice_ref` = an absolute
  path to a WAV in your workspace — same filesystem, so the engine can read it),
  optionally with `reference_text`.
- **Deterministic synthesis** via `seed`.
- **Language forcing** via `language` (the .NET wire never sends `lang_code`).
- **Voice-design `instructions`** (only on a model loaded with the `vdes` task,
  i.e. the VoiceDesign catalog entry).
- **ASR on a workspace file by path** with a `language` hint via
  `/v1/audio/transcriptions` — no upload, no wrapper contract.

```bash
python3 Output/Skills/audiocpp-extended/scripts/engine_tool.py speech "Hello there" \
  -o Output/cloned.wav --voice-ref Output/uploads/user_voice.wav --seed 42
python3 Output/Skills/audiocpp-extended/scripts/engine_tool.py transcribe Output/uploads/clip.wav --language de
python3 Output/Skills/audiocpp-extended/scripts/engine_tool.py voices
```

`engine_tool.py speech` auto-detects the TTS engine's model id from the wrapper's
`/health` (`catalogEntryId`). The ASR engine's model id is always `qwen3-asr`.

### Route 2 — sideload a non-catalog ASR model through the wrapper

The **ASR** wrapper (unlike TTS) accepts a bare directory under
`/models-local/asr` even when it is not in the catalog: `/admin/load` with a
`model_id` whose leaf matches the directory name. The engine family defaults to
`qwen3_asr` (override requires the `GA_ASR_ENGINE_FAMILY` env at service start,
which a skill cannot set — so this route is only useful for other
qwen3_asr-family snapshots).

```bash
python3 Output/Skills/audiocpp-extended/scripts/fetch_model.py <hf-repo-id> --dest /models-local/asr/<DirName>
curl -s -X POST http://127.0.0.1:8082/admin/load -H "Content-Type: application/json" -d '{"model_id": "<DirName>"}'
```

Requires `/models-local/asr` to be writable from the sandbox — the probe checks.
**Warning:** this replaces the user's loaded ASR model. Ask before doing it, and
offer to reload the previous model afterwards (`{"model_id": "qwen3_asr_0_6b"}`).

### Route 3 — spawn a private engine for a deferred/non-catalog TTS model

The TTS wrapper is strictly catalog-gated, but the container ships the full
`audiocpp_server` binary (`/usr/local/bin/audiocpp_server`) with every *released*
loader — including the families GuideAnts defers (Qwen3 CustomVoice, VibeVoice,
MioTTS, VoxCPM2, PocketTTS, Vevo2). The blockers that kept them out of the catalog
are download/packaging chores a script can do by hand. So:

1. Download the model with `fetch_model.py` (per-family recipe in
   `references/deferred-models.md` — composite repos, file scoping, conversions).
2. Spawn your own engine on a free port (default 18099):

```bash
python3 Output/Skills/audiocpp-extended/scripts/spawn_engine.py start \
  --path /models-local/tts/Qwen3-TTS-12Hz-1.7B-CustomVoice --family qwen3_tts --task tts
python3 Output/Skills/audiocpp-extended/scripts/spawn_engine.py status   # poll until ready
python3 Output/Skills/audiocpp-extended/scripts/engine_tool.py speech "Hi" \
  --engine-url http://127.0.0.1:18099 --model Qwen3-TTS-12Hz-1.7B-CustomVoice --voice Vivian -o Output/hi.wav
python3 Output/Skills/audiocpp-extended/scripts/spawn_engine.py stop     # always stop when done
```

Ground rules for this route:

- **VRAM**: a second engine competes with the wrapper's loaded models. Prefer
  asking the user to unload the GuideAnts TTS model first (Settings, or
  `curl -X POST http://127.0.0.1:8084/admin/unload`) — ask, never do it silently.
- **Script budget is ~5 minutes per call.** `spawn_engine.py start` detaches the
  engine and returns; big models may still be warming up — call `status` in a
  follow-up script instead of blocking. Downloads of multi-GB models may need
  several `fetch_model.py` calls (it resumes; already-complete files are skipped).
- **Always `stop` the private engine** when the task is done or the user moves on.
- If the detached engine does not survive between script calls (probe reports
  this), Route 3 is limited to synth requests issued in the same script call that
  spawned it — still workable for small models, not for slow-warmup ones.

### Route 4 — the user's host-native audiocpp_server

`host.docker.internal:8080` (or `AUDIOCPP_ENGINE_URL`). Same raw API as Route 1,
but whatever models and loaders the user's own build has — including families the
container binary lacks (e.g. a fork built with Kokoro or Parakeet enabled).
TTS-only from the sandbox: its `/v1/audio/transcriptions` wants host-local paths
that sandbox files are not.

## Scenario recipe: speaker diarization (who spoke when)

Diarization is a whole task class GuideAnts has no local support for (only the
Azure cloud transcription provider does it) — but the container binary ships the
`sortformer_diar` loader and a generic `/v1/tasks/run` route. This is Route 3
with a small model (~hundreds of MB, usually fits without unloading anything):

```bash
python3 Output/Skills/audiocpp-extended/scripts/fetch_model.py nvidia/diar_sortformer_4spk-v1 \
  --dest /models-local/asr/diar_sortformer_4spk-v1 --exclude diar_sortformer_4spk-v1.nemo
python3 Output/Skills/audiocpp-extended/scripts/spawn_engine.py start \
  --path /models-local/asr/diar_sortformer_4spk-v1 --family sortformer_diar --task diar
python3 Output/Skills/audiocpp-extended/scripts/diarize.py Output/uploads/meeting.mp3 -o Output/meeting
python3 Output/Skills/audiocpp-extended/scripts/spawn_engine.py stop
```

(Check whether `/models-local/asr/diar_sortformer_4spk-v1/model.safetensors`
already exists before downloading — the model persists across runs.)

`diarize.py` converts the input to the 16 kHz mono WAV the engine requires,
fetches speaker turns, merges/filters them, and — when the GuideAnts ASR engine
is up (Route 1) — transcribes each turn so the deliverable is a speaker-labeled
transcript (`<base>.transcript.txt` + `<base>.diarization.json` in `Output/`).
Pass `--turns-only` to skip labeling, `--language` as an ASR hint. Know the
limits: offline only, at most 4 speakers (model variant), and speaker ids are
arbitrary labels — ask the user who is who if they want real names. For long
recordings the per-turn labeling can hit the script budget; the JSON then says
`"partial": true` — just rerun `diarize.py` (the diar pass itself is fast).

## What a skill cannot do

- Load a non-catalog model through the **TTS wrapper** (`/admin/load` hard-rejects
  anything outside the manifest) — hence Route 3.
- Use loaders that are **not compiled into the container binary**: Kokoro
  (`kokoro_tts`) is not in audio.cpp release builds and `parakeet_tdt`'s loader is
  commented out upstream. No amount of downloading fixes a missing loader; only
  Route 4 against a custom host build can serve those.
- Set service-level env (`GA_ASR_ENGINE_FAMILY`, `GA_*_CUDA_VISIBLE_DEVICES`, …) —
  those need a compose/.env change, i.e. actual GuideAnts configuration.
- Show up in GuideAnts' voice-picker UI. Deliverables are WAV/text files in
  `Output/`; the live phone/voice path keeps using the built-in pipeline.

## References

- `references/engine-api.md` — raw engine endpoints, request fields, the engine
  config-file schema `spawn_engine.py` writes, task tokens (`tts`/`clon`/`vdes`).
- `references/deferred-models.md` — per-family recipes for the deferred models and
  the exact blocker each script has to compensate for.

## Reporting results

This skill exists to answer "is it possible?". Whatever happens, end by telling
the user which route you used, what worked, what was blocked and why (quote the
probe/report evidence), so the experiment log stays honest.
