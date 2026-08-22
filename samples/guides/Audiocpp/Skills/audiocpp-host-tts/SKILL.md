---
name: audiocpp-host-tts
description: "Synthesize against the user's own host-native audiocpp_server (outside Max / GuideAnts containers) — including families the Max binary lacks. TTS only. Use when the user runs their own audio.cpp build."
metadata:
  guideants:
    enabled: true
    display_order: 36
    requires_toolsets: [sandbox]
---

# audio.cpp host-native engine (experimental)

This skill does **not** use the Max raw audiocpp gateway. It talks to an
`audiocpp_server` the user runs on their own machine (or LAN), for loaders Max
does not ship (e.g. Kokoro forks).

For Max-hosted ASR/TTS/diarize/deferred catalog gaps, use the other audiocpp
skills with `AUDIOCPP_SKILL_BASE_URL` instead.

## Preflight

```bash
python3 Output/Skills/audiocpp-host-tts/scripts/preflight.py --for host-tts
```

Resolution: `AUDIOCPP_ENGINE_URL`, else `http://host.docker.internal:8080`.

## Synthesize

`--model` is required:

```bash
python3 Output/Skills/audiocpp-host-tts/scripts/engine_tool.py speech "Hello" \
  --engine-url http://host.docker.internal:8080 --model <engine-model-id> \
  -o Output/hello.wav [--voice <id>] [--seed 42] [--language en] [--voice-ref ...]
```

Consent rule applies to `--voice-ref`.

## Hard limit: TTS only

Host `/v1/audio/transcriptions` needs host-local paths — sandbox files are not
visible. Use **audiocpp-asr** (Max gateway) for transcription.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Unreachable | Not bound to `0.0.0.0`, or firewall |
| `/health` 200 but no `/v1/audio/*` | Wrong process (e.g. llama-server) on that port |
| Model errors | Family/option mismatch — surface engine text |

## Reporting

End by telling the user what worked and what was blocked, quoting preflight evidence.
