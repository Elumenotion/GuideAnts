---
name: video-timeline
description: "Turn a final TTS audio track (with its timed transcript) into an editable story timeline (xlsx) for a human to fill with images, slides, and video clips. Audits the audio against timing best practices first, then maps each finding to a visual offset, and QA-scores the timeline objectively. Use when a finished narration exists and the picture layer needs to be planned."
metadata:
  guideants:
    enabled: true
    display_order: 50
    requires_toolsets: [sandbox]
---

# video-timeline

Third step of the video-production pipeline (after
`session-ingestion-scripting` and TTS generation). Paths — fixed layout, do
not probe. The sandbox CWD is the notebook's **output directory**; write every
deliverable to the CWD with a **bare filename** (never prefix `Output/`).
Scripts live under `Skills/<skill>/scripts/` relative to the CWD — run the
commands in this file exactly as written.

Requires `openpyxl` (for the .xlsx deliverable); everything else is stdlib.
`pip install openpyxl` if missing.

## What this skill produces — and what it deliberately is not

The deliverable is a **story document a human edits**: one xlsx, one row per
story moment, with dropdowns for visual type (New Image / Slide / Video Clip /
Session B-roll / End Card / Hold), an empty **Asset/file** column the user
fills, and a Status workflow (To-do → Have → Verify → Done).

It is **not** a frame map. Extracting frames, aligning raw-footage spans, and
building storyboards are optional *internal means* to inform suggestions —
never the deliverable. The user's standing instruction: *help me build the
visual timeline to tell a story, not to map the video frames.*

Learnings that are binding for this skill (from the 2026-08-27 intro-video
session — do not regress):

1. **Audit the audio first.** The picture plan is driven by measured numbers
   (WPM per 30-s bucket, the real pause ladder, 0-s/1-s TTS seams, missing
   tail), not by taste.
2. **Rows are story moments, chained with no gaps** from 0:00 to audio-end +
   a 1.5–2.5 s tail pad. Split or merge freely; keep the chain unbroken.
3. **"What Doug says" is a plain-English summary** of the narration at that
   time — never a verbatim transcript dump, never longer than ~60 words.
4. **The assistant proposes, the user decides.** Pre-fill Visual type + What to
   show as *suggestions*; the user owns the asset choices.
5. **QA is scripted, not vibes.** `qa_timeline.py` produces a 100-point score
   with per-check evidence; quote it, do not assert.

## Inputs

| Input | Required | Notes |
|---|---|---|
| `narration.json` | yes | final TTS timing: `duration`, `segments` (id/start/end/text) |
| `narration.transcript.txt` | yes | word-timed transcript (`[  0.000 ->   0.320] word`) — drives the pause ladder and long-run detection |
| `narration.wav` | optional | for level checks (peak dBFS) |
| raw session archive (step-A inputs) | optional | raw `video.mp4` + alignment map — only needed to suggest Session B-roll spans |
| beat map (e.g. Step-A alignment JSON) | optional | explicit beats; if absent, beats are derived automatically |

## Phase 1 — Audio audit (before any timeline)

Compute and report against `references/timing-best-practices.md`:

- duration, spoken word count, overall WPM
- WPM per 30-s and 60-s buckets (find the rushed/flat zones)
- **pause ladder** from the word stream: counts of gaps >0.5 / >1.0 / >2.0 s,
  and the top pauses with timestamps + surrounding words — these are the only
  places a visual breather can land
- **seams**: 0-s and 1-s segments (TTS chunk artifacts) → they get no shot
  of their own; adjacent visual holds across them
- head/tail silence (TTS usually has none → tail pad is mandatory)
- peak/RMS (headroom for a music bed)

State each number next to its threshold and name the pass/fail. This audit
drives Phase 3; write it into the xlsx's **Audit** sheet so it travels with
the file.

## Phase 2 — Build the editable timeline

```bash
python3 Skills/video-timeline/scripts/build_timeline.py \
  --transcript <narration.json> \
  --words <narration.transcript.txt> \
  [--beats <alignment.json>] \
  --out <name>.xlsx [--tail 2.0]
```

Stdlib + openpyxl. It derives beats (from `--beats` if given, else from
segments: break on gaps >0.6 s, cap ~30 s, merge slivers <2 s), groups beats
into acts (first act ~30–90 s, later acts ~60–210 s), chains rows with no
gaps, appends the tail-pad row, and writes the exact schema in
`references/timeline-format.md` (columns, dropdowns, role colors, an **Audit**
sheet, a short **How to use** sheet).

After generation, do the **assistant pass**: rewrite column G in plain English
(≤60 words each), fill column C (Moment names, 3–6 words) and column J
(What to show) with real suggestions, and mark rows that need new capture.
Suggest Visual types for what the user actually works in: **New Image, Slide,
Video Clip** first; Session B-roll only where raw footage genuinely fits.

## Phase 3 — Offset plan (findings → devices)

Map every Phase-1 finding to a concrete row/device, and say so in the Notes
column (e.g. `F1: card lands on the real 4:08 pause`):

| Audio finding | Visual device |
|---|---|
| fast WPM, no breathers | text beats (cards/slides) placed on the real pauses; music-bed duck points listed in Notes |
| long continuous run (>60 s, e.g. a 132-s beat) | split into shots at 7–10 s cadence; an inner re-hook card mid-run |
| 0-s / 1-s seam segments | no shot of their own; Hold across the seam |
| no tail in audio | 1.5–2.5 s tail-pad row for the fade |
| weak opening | strong title card behind the first lines |
| 40–55% slump zone | a pivot/re-hook row (chapter card) inside it |
| end-screen (last 20 s) | End Card + one CTA target only; CTA synced to the spoken mention |

## Phase 4 — QA with objective scoring

```bash
python3 Skills/video-timeline/scripts/qa_timeline.py \
  --timeline <name>.xlsx \
  --transcript <narration.json> \
  [--words <narration.transcript.txt>]
```

Prints a JSON verdict: **100-point score, letter grade, per-check
score/weight/evidence, and a fix list**. Grades: **A ≥ 90, B ≥ 75,
C ≥ 60, D < 60**. The 12 checks and their exact thresholds are in
`references/timing-best-practices.md` §QA rubric — the script and the
reference must stay in agreement; if you change one, change both.

**Definition of done:** grade ≥ B **and** no hard fail (hard fails: any
0-s row, any overlap >0.5 s, any uncovered audio, missing tail pad). Fix the
top failing checks, re-run, and repeat. Cite the JSON in the reply — quote
the score, the grade, and the top 3 fixes.

Feedback rounds: the user edits the xlsx in place (assets, types, splits).
Re-run QA after every round and quote the new score. One working file — name
it in every reply.

## Out of scope

- Audio: re-TTS, speed changes, caption files (separate audio-track work).
- Rendering/burning the final video, thumbnails, publishing.
- Editorial decisions about the story — the user owns those; this skill
  proposes, it does not direct (same contract as
  `session-ingestion-scripting/references/tts-editor-brief.md`).

## References

- `references/timing-best-practices.md` — the timing thresholds (WPM, shot
  cadence, chapters, re-hooks, payoffs, opening/ending) **and the QA rubric**.
- `references/timeline-format.md` — the exact xlsx schema: columns,
  dropdown values, story-role taxonomy, chaining rules, status workflow.

## Reporting

End with: the xlsx path, the Phase-1 audit bottom line (3–5 numbers with
pass/fail), the current QA **score + grade + top 3 fixes** (quoted from the
JSON), and the count of rows by visual type / status. If blocked, quote the
blocker evidence. No frame tables, no storyboards, unless the user asks.
