---
name: audiocpp-extended
description: "Experimental: use full raw audiocpp_server scenarios GuideAnts does not ship — speaker diarization, voice cloning, seeds, language forcing, voice-design, and deferred TTS families — via the GPU host token-gated raw gateway (/asr|/tts|/private) from a PC sandbox (or co-located engines if present). Use when the user asks for a voice, model, or audio scenario the built-in audio tools reject, including diarizing a meeting or labeling speakers."
metadata:
  guideants:
    enabled: true
    display_order: 30
    requires_toolsets: [sandbox]
---

# audio.cpp extended (experimental)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. Skill scripts live under `Skills/audiocpp/scripts/`
relative to it. Write deliverables with **bare filenames**; never prefix with `Output/`.

GuideAnts product ASR/TTS only expose `{text, voice, speed}` against a fixed
catalog. This skill reaches the raw engine surface **without GuideAntsApi /
ServiceModes changes**.

**Default path: PC sandbox → GPU host raw audiocpp gateway.** That host is a
transparent reverse proxy to full `audiocpp_server` (`/asr/*`, `/tts/*`,
`/private/*`), plus `/files` staging and `/admin/*` for model fetch / private
spawn. Scripts use `AUDIOCPP_SKILL_BASE_URL` when set. Do not call
`127.0.0.1:18082/18084` from a PC sandbox — those ports exist only inside the
GPU host AI container.

Everything here is experimental. Run the probe first, trust its report, and tell
the user plainly when a route is blocked.

## Environment (required for PC → the GPU host)

```text
AUDIOCPP_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as the GPU host GA_AUDIOCPP_SKILL_TOKEN>
```

Optional: `HF_TOKEN` for gated downloads (runs on the GPU host).

With these set, scripts stage workspace audio via `/files`, call raw engine
JSON under `/asr|/tts|/private`, download models into `/models-local/skill/…`
on the GPU host, and spawn private engines there. See `references/engine-api.md`.

## Always start with the probe

```bash
python3 Skills/audiocpp-extended/scripts/probe.py
```

Expect `routes.route5_remote_skill_gateway.open: true` on a PC sandbox. Do not
attempt a route the probe marked blocked.

## Voice cloning consent rule

Cloning from a reference clip is supported **when the speaker consents** (own
voice or stated permission). Decline only unconsented third-party imitation.

## What to run (raw gateway path)

ASR / TTS must already be loaded on the GPU host via GuideAnts Settings (API lifecycle).
Then:

```bash
# Synthesis with seed / language / clone
python3 Skills/audiocpp-extended/scripts/engine_tool.py speech "Hello there" \
  -o out.wav --voice-ref uploads/user_voice.wav --seed 42

python3 Skills/audiocpp-extended/scripts/engine_tool.py transcribe \
  uploads/clip.wav --language de

python3 Skills/audiocpp-extended/scripts/engine_tool.py voices
```

`engine_tool.py` auto-detects the loaded TTS `catalogEntryId` from gateway
health. ASR engine model id is always `qwen3-asr`.

### Private engine (deferred TTS / diarization)

Downloads and private engines run on the GPU host under `/models-local/skill/`. Pass the
usual dest paths; when the gateway is configured, scripts rewrite them:

```bash
python3 Skills/audiocpp-extended/scripts/fetch_model.py <hf-repo> \
  --dest /models-local/tts/<DirName>

python3 Skills/audiocpp-extended/scripts/spawn_engine.py start \
  --path /models-local/tts/<DirName> --family <family> --task tts
python3 Skills/audiocpp-extended/scripts/spawn_engine.py status
python3 Skills/audiocpp-extended/scripts/engine_tool.py speech "Hi" \
  --engine-url http://127.0.0.1:18099 --model <id> -o hi.wav
python3 Skills/audiocpp-extended/scripts/spawn_engine.py stop
```

(`--engine-url http://127.0.0.1:18099` is a label for the gateway’s private
engine; the request still goes to `AUDIOCPP_SKILL_BASE_URL`.)

Ground rules:

- Product emb/ASR/TTS normally stay loaded on the GPU host. Only ask before unloading
  via Settings if a private second engine truly cannot fit (never unload silently).
- Script budget ~5 minutes; poll `status` / re-run `fetch_model.py` to resume.
- Always `stop` the private engine when done.

### Speaker diarization

```bash
python3 Skills/audiocpp-extended/scripts/fetch_model.py nvidia/diar_sortformer_4spk-v1 \
  --dest /models-local/asr/diar_sortformer_4spk-v1 --exclude diar_sortformer_4spk-v1.nemo
python3 Skills/audiocpp-extended/scripts/spawn_engine.py start \
  --path /models-local/asr/diar_sortformer_4spk-v1 --family sortformer_diar --task diar
python3 Skills/audiocpp-extended/scripts/diarize.py uploads/meeting.mp3 -o meeting
python3 Skills/audiocpp-extended/scripts/spawn_engine.py stop
```

Outputs: `<base>.transcript.txt` + `<base>.diarization.json`. Limits: offline,
max 4 speakers, arbitrary `SPEAKER_00` labels.

## Co-located sandbox only (rare)

If the sandbox runs **inside** the GPU host AI container and `AUDIOCPP_SKILL_BASE_URL`
is unset, scripts fall back to loopback engines (`18082`/`18084`) and local
`audiocpp_server` spawn. That is not the PC hybrid layout.

## What a skill cannot do

- Change ServiceModes or product `/asr` `/tts` contracts.
- Load non-catalog models through the TTS wrapper (use private engine).
- Use loaders not compiled into the GPU host image binary (use **audiocpp-host-tts**).
- Appear in the GuideAnts voice-picker UI.

## References

- `references/engine-api.md` — raw engine endpoints and config schema.
- `references/deferred-models.md` — per-family download recipes.

## Reporting

End by saying which path you used (remote gateway vs co-located), what worked,
and what was blocked — quote probe/preflight evidence.
