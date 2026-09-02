# Timeline xlsx format (the deliverable schema)

The deliverable is ONE `.xlsx`. Three sheets, in this order:
**Timeline** · **Available assets** · **How to use**. A fourth sheet,
**Audit**, carries the Phase-1 audio audit (numbers next to thresholds).

## Sheet 1: Timeline

One row per **story moment**. Frozen header, auto-filter on.

| Col | Header | Rules |
|---|---|---|
| A | # | 1..n, row order = play order |
| B | Chapter | `Act N · <name>` — the act the row belongs to |
| C | Moment | 3–6 word name of the moment ("THE REVEAL", "MIDPOINT PIVOT") |
| D | In | `M:SS` on the audio clock |
| E | Out | `M:SS` on the audio clock |
| F | Dur | Out − In, seconds |
| G | What Doug says (plain) | **plain-English summary, ≤60 words** — what the narration actually says at this time. No transcript dumps, no quotes-with-punctuation-fidelity |
| H | Story role | dropdown: Hook, Promise, Establish, Story, Proof, Re-hook, Payoff, Reveal, Principle, Transition, CTA, Close (taxonomy in `timing-best-practices.md` §3) |
| I | Visual type | dropdown: **New Image, Slide, Video Clip, Session B-roll, End Card, Hold** |
| J | What to show | the suggestion for the content of the image/slide/clip (4–12 words) |
| K | Asset / file (you fill) | **empty by design** — the user fills it |
| L | Status | dropdown: To-do, Have, Verify, Optional, Done |
| M | Notes | offsets and flags: `F1: card on the real 4:08 pause`, `NEEDS CAPTURE`, `verify span` |

### Chaining rules (the geometry)

- Rows chain **with no gaps**: row[i+1].In == row[i].Out, from 0:00 to
  audio-end + tail pad. Overlaps are allowed only to bridge 0–1 s seams
  (a Hold row may start up to 1 s before the previous row ends — QA
  tolerance 0.5 s).
- The **last row is the tail pad**: 1.5–2.5 s past audio end, type
  End Card or Hold (audio from TTS has no tail).
- **0-s and 1-s audio segments (TTS seams) get no row of their own.** The
  adjacent row simply spans across them; mark it in Notes (`merges 0-s seam`).
- The user may split or merge rows freely — the chain must stay unbroken.

### Status workflow

`To-do` (default for New Image/Slide/Video Clip rows — the asset must be
made) → `Have` (asset exists) → `Verify` (assistant-suggested span/
content needs a human confirm) → `Done`. `Optional` = nice-to-have.
A row of a to-make type may never be `Done` with an empty Asset column.

### Visual conventions

- Header row: dark fill (#181B1F), white bold text, wrapped, height 30.
- Column B (Chapter) fill = the row's story-role color:
  Hook #FDE9C9 · Promise #D6E4F5 · Establish #EDEDED · Story white ·
  Re-hook #F5D6D0 · Proof #DFF0E5 · Payoff/Reveal #FCE4B0 ·
  Principle/Transition #EDEDED · CTA #D6E4F5 · Close #EDEDED.
- Column C bold; column I bold; all cells wrapped, top-aligned; borders thin #D9D9D9.
- Widths: A4 B16 C17 D6 E6 F6 G52 H11 I14 J36 K20 L9 M32.

## Sheet 2: Available assets

Columns: Type · What it is · Where/how many · Use for. List what the
project actually has (session footage + how to cut it, any pre-extracted
frames, rendered cards, website screenshots, product screenshots, presenter
stills) so the user can fill column K from a menu, not from memory.

## Sheet 3: How to use

≤25 short lines: the five usage steps (each row is a story moment; pick a
visual type; fill Asset/file; move Status; keep the chain), the story-role
one-liners, and the visual-type hints (what counts as a New Image vs a Slide
vs a Video Clip vs Session B-roll vs Hold).

## Sheet 4: Audit (Phase 1 output)

The audio audit, numbers next to thresholds, each marked pass/fail:
duration + word count + overall WPM; WPM extremes per 30-s bucket; the pause
ladder counts; the list of real pauses (timestamp + words before/after) with
a note "cards may land here"; seams; head/tail; peak/RMS. This sheet makes
the file self-documenting — a future session can re-derive the plan from it.
