---
name: audiocpp-diarize
description: "Speaker diarization on the GPU host via Sortformer (audiocpp /private): who-spoke-when for meetings/calls, including long audio via overlapping windows + speaker-ID stitch. Optional merge with audiocpp-timed-transcript for speaker-labeled SRT/VTT/RTTM. Use when the user wants speakers, not plain captions alone."
metadata:
  guideants:
    enabled: true
    display_order: 34
    requires_toolsets: [sandbox]
---

# audio.cpp speaker diarization (experimental)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/audiocpp-diarize/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o meeting`); never prefix with `Output/`.

GuideAnts has no product local diarization. This skill uses the GPU host container’s
`sortformer_diar` loader through the **GPU host raw audiocpp gateway** from a PC
sandbox (transparent `/private` + `/files` + optional `/asr` labeling). Deliverables are written to the CWD with bare filenames — never prefix with `Output/`.

## Environment (required for PC → the GPU host)

```text
AUDIOCPP_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as the GPU host GA_AUDIOCPP_SKILL_TOKEN>
```

Optional: `HF_TOKEN` if the HF repo is gated. For labeled transcripts, load ASR
on the GPU host as well (Settings / API lifecycle).

## Preflight

```bash
python3 Skills/audiocpp-diarize/scripts/preflight.py --for diarize
```

With the gateway env set, preflight checks the GPU host (route 5), not sandbox loopback.

## Recipe

Paths below are rewritten to `/models-local/skill/…` on the GPU host when the gateway is
configured — pass them as written. On ROCm hosts, pass `--backend rocm`. Match
`session_len_sec` to the diarize window (default window is 100 s; model max ≈ 120 s):

```bash
python3 Skills/audiocpp-diarize/scripts/fetch_model.py nvidia/diar_sortformer_4spk-v1 \
  --dest /models-local/asr/diar_sortformer_4spk-v1 --exclude diar_sortformer_4spk-v1.nemo

python3 Skills/audiocpp-diarize/scripts/spawn_engine.py start \
  --path /models-local/asr/diar_sortformer_4spk-v1 --family sortformer_diar --task diar \
  --backend rocm --option session_len_sec=100

python3 Skills/audiocpp-diarize/scripts/diarize.py uploads/meeting.mp3 -o meeting

python3 Skills/audiocpp-diarize/scripts/spawn_engine.py stop
```

`diarize.py` uploads audio to the GPU host, runs `/v1/tasks/run` (overlapping windows when
needed), optionally labels turns with GPU host ASR. Outputs: `<base>.transcript.txt`
and `<base>.diarization.json`.

## Timed captions + speakers (word-midpoint merge)

For industry SRT/VTT with speaker labels, run timed transcription first, then
diarize turns, then merge:

```bash
# 1) words + cues (private ForcedAligner; product ASR)
python3 Skills/audiocpp-timed-transcript/scripts/timed_transcribe.py meeting.wav \
  -o meeting --language English --budget-seconds 900

# 2) switch private engine to Sortformer (or Gate M multi-model when gateway supports --extra)
python3 Skills/audiocpp-diarize/scripts/spawn_engine.py stop
python3 Skills/audiocpp-diarize/scripts/spawn_engine.py start \
  --path /models-local/asr/diar_sortformer_4spk-v1 --family sortformer_diar --task diar \
  --backend rocm --option session_len_sec=100

python3 Skills/audiocpp-diarize/scripts/diarize.py meeting.wav -o meeting --turns-only

# 3) word midpoint → speaker; writes RTTM + speaker SRT/VTT/JSON
python3 Skills/audiocpp-diarize/scripts/merge_diarized.py \
  --words-json meeting.json \
  --turns-json meeting.diarization.json \
  -o meeting_speakers
```

Gate M (align + diar co-loaded): after the GPU host ships the multi-model gateway,

```bash
python3 .../spawn_engine.py start \
  --path /models-local/skill/asr/Qwen3-ForcedAligner-0.6B-GGUF \
  --family qwen3_forced_aligner --task align --backend rocm \
  --extra path=/models-local/asr/diar_sortformer_4spk-v1;family=sortformer_diar;task=diar
```

## Options

- `--turns-only` — skip ASR labeling.
- `--language <hint>` — ASR hint per turn.
- `--window-seconds` / `--overlap-seconds` — long-audio overlap stitch (defaults 100 / 30).
- `--merge-gap-seconds` / `--min-turn-seconds` — shape turns.
- Re-spawn with `--option speaker_threshold=0.4` (etc.) for engine tuning.

## Limits

- Offline Sortformer only; **at most 4 speakers**.
- **Per-window ceiling ≈ 120 s** (`tf_encoder.max_source_positions=1500`).
  Default spawn builds a **20 s** graph — for long audio re-spawn with a window
  that fits:

  ```bash
  python3 .../spawn_engine.py start ... --family sortformer_diar --task diar \
    --backend rocm --option session_len_sec=100
  ```

- **Longer than one window:** `diarize.py` uses **overlapping windows**
  (`--window-seconds` default 100, `--overlap-seconds` default 30) and remaps
  `SPEAKER_*` IDs by agreement in the overlap. This is **not** hard-cut
  chunking — hard cuts without overlap stitch destroy cross-speaker contrast.
- `SPEAKER_00`… are arbitrary labels — ask the user for real names if needed.
- Long files may hit the ~5-minute script budget during ASR labeling (`partial: true`);
  re-run `diarize.py`.

## Reporting

End by telling the user what worked and what was blocked, quoting preflight evidence.
