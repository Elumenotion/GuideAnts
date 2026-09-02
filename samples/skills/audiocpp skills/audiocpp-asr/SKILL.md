---
name: audiocpp-asr-extended
description: "Transcription beyond the GuideAnts wrapper contract: language hints and workspace files via the GPU host raw audiocpp gateway (/asr + /files) from a PC sandbox. Use when a transcription needs a language hint or the built-in ASR tool is too narrow."
metadata:
  guideants:
    enabled: true
    display_order: 33
    requires_toolsets: [sandbox]
---

# audio.cpp extended ASR (experimental)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/audiocpp-asr/scripts/` relative to it. Write deliverables with
**bare filenames**; never prefix with `Output/`.

Product ASR is multipart upload with no language control. This skill stages the
file (`/files`) then calls raw `/asr/v1/audio/transcriptions` on the GPU host.

## Environment (required for PC → the GPU host)

```text
AUDIOCPP_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as the GPU host GA_AUDIOCPP_SKILL_TOKEN>
```

An ASR model must already be loaded on the GPU host (GuideAnts Settings → Local models /
API lifecycle).

## Preflight

```bash
python3 Skills/audiocpp-asr-extended/scripts/preflight.py --for asr-extended
```

Trust its verdict. With the gateway env set, it checks GPU host ASR via the gateway
(not sandbox loopback).

## Transcribe

```bash
python3 Skills/audiocpp-asr-extended/scripts/engine_tool.py transcribe \
  uploads/clip.wav [--language de]
```

The script stages the file on the GPU host and returns JSON text. Engine model id is
always `qwen3-asr`. Prefer WAV; other formats may need ffmpeg conversion first.

## Sideload (advanced)

Fetching a non-catalog qwen3-family snapshot downloads onto the GPU host under
`/models-local/skill/asr/…`. Loading it into the **product** ASR wrapper still
requires GPU host-side `/asr/admin/load` (and replaces the user’s loaded model) —
ask before doing that; prefer Settings / API lifecycle for durable loads.

```bash
python3 Skills/audiocpp-asr-extended/scripts/fetch_model.py <hf-repo-id> \
  --dest /models-local/asr/<DirName>
```

## Related

Speaker-labeled transcripts: **audiocpp-diarize**.

## Reporting

End by telling the user what worked and what was blocked, quoting preflight evidence.
