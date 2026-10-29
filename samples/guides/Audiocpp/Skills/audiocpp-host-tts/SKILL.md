---
name: audiocpp-host-tts
description: "Synthesize against the user's own host-native audiocpp_server (outside GPU host / GuideAnts containers) — including families the GPU host image binary lacks. TTS only. Use when the user runs their own audio.cpp build."
metadata:
  guideants:
    enabled: true
    display_order: 36
    requires_toolsets: [sandbox]
---

# audio.cpp host-native engine (experimental)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/audiocpp-host-tts/scripts/` relative to it. Write deliverables with
**bare filenames**; never prefix with `Output/`.

This skill does **not** use the GPU host raw audiocpp gateway. It talks to an
`audiocpp_server` the user runs on their own machine (or LAN), for loaders the GPU host
does not ship (e.g. Kokoro forks).

For the GPU host-hosted ASR/TTS/diarize/deferred catalog gaps, use the other audiocpp
skills with `AUDIOCPP_SKILL_BASE_URL` instead.

## Preflight

```bash
python3 Skills/audiocpp-host-tts/scripts/preflight.py --for host-tts
```

Resolution: `AUDIOCPP_ENGINE_URL`, else `http://host.docker.internal:8080`.

## Synthesize

`--model` is required:

```bash
python3 Skills/audiocpp-host-tts/scripts/engine_tool.py speech "Hello" \
  --engine-url http://host.docker.internal:8080 --model <engine-model-id> \
  -o hello.wav [--voice <id>] [--seed 42] [--language en] [--voice-ref ...]
```

Consent rule applies to `--voice-ref`.

## Hard limit: TTS only

Host `/v1/audio/transcriptions` needs host-local paths — sandbox files are not
visible. Use **audiocpp-asr** (GPU host gateway) for transcription.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Unreachable | Not bound to `0.0.0.0`, or firewall |
| `/health` 200 but no `/v1/audio/*` | Wrong process (e.g. llama-server) on that port |
| Model errors | Family/option mismatch — surface engine text |

## Reporting

End by telling the user what worked and what was blocked, quoting preflight evidence.
