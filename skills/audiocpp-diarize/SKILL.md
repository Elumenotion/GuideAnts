---
name: audiocpp-diarize
description: "Speaker diarization: figure out who spoke when in a recording and produce a speaker-labeled transcript, by spawning a private audio.cpp engine with the sortformer diarization model and labeling each turn with the local ASR engine. Use when the user asks to diarize a meeting or call recording, separate or identify speakers, or wants a transcript broken down by speaker."
metadata:
  guideants:
    enabled: true
    display_order: 34
    requires_toolsets: [sandbox]
---

# audio.cpp speaker diarization (experimental)

GuideAnts has no local diarization (only its Azure cloud transcription provider
does it), but the container's `audiocpp_server` binary ships the
`sortformer_diar` loader and a generic `/v1/tasks/run` route. This skill
downloads the model once, spawns a private engine, and turns any recording into
a speaker-labeled transcript. Deliverables land in `Output/`; the live voice
path is untouched. E2E-verified in this stack (2026-07-15).

## Preflight

```bash
python3 Output/Skills/audiocpp-diarize/scripts/preflight.py --for diarize
```

Trust its verdict over this document. It also tells you whether the model is
already on disk (skip the download) and whether ASR labeling is available.

## The recipe

```bash
# 1. Download the model — skip if preflight says modelAlreadyDownloaded
python3 Output/Skills/audiocpp-diarize/scripts/fetch_model.py nvidia/diar_sortformer_4spk-v1 \
  --dest /models-local/asr/diar_sortformer_4spk-v1 --exclude diar_sortformer_4spk-v1.nemo

# 2. Spawn the private engine (loads in seconds; ~494 MB weights, fits next to
#    the wrappers' models without an unload)
python3 Output/Skills/audiocpp-diarize/scripts/spawn_engine.py start \
  --path /models-local/asr/diar_sortformer_4spk-v1 --family sortformer_diar --task diar

# 3. Diarize (any ffmpeg-decodable input)
python3 Output/Skills/audiocpp-diarize/scripts/diarize.py Output/uploads/meeting.mp3 -o Output/meeting

# 4. Always stop the engine when done
python3 Output/Skills/audiocpp-diarize/scripts/spawn_engine.py stop
```

`diarize.py` converts the input to the 16 kHz mono WAV the engine requires
(the raw engine does not resample), fetches speaker turns, merges/filters them,
and — when the GuideAnts ASR engine is up — transcribes each turn. Outputs:
`<base>.transcript.txt` (speaker-labeled, timestamped) and
`<base>.diarization.json` (structured turns with confidences).

## Options that matter

- `--turns-only` — skip ASR labeling (or it degrades to this automatically,
  with a note, when no ASR model is loaded).
- `--language <hint>` — passed to the ASR engine per turn.
- `--merge-gap-seconds` / `--min-turn-seconds` — shape the output without
  touching the engine.
- Engine-side tuning needs a re-spawn with e.g.
  `--option speaker_threshold=0.4` (also: `speaker_min_frames`,
  `speaker_pad_frames`, `session_len_sec`).

## Limits — say these to the user when relevant

- Offline only; **at most 4 speakers** (model variant).
- Speaker ids (`SPEAKER_00`…) are arbitrary labels — ask the user who is who
  if they want real names in the deliverable.
- Long recordings: per-turn labeling can hit the ~5-minute script budget; the
  JSON then has `"labeling": {"partial": true}` — rerun `diarize.py` (the
  diarization pass itself is fast; only labeling resumes slowly).

## Reporting

End by telling the user what worked and what was blocked, quoting the
preflight/engine evidence.
