---
name: session-ingestion-scripting
description: "Turn a recorded browser-session capture (screen + mic archive) into a context report, a timed transcript of the narration, and a TTS-ready script of the story the user told. Use when the user records a session and wants narration/script work for a video built from it."
metadata:
  guideants:
    enabled: true
    display_order: 45
    requires_toolsets: [sandbox]
---

# session-ingestion-scripting

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/<skill>/scripts/` relative to it, so run the commands in this file
exactly as written. Write every deliverable to the CWD with a **bare filename**
(e.g. `-o clip`, `-o scene.wav`): never prefix an output path with `Output/` —
the CWD *is* the output directory, so `Output/…` would create a nested
`Output/` folder.

First step of the video-production pipeline: take a `browser-session-capture`
session (a folder or `.zip`: `video.mp4`, `narration*.wav`, `index.json`,
`checkpoints/`, event JSONLs, verified `edit_map.json`) and produce

1. a **context report** (what was captured, what happened, which audio to use),
2. a **timed transcript** of the narration (delegated to
   `audiocpp-timed-transcript`), and
3. a **TTS-ready script** — the story the user told, in their words and order,
   cleaned for the TTS engine, with a full audit trail.

The scripting half is a **human-in-the-loop editing job**. The governing
contract is `references/tts-editor-brief.md` — read it before drafting and
treat it as binding. Its first rule: **when in doubt, ask; do not draft on a
guess.** The transcript does not define what the story is; the user does.

## Phase 1 — Ingest & report

```bash
python3 Skills/session-ingestion-scripting/scripts/session_report.py \
  <session-folder-or-zip> [-o report.md] [--json report.json]
```

- Stdlib only; read-only on session data. If given a `.zip`, pass
  `--extract-dir DIR` to unpack first (safe-extract: rejects path traversal).
- The report covers: identity/host/monitor/duration, media (video + narration
  candidates with sizes), verified compaction (`edit_map.json`), idle stats,
  checkpoint timeline (tab/URL/title/trigger), event kind counts, foreground
  window intervals, and which checkpoints carry text/screenshot/mhtml.
- Report the bottom line to the user in a few lines: what the session is, what
  the narration covers, which WAV to transcribe. Then ask the Phase-2 question.

## Phase 2 — Define the story (ask before drafting)

Before any transcript-dependent drafting, get the user's story definition. If
the session opener did not supply it, ask exactly this (and nothing else):

> In one paragraph: what is the story I told in this recording, and which
> parts of the transcript are part of it?

Rules from the brief: 1-3 questions max, numbered, at the end of a short
reply; wait for the answer; no "draft assuming X" moves. Log the answer as
decided — never re-ask it.

## Phase 3 — Transcribe the narration

Route to **audiocpp-timed-transcript** (`timed_transcribe.py`) on the
narration WAV the user confirms (usually `narration.compact.wav` — the
idle-compacted track that matches the compact video). Keep the outputs:
`.srt`, `.vtt`, `.json`, `.transcript.txt`, plus a derived clean paragraph
text. Quote the skill's evidence (engine status, segment/word counts, output
files) in the report to the user.

## Phase 4 — Draft the TTS script

Draft per `references/tts-editor-brief.md`:

- Output is ONE working `.md` file, e.g. `<topic>-tts-script.md` (in the CWD — no `Output/` prefix).
  Layout: `# TTS-READY` … `# END TTS-READY` spoken block, then **Removed**
  (verbatim, one-word reasons), **Kept beats**, **Corrections applied**.
- Permitted edits only: fillers, user-identified muttering, flagged
  navigation fumbles, user-confirmed ASR fixes, TTS hygiene. Never reorder,
  summarize, compress, or polish. No length, no structure, no production doc.
- Feedback rounds: edit the same file in place; log every correction.
- Before reporting a script (initial or revised), run:

```bash
python3 Skills/session-ingestion-scripting/scripts/verify_script.py \
  <script.md> \
  --removed "<phrase>" [--removed ...] \
  --beat "<phrase>" [--beat ...]
```

  It prints a JSON verdict (`open`, `blockers`, `warnings`, `evidence`:
  marker integrity, spoken word count, removed-phrases absent, kept-beats
  present). Fix blockers; quote the verdict in the reply.

## Out of scope

- TTS audio generation (next skill in this set; routes to audiocpp TTS).
- Video assembly, caption burn-in, thumbnails, publishing.
- Any editorial decision about the story — that belongs to the user.

## References

- `references/tts-editor-brief.md` — the binding editor contract (ask-first,
  voice preservation, permitted edits, file discipline, anti-patterns).
- `references/session-archive.md` — anatomy of a `browser-session-capture`
  session folder and which files matter for each phase.

## Reporting

End with: the report path, transcript evidence (engine status + counts), the
current script file name (unambiguously), and the `verify_script.py` verdict.
If blocked, quote the blocker evidence. No scene tables, no length options,
no "suggested next steps" about content the user did not ask for.
