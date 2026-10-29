---
name: audiocpp-ming3-narration
description: "Long-form Ming-3.0-TTS narration with per-paragraph emotion and tempo, written in plain English. Annotated scripts ([emotion: happy], [speed: fast]) map to the model's trained control tokens inside the tool, chunk boundaries never fall inside a sentence, and the voice-pack acoustic prefix is always applied so every chunk is the same speaker. Free-standing: stdlib only, calls a ming3 engine directly, imports nothing from audiocpp-tts-controls, so chatterbox paths are unaffected."
metadata:
  guideants:
    enabled: true
    display_order: 37
    requires_toolsets: [sandbox]
---

# Ming-3 narration with English annotations

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the notebook's
**output directory**; this skill's scripts live at
`Skills/audiocpp-ming3-narration/scripts/`. Write deliverables with **bare filenames**
(`-o narration.wav`); never prefix with `Output/`.

**This skill does not use the audiocpp gateway.** It posts to a ming3 engine's
`/v1/audio/speech` directly (`--engine-url`, default `http://127.0.0.1:18099`). Do not
set `AUDIOCPP_SKILL_BASE_URL` for it, and do not route it to a production host.

Start the engine first (that is `audiocpp-diarize`'s `spawn_engine.py`, family
`ming3_tts`, task `clon`), then confirm the voice is registered:

```bash
curl -s -m5 http://127.0.0.1:18099/v1/audio/voices | grep -o '"doug"'
```

## Quick start

```bash
python3 Skills/audiocpp-ming3-narration/scripts/ming3_say.py script.txt \
  -o narration.wav --voice doug --engine-url http://127.0.0.1:18099
```

Outputs the WAV plus `<output>.report.json`: per-chunk text, duration, peak, the
English control, the token actually sent, and the prefix state.

Dry-run first when a script is new — it parses, validates the control gate, and prints
the exact fragmentation without synthesizing:

```bash
python3 .../ming3_say.py script.txt -o out.wav --dry-run
```

## Script format

Paragraphs are separated by **blank lines**. A directive line of `[key: value]` groups
immediately above a paragraph sets that paragraph's controls. Unannotated paragraphs use
the command-line defaults (`--emotion`, `--speed`, `--pitch`, `--volume`, `--dialect`,
`--style`).

```
[emotion: happy]
I am so pleased to tell you about this wonderful safety device.

[emotion: sad]
Had this existed when the tragedy happened, so many could have been saved.

[speed: fast] [pause: 600]
Nobody should ever be forced to deal with that kind of tragedy.
```

Also accepted: `pitch`, `volume`, `dialect`, `style` (free English description),
`voice` (per-paragraph voice-pack id), `seed`, `pause` (ms, overrides the boundary pause
before this paragraph). A double blank line doubles that pause.

**Scripts are English only.** A directive line holding a CJK character is rejected with
the English value to use instead. The tool maps English → the trained token at the last
moment and records the mapping in the report.

| Directive | English values | Trained token sent |
|---|---|---|
| `emotion` | neutral, happy, sad, angry, fearful, disgusted, surprised | `中性 高兴 悲伤 愤怒 恐惧 厌恶 惊讶` (`tts_tasks.py:63`) |
| `speed` | slow, medium, fast | `慢速 中速 快速` (`tts_tasks.py:58`) |
| `pitch` / `volume` | low, medium, high | `低 中 高` (`tts_tasks.py:59-60`) |
| `dialect` | cantonese, sichuan | `cantonese`, `chuanyu dialect` (`tts_tasks.py:65`) |

Values must be the trained tokens — the English glosses in the cookbook "explain the
values; they are not API aliases" (`cookbooks/instruction_en.md:110`), which is why the
mapping lives in the tool rather than in scripts.

## Three rules this skill enforces, each from a measured failure

### 1. The acoustic prefix is ON for every call — it is the voice lock

The prefix is the audio-tokenizer encoding of the voice-pack clip, and it is built only
when `reference_text` is non-empty (`session.cpp:148`). Suppress it and each call becomes
an independent draw from the 192-dim embedding: emotion gets stronger, and chunks stop
sounding like one person. Measured this session, cosine similarity of each chunk's
voiced MFCC against the reference clip:

| prefix | per-chunk cos | min | slope across chunks |
|---|---|---|---|
| **on** | .994 .996 .994 .995 .994 | **.994** | −0.0002 |
| off | .977 .980 .981 .981 .973 .963 .963 | .963 | −0.0030 |

