---
name: audiocpp-diarize
description: "Speaker diarization on Max via Sortformer (audiocpp /private): who-spoke-when for meetings/calls, including long audio via overlapping windows + speaker-ID stitch. Optional merge with audiocpp-timed-transcript for speaker-labeled SRT/VTT/RTTM. Use when the user wants speakers, not plain captions alone."
metadata:
  guideants:
    enabled: true
    display_order: 34
    requires_toolsets: [sandbox]
---

# audio.cpp speaker diarization (experimental)

GuideAnts has no product local diarization. This skill uses the Max container’s
`sortformer_diar` loader through the **Max raw audiocpp gateway** from a PC
sandbox (transparent `/private` + `/files` + optional `/asr` labeling). Deliverables land in `Output/`.

## Environment (required for PC → Max)

```text
AUDIOCPP_SKILL_BASE_URL=http://<max-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as Max GA_AUDIOCPP_SKILL_TOKEN>
```

Optional: `HF_TOKEN` if the HF repo is gated. For labeled transcripts, load ASR
on Max as well (Settings / API lifecycle).

## Preflight

```bash
python3 Output/Skills/audiocpp-diarize/scripts/preflight.py --for diarize
```

With the gateway env set, preflight checks Max (route 5), not sandbox loopback.

## Recipe

Paths below are rewritten to `/models-local/skill/…` on Max when the gateway is
configured — pass them as written. On Max ROCm, pass `--backend rocm`. Match
`session_len_sec` to the diarize window (default window is 100 s; model max ≈ 120 s):

```bash
python3 Output/Skills/audiocpp-diarize/scripts/fetch_model.py nvidia/diar_sortformer_4spk-v1 \
  --dest /models-local/asr/diar_sortformer_4spk-v1 --exclude diar_sortformer_4spk-v1.nemo

python3 Output/Skills/audiocpp-diarize/scripts/spawn_engine.py start \
  --path /models-local/asr/diar_sortformer_4spk-v1 --family sortformer_diar --task diar \
  --backend rocm --option session_len_sec=100

python3 Output/Skills/audiocpp-diarize/scripts/diarize.py Output/uploads/meeting.mp3 -o Output/meeting

python3 Output/Skills/audiocpp-diarize/scripts/spawn_engine.py stop
```

`diarize.py` uploads audio to Max, runs `/v1/tasks/run` (overlapping windows when
needed), optionally labels turns with Max ASR. Outputs: `<base>.transcript.txt`
and `<base>.diarization.json`.

## Timed captions + speakers (word-midpoint merge)

For industry SRT/VTT with speaker labels, run timed transcription first, then
diarize turns, then merge:

```bash
# 1) words + cues (private ForcedAligner; product ASR)
python3 Output/Skills/audiocpp-timed-transcript/scripts/timed_transcribe.py meeting.wav \
  -o Output/meeting --language English --budget-seconds 900

# 2) switch private engine to Sortformer (or Gate M multi-model when gateway supports --extra)
python3 Output/Skills/audiocpp-diarize/scripts/spawn_engine.py stop
python3 Output/Skills/audiocpp-diarize/scripts/spawn_engine.py start \
  --path /models-local/asr/diar_sortformer_4spk-v1 --family sortformer_diar --task diar \
  --backend rocm --option session_len_sec=100

python3 Output/Skills/audiocpp-diarize/scripts/diarize.py meeting.wav -o Output/meeting --turns-only

# 3) word midpoint → speaker; writes RTTM + speaker SRT/VTT/JSON
python3 Output/Skills/audiocpp-diarize/scripts/merge_diarized.py \
  --words-json Output/meeting.json \
  --turns-json Output/meeting.diarization.json \
  -o Output/meeting_speakers
```

Gate M (align + diar co-loaded): after Max ships the multi-model gateway,

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
