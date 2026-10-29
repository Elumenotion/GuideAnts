---
name: audiocpp-tts-controls
description: "Advanced synthesis on the loaded GPU host TTS model via the raw audiocpp gateway (/tts): seed, language, voice-design instructions, builtin/voice-pack speakers, multi-speaker dialogue with overlapping interruptions, and per-paragraph long-form narration with designed line-break pauses and speech-protected intra-utterance pause clamping. None of which the built-in GuideAnts audio tools expose."
metadata:
  guideants:
    enabled: true
    display_order: 32
    requires_toolsets: [sandbox]
---

# audio.cpp synthesis controls (experimental)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/audiocpp-tts-controls/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o scene.wav`); never prefix with `Output/`.

Product TTS is `{text, voice, speed}`. This skill calls raw
`/tts/v1/audio/speech` on the GPU host. Deliverables are WAVs written to the CWD (bare filenames).

## Environment (required for PC → the GPU host)

```text
AUDIOCPP_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as the GPU host GA_AUDIOCPP_SKILL_TOKEN>
```

A TTS model must already be loaded on the GPU host via GuideAnts Settings. Chatterbox
(`ResembleAI/chatterbox`) is the known-good catalog entry for cloning /
voice-pack presets.

## Long-form single-speaker narration (video scripts)

Write the final narration script as plain text (markdown headers ok) and call
`paragraph_tts.py`. A blank line in the script is a **beat**: each piece is
synthesized with its own engine call and the paragraph pauses are authored by
the join, not left to the model's generated prosody.

```bash
python3 Skills/audiocpp-tts-controls/scripts/paragraph_tts.py script.md \
  -o narration.wav --voice doug \
  [--base-ms 250] [--pad-min-ms 100] [--pad-max-ms 200] [--seed N] \
  [--clamp-min-ms 300] [--clamp-ratio 0.80] [--no-clamp]
```

Locked rules (do not improvise around these):

1. **Pieces** — a blank line starts a new piece; wrapped lines (no blank
   line) stay in the same piece; markdown headers / `---` rules /
   `END OF SCRIPT` are ignored.
2. **One engine call per piece**, cached under `--state-dir`
   (default `.audiocpp-para/`) keyed by text+voice hash — an interrupted
   run resumes without re-synthesizing finished pieces.
3. **Interior pause clamping, per piece, BEFORE the join** — every silent
   gap ≥ `--clamp-min-ms` inside a piece is rescaled to `--clamp-ratio` of
   its **own original length** (default 0.80). Never a hard target length.
4. **Speech protection** — the detector's gap span can include the decaying
   tail of a final word (soft speech under the RMS threshold), so each cut
   is restricted to the span's **quiet core** (frames below an
   adaptive peak threshold). If the quiet core is shorter than the floor
   the gap is not clamped. Removed samples are always below the quiet
   peak — the report's `removed_peak` per gap proves it. Do not raise the
   threshold or cut outside the quiet core: that clips final syllables.
5. **Edge silence is never touched** — each piece's natural engine lead/
   tail silence stays exactly as emitted (no trimming, no cutting); it is
   reported per piece, not clamped.
6. **Designed boundary pause** — between pieces:
   `pause_ms = (base_ms + uniform(pad_min_ms, pad_max_ms)) * linefeeds`,
   where `linefeeds` is the number of consecutive blank lines (a double
   break doubles the pause). The pad RNG is seeded with `--seed`.

Output: the final WAV plus `<output>.para-report.json` (per-piece
durations, clamped gaps with `removed_peak`, untouched edge gaps, designed
boundary pauses). Verify a run by checking `removed_peak < 100` on every
clamped gap before delivering the audio.

## Short utterances

For a single line or short clip, use `engine_tool.py speech` directly:

```bash
python3 Skills/audiocpp-tts-controls/scripts/engine_tool.py speech "Hello there" \
  -o out.wav \
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
python3 Skills/audiocpp-tts-controls/scripts/engine_tool.py voices
```

