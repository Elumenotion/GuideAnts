# Video production skills

Experimental GuideAnts skills that turn **recorded sessions** (the
`browser-session-capture` archives: screen video + mic narration + page
checkpoints) into finished videos. Each skill owns one step of the pipeline; deliverables are written to the
sandbox CWD (the notebook's output directory) with bare filenames — never
prefixed `Output/`, because the CWD *is* the output directory.

## Pipeline

```text
session archive (.zip / folder)
  -> 1. session-ingestion-scripting   context report + timed transcript + TTS-ready script   (this set)
  -> 2. TTS narration audio           (planned: routes to the audiocpp TTS skills)
  -> 3. video assembly                (planned: narration over session footage, per edit map)
```

## Skills

| Skill | What it does |
|-------|--------------|
| [`session-ingestion-scripting`](session-ingestion-scripting/) | Ingest a session capture (verify + context report), transcribe the narration via `audiocpp-timed-transcript`, and produce a **TTS-ready script** of the story the user told — under the editor contract in `references/tts-editor-brief.md`. |

## Common rules

- **Ask before acting.** If the story/scope is ambiguous, the assistant asks 1-3
  short questions and waits — it does not draft on a guess. The transcript alone
  does not define the story; the user does.
- **One working file per step.** Feedback is applied in place; stale versions are
  not pointed back at the user.
- **Verify before reporting.** Mechanical checks are run (`session_report.py`,
  `verify_script.py`) and the evidence is quoted, not asserted.
- **The assistant is an editor, not a director.** No length targets, no
  restructuring, no production-doc artifacts in the script file.
- Report honestly what worked and what was blocked.