`--prefix` defaults to `on`. `off` is diagnostic only. Calls are position-independent
(same fragment produces byte-identical audio spoken first or last), so every chunk sees
the identical conditioning — that is what holds the speaker constant.

### 2. Chunk boundaries never fall inside a sentence

`split_chunks` packs **whole sentences** into a `--chunk-chars` budget (default 200).
Only a single sentence longer than the hard cap falls back to comma, then word
boundaries. Upstream splits "at punctuation, keeping long clauses intact"
(`infer.py:79-80`). A boundary mid-sentence gives the model no punctuation cue and the
next fragment restarts prosody cold — measured as `"...on my nvidia"` /
`"powered workstation."` on separate calls.

### 3. One engine call = one synthesis fragment

Every call sends `raw_text=true` ("Send the text as one synthesis fragment without
frontend chunking", `model_specs/ming3_tts.json`). With a single fragment there is no
"later chunk", so the engine's cross-chunk paths — freeform reusing chunk 1 as reference
(`session.cpp:194-202`) and the structured templates' zero-history drift
(`generator.cpp:205`) — cannot apply. Pacing is inserted here, at the seams.

## Control gate

`--instruction-schema` defaults to `zh`: upstream builds EMOTION/BASIC captions with the
Chinese field names and uses `en` only for DIALECT (`tts_tasks.py:172`). Schema switches
field *names*, not the language of your text.

The engine rejects mixing families in one call: the `emotion` template accepts only an
emotion, `basic` only speed/pitch/volume, `dialect` only a dialect (`prompt.cpp:50-59`).
So a section can be *happy* or *fast*, not *happy and fast*. `--style-fallback` rewrites
an incompatible combination as a freeform English instruction; it works mechanically but
its audio quality is **unverified**, and freeform speed text was previously measured to
move nothing at length.

Per-paragraph directives are how you get tempo and emotion changing mid-script: each
section is its own call with its own caption.

## Control matrix

Renders a transcript once per cell so controls can be compared back to back. Resumable.

```bash
python3 .../run_matrix.py --transcript doug_script.txt --outdir matrix
```

Cells: `baseline`, one per emotion, one per speed. `--only emotion_sad,speed_fast`
subsets it; each cell is written as an annotated script so the format is exercised too.

## Verified on (2026-10-29)

`Ming-3.0-TTS-0.5B-GGUF`, voice-pack preset `doug` (24 kHz mono), full 3-paragraph
807-character transcript, engine default seed (never pinned), 0 silent cells in 11 cells.

- Speed is monotonic: slow 47.16 s → medium 45.36 s → fast 35.66 s.
- Emotion dispersion with the prefix locked on (`zh` caption schema) is roughly double
  the old `en`-schema config, measured as spread of spectral centroid across the seven
  labels: 0.18 (`en`) → 0.36 (`zh`) → 1.36 (prefix off). **Locking the voice costs
  emotional range.** That trade-off is the open problem, not a misconfiguration.

## Limits — read before promising anything

- **Emotion strength with the prefix on is modest.** Centroid and duration are proxies
  and have already been contradicted by listening once. The user's ears are the test.
- No ASR verification of any rendered take's word accuracy.
- Emotion and tempo cannot combine in one section (see the gate).
- Long uncontrolled text should still be spot-checked: `max_tokens` defaults to 1000
  patches per fragment. A 195-character fragment in one `raw_text` call produced 9.6 s
  with no truncation; longer single sentences than that are untested.
- Silence cells: the engine retains a ~300 ms tail per fragment
  (`session.cpp:179-187`), so a very short phrase is partly trim tail — do not read its
  rate or energy figures as delivery.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `Connection refused` on 18099 | Engine not up. Spawn via `audiocpp-diarize`'s `spawn_engine.py`, family `ming3_tts`, task `clon`. |
| Voices list empty | The spawn bypassed the gateway, so no voice-pack presets were built. Re-spawn through the local gateway. |
| `Ming EMOTION uses structured controls, not raw instruct` | `style` combined with a structured control. Split into sections, or `--style-fallback`. |
| `scripts are English only; found CJK characters` | A trained token was pasted into a script. Use the English value. |
| `line starting with '[' is not a valid directive` | Malformed directive. A directive line holds only `[key: value]` groups. |
| Voice drifts after the first chunk | `--prefix off`, or a non-sentence-atomic splitter. Both are regressions; prefix must stay `on`. |
| 0.300 s silent fragment | A (config, seed) mapping landing on low-energy patches, collapsed by `trim_silence`. Not a broken engine. |
