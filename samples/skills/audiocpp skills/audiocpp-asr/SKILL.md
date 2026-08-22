---
name: audiocpp-asr-extended
description: "Transcription beyond the GuideAnts wrapper contract: language hints and workspace files via the Max raw audiocpp gateway (/asr + /files) from a PC sandbox. Use when a transcription needs a language hint or the built-in ASR tool is too narrow."
metadata:
  guideants:
    enabled: true
    display_order: 33
    requires_toolsets: [sandbox]
---

# audio.cpp extended ASR (experimental)

Product ASR is multipart upload with no language control. This skill stages the
file (`/files`) then calls raw `/asr/v1/audio/transcriptions` on Max.

## Environment (required for PC → Max)

```text
AUDIOCPP_SKILL_BASE_URL=http://<max-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as Max GA_AUDIOCPP_SKILL_TOKEN>
```

An ASR model must already be loaded on Max (GuideAnts Settings → Local models /
API lifecycle).

## Preflight

```bash
python3 Output/Skills/audiocpp-asr-extended/scripts/preflight.py --for asr-extended
```

Trust its verdict. With the gateway env set, it checks Max ASR via the gateway
(not sandbox loopback).

## Transcribe

```bash
python3 Output/Skills/audiocpp-asr-extended/scripts/engine_tool.py transcribe \
  Output/uploads/clip.wav [--language de]
```

The script stages the file on Max and returns JSON text. Engine model id is
always `qwen3-asr`. Prefer WAV; other formats may need ffmpeg conversion first.

## Sideload (advanced)

Fetching a non-catalog qwen3-family snapshot downloads onto Max under
`/models-local/skill/asr/…`. Loading it into the **product** ASR wrapper still
requires Max-side `/asr/admin/load` (and replaces the user’s loaded model) —
ask before doing that; prefer Settings / API lifecycle for durable loads.

```bash
python3 Output/Skills/audiocpp-asr-extended/scripts/fetch_model.py <hf-repo-id> \
  --dest /models-local/asr/<DirName>
```

## Related

Speaker-labeled transcripts: **audiocpp-diarize**.

## Reporting

End by telling the user what worked and what was blocked, quoting preflight evidence.