Voice ids come from the **voice-pack** baked into the GPU host AI image at
`/opt/guideants/voice-pack/` (or a custom mount at
`/opt/guideants/custom-voice-pack`). The engine resolves a `--voice` id to a
reference clip **server-side** — you do NOT stage the clip for pack voices.
A custom user voice (e.g. a cloned reference added to the pack's
`manifest.json`) works the same way: pass its `voiceId` as `--voice`.

### Choosing a voice for a script (important)

The voice-pack `manifest.json` records each voice's `language` and `gender`.
**Match the voice's `language` to the script's language.** Chatterbox is an
English model: an in-distribution pair (English reference + English text)
locks onto one stable voice. An out-of-distribution pair (e.g. a French-
reference `ff_*` voice speaking English) produces an **unstable, text-
sensitive accent** — the same speaker drifts into different accents across
lines, and **a seed cannot fix this** (a seed only controls sampling noise on
top of a stable distribution). If a voice sounds like a different person on
each line, check its `language` in the manifest first, then swap for a
matching-language voice.

## Multi-speaker dialogue (overlap / interruptions)

```bash
python3 Skills/audiocpp-tts-controls/scripts/multi_speaker.py \
  scene.json -o scene.wav \
  [--model chatterbox] \
  [--seed-map '{"narrator":1001,"alice":3003}']
```

`scene.json` is an array of lines:

```json
[
  {"voice": "narrator",  "text": "What's the air speed velocity of an unladen swallow?"},
  {"voice": "bm_george", "text": "African or European?", "overlap_ms": 400},
  {"voice": "bf_alice",  "text": "Oh no, you two are doing the swallow thing again.", "overlap_ms": 600}
]
```

- `voice` / `text` — required.
- `overlap_ms` — how many ms this line **starts before the previous line
  ends**. `0` (default) = clean turn-taking; `>0` = the new speaker talks
  over / interrupts. For a realistic interruption the overlap should be
  **~1s+** so real words from both speakers are audible at once; 200–300 ms
  only overlaps a trailing consonant and does NOT read as an interruption.
- `seed` — per-line override. Omit to use an auto per-speaker seed.

### How the mix works (do not change these)

1. **Two tracks on one timeline.** Each line is placed at its computed start
   offset on a shared buffer and **additively mixed** where they overlap.
   No crossfade, no truncation — both voices are genuinely audible together.
2. **Preserve the native sample rate.** The TTS engine emits **24 kHz** mono.
   Writing the output at a different rate (e.g. 48 kHz) plays the audio at
   the wrong speed and mangles it. Always pass the source `framerate` through.
3. **One seed per speaker.** Chatterbox is non-deterministic per call. Assign
   each speaker a **fixed seed** (the script auto-derives one from the voice
   name by default) so a speaker sounds consistent across all their lines.
   This only works for in-distribution (matching-language) voices — see above.

### Realistic interruption beats

To make interruptions sound natural, write the **interrupted** speaker's line
as a full flowing sentence that is still going when the other person cuts in
(so the overlap zone contains real words from both), rather than a truncated
fragment. A short `overlap_ms` on a clean turn is fine for pacing, but the
"talk-over" moments should have `overlap_ms` ≥ ~1000.

## When this isn't enough

- Loaded model rejects a `voice` preset id → confirm the pack is mounted and
  the id is in `manifest.json`; for a brand-new custom voice add it to the
  pack and rebuild/remount, or stage a clip and use **audiocpp-voice-clone**
  (`--voice-ref`).
- No TTS loaded on the GPU host → blocked; say so.

## Reporting

End by telling the user what worked and what was blocked. For long-form
narration, quote `paragraph_tts.py`'s per-piece gap report (clamped gaps with
`removed_peak`, untouched edge gaps, designed boundary pauses, final
`duration_seconds`) — and confirm `removed_peak < 100` on every clamped gap
before delivering the audio. For
multi-speaker output, state the total duration, per-speaker seeds, and which
lines carry `overlap_ms`. For long narration, quote `status` (`progress`,
`elapsed_seconds`, `eta_seconds`, final `result.duration_seconds`) — not
chunk or segment details.
