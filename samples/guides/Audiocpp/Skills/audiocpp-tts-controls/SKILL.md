---
name: audiocpp-tts-controls
description: "Advanced synthesis on the loaded Max TTS model via the raw audiocpp gateway (/tts): seed, language, voice-design instructions, and builtin speakers — none of which the built-in GuideAnts audio tools expose."
metadata:
  guideants:
    enabled: true
    display_order: 32
    requires_toolsets: [sandbox]
---

# audio.cpp synthesis controls (experimental)

Product TTS is `{text, voice, speed}`. This skill calls raw
`/tts/v1/audio/speech` on Max. Deliverables are WAVs in `Output/`.

## Environment (required for PC → Max)

```text
AUDIOCPP_SKILL_BASE_URL=http://<max-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as Max GA_AUDIOCPP_SKILL_TOKEN>
```

A TTS model must already be loaded on Max via GuideAnts Settings.

## Long-form single-speaker narration (video scripts)

**Skill users:** write the final narration script as plain text (markdown headers
ok) and call `narration.py`. Do **not** chunk, concat, trim silence, or run
preflight yourself — this tool owns all of that.

Preflight runs inside `start`. Each `step` synthesizes **one** chunk and returns
immediately so the producer can poll without hitting sandbox timeouts. Per-chunk
HTTP synthesis timeouts are **hours-scale** (default 4h); the producer only
waits on short poll calls.

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/narration.py start script.txt \
  -o Output/narration.wav \
  --voice doug

python3 Output/Skills/audiocpp-tts-controls/scripts/narration.py step Output/narration.wav
python3 Output/Skills/audiocpp-tts-controls/scripts/narration.py status Output/narration.wav
```

Repeat `step` (or check `status`) until `"status": "done"`. Job state lives in
`.audiocpp-narration/` beside the output.

### Chunking policy (implementation — do not replicate manually)

Measured on Max + chatterbox + voice-pack clone (~186 wpm):

| Heuristic | Default | Meaning |
|-----------|---------|---------|
| `words_per_second` | 3.1 | Estimated audio duration from word count |
| `max_chunk_seconds` | 95 | Split when a segment would exceed this |
| `synthesis_timeout` | 4 hours | HTTP timeout per chunk synthesis call |

When chunking is required, splits happen on **sentence boundaries** (then
clauses, only if a single sentence still exceeds the budget). Join healing on
concat: trim leading silence on each segment; **do not trim trailing** except on
the last segment (preserves word endings like “build on”); insert **150ms**
between segments (`AUDIOCPP_TTS_INTER_CHUNK_PAUSE_MS`).

Override heuristics via env only when re-tuning the device:
`AUDIOCPP_TTS_WORDS_PER_SECOND`, `AUDIOCPP_TTS_MAX_CHUNK_SECONDS`,
`AUDIOCPP_TTS_SYNTHESIS_TIMEOUT_SECONDS`.

## Short utterances

For a single line or short clip, use `engine_tool.py speech` directly:

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/engine_tool.py speech "Hello there" \
  -o Output/out.wav \
  [--seed 42] \
  [--language de] \
  [--instructions "a calm, deep narrator voice"] \
  [--voice Vivian]
```

- `--instructions` only on `vdes`-task models (VoiceDesign catalog entry).
- `--language` values are family-specific; engine errors name what is valid.
- Same text + seed + model ⇒ same audio.

## Voices

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/engine_tool.py voices
```

If the list is empty, some families keep speakers only in model `config.json`.

## Related

Cloning: **audiocpp-voice-clone**. Non-catalog families: **audiocpp-deferred-tts**.

## Reporting

End by telling the user what worked and what was blocked, quoting preflight
evidence from `narration.py start` when using long-form narration.
