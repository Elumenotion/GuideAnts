---
name: audiocpp-timed-transcript
description: "Industry-standard time-coded transcripts (SRT/WebVTT/JSON) via GPU host AudioCPP: product ASR for text + private qwen3_forced_aligner (/v1/tasks/run) for word times, collapsed to caption cues. Use when the user wants timed captions without PyTorch."
metadata:
  guideants:
    enabled: true
    display_order: 35
    requires_toolsets: [sandbox]
---

# audio.cpp time-coded transcripts (experimental)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/audiocpp-timed-transcript/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o meeting`); never prefix with `Output/`.

Product ASR returns text only. Word timings come from AudioCPP’s
`qwen3_forced_aligner` over the GPU host raw gateway (`/private/v1/tasks/run`), then
words are grouped into **SRT / WebVTT / segment JSON** (industry caption formats).

Authority: upstream [audio.cpp](https://github.com/0xShug0/audio.cpp)
`model_specs/qwen3_forced_aligner.json`, `docs/models/qwen3.md`,
`app/server/runtime.cpp` (`/v1/audio/transcriptions` is text-only;
timed fields are on `/v1/tasks/run`).

## Environment (PC → the GPU host)

```text
AUDIOCPP_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as the GPU host GA_AUDIOCPP_SKILL_TOKEN>
```

ASR must already be loaded on the GPU host (Settings / API lifecycle).

## Phase 0 gate (once per GPU host image)

```bash
python3 Skills/audiocpp-timed-transcript/scripts/fetch_model.py \
  audio-cpp/audio.cpp-gguf \
  --dest /models-local/skill/asr/Qwen3-ForcedAligner-0.6B-GGUF \
  --include Qwen3-ForcedAligner-0.6B-GGUF/qwen3-forced-aligner-0.6b-q8_0.gguf \
  --strip-prefix Qwen3-ForcedAligner-0.6B-GGUF/

# ROCm host builds need --backend rocm (not cuda)
python3 Skills/audiocpp-timed-transcript/scripts/spawn_engine.py start \
  --path /models-local/skill/asr/Qwen3-ForcedAligner-0.6B-GGUF \
  --family qwen3_forced_aligner --task align --backend rocm

python3 Skills/audiocpp-timed-transcript/scripts/spawn_engine.py status
```

If status is not `ready` (e.g. missing model spec), update the GPU host `guideants-ai`
audiocpp image. Do not add a PyTorch aligner.

## Transcribe

```bash
python3 Skills/audiocpp-timed-transcript/scripts/timed_transcribe.py \
  uploads/clip.wav -o clip --language English
```

Writes `clip.srt`, `clip.vtt`, `clip.json`, `clip.transcript.txt`.

When finished: `spawn_engine.py stop`.

## Limits

- Standalone ForcedAligner does **not** chunk internally; this script chunks at
  **30 s** by default (matches Qwen3 ASR `audio_chunk_seconds`). Cap `--max-chunk-s`
  at 60 — longer passes hit `max_source_positions`.
- Long-form: raise `--budget-seconds` (e.g. 900) for multi-chunk runs.
- Aligner languages: 11 (pass `--language` explicitly).
- Cue text is grouped from word times (~3–8 s); not Whisper-native segment IDs.
- ROCm hosts: `spawn_engine.py start ... --backend rocm`.
- Multi-model private engine (align+diar): `spawn_engine.py start ... --extra path=...;family=...;task=...`
  requires the updated skill gateway on the GPU host (Gate M).

## Related

Speaker-labeled timed captions: **audiocpp-diarize** (`diarize.py` + `merge_diarized.py`).

## Reporting

End by quoting spawn/status evidence and which output files were written.
