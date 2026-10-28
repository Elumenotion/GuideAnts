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

**Model paths are GPU-host paths, not sandbox paths.** `/models-local/...` is on
the GPU host. Do not `ls`/`test` it from sandbox commands — the sandbox carries
its own empty `/models-local`, so a failing `ls` is not evidence the model is
missing. `spawn_engine.py status` is the only readiness check.

Product ASR returns text only. Word timings come from AudioCPP's
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

## Engine lifecycle (every run)

`timed_transcribe.py` **requires** the private aligner engine to be `ready`.
The engine is ephemeral: it is `dead` at the start of every session. That is
normal, not a setup failure — just start it.

```bash
python3 Skills/audiocpp-timed-transcript/scripts/spawn_engine.py status
# dead -> start it (the model is already on the host):
python3 Skills/audiocpp-timed-transcript/scripts/spawn_engine.py start \
  --path /models-local/skill/asr/Qwen3-ForcedAligner-0.6B-GGUF \
  --family qwen3_forced_aligner --task align --backend rocm
# ready -> proceed to Transcribe.
```

ROCm host builds need `--backend rocm` (not cuda).

When finished: `spawn_engine.py stop`.

## One-time model fetch (only if `start` reports the model missing on the host)

`fetch_model.py` downloads **to the GPU host** via the gateway. Run it only when
`spawn_engine.py start` fails because the model directory is absent there — never
on the strength of a sandbox `ls`.

```bash
python3 Skills/audiocpp-timed-transcript/scripts/fetch_model.py \
  audio-cpp/audio.cpp-gguf \
  --dest /models-local/skill/asr/Qwen3-ForcedAligner-0.6B-GGUF \
  --include Qwen3-ForcedAligner-0.6B-GGUF/qwen3-forced-aligner-0.6b-q8_0.gguf \
  --strip-prefix Qwen3-ForcedAligner-0.6B-GGUF/
# then spawn_engine.py start again
```

If the engine still does not reach `ready` after a successful fetch (e.g. a
missing model spec in the image), update the GPU host `guideants-ai` audiocpp
image. Do not add a PyTorch aligner.

## Transcribe

With the engine `ready`:

```bash
python3 Skills/audiocpp-timed-transcript/scripts/timed_transcribe.py \
  uploads/clip.wav -o clip --language English
```

Writes `clip.srt`, `clip.vtt`, `clip.json`, `clip.transcript.txt`.

Model ID: `Qwen3-ForcedAligner-0.6B-GGUF` (auto-detected while the engine is
`ready`). If `timed_transcribe.py` fails with `"Private engine status has no
modelId; pass --model explicitly."`, the engine is not `ready` — go back to
Engine lifecycle (or pass `--model Qwen3-ForcedAligner-0.6B-GGUF` explicitly).

## Limits

- Standalone ForcedAligner does **not** chunk internally; this script chunks at
  **30 s** by default (matches Qwen3 ASR `audio_chunk_seconds`). Cap `--max-chunk-s`
  at 60 — longer passes hit `max_source_positions`.
- Long-form: raise `--budget-seconds` (e.g. 900) for multi-chunk runs.
- Aligner languages: 11 (pass `--language` explicitly).
- Cue text is grouped from word times (~3–8 s); not Whisper-native segment IDs.
- Multi-model private engine (align+diar): `spawn_engine.py start ... --extra path=...;family=...;task=...`
  requires the updated skill gateway on the GPU host (Gate M).

## Related

Speaker-labeled timed captions: **audiocpp-diarize** (`diarize.py` + `merge_diarized.py`).

## Reporting

End by quoting spawn/status evidence and which output files were written.
