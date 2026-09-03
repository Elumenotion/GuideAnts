# Timing best practices — thresholds and QA rubric

Distilled from `video-construction-best-practices-guide.md` (the project's
cited guide) plus the 2026-08-27 v4-audio audit. These are the numbers the
audio audit (Phase 1) and the QA scorer (`qa_timeline.py`) hold things to.
**If you change a threshold here, change `qa_timeline.py` too** — the
rubric and the script must never disagree.

## 1. Audio audit thresholds (Phase 1)

| Metric | Target | Flag |
|---|---|---|
| Overall WPM (explainer) | 130–145; presentation 140–160 | >170 = "rushed" → the whole plan must carry extra visual energy |
| WPM per 30-s bucket | ≤170 | any bucket >170 → "rushed zone" (list the timestamps) |
| WPM per 30-s bucket | ≥120 | <120 → "flat zone" |
| Pauses (from word stream) | 1–3 s deliberate silences | count gaps >1.0 s and >2.0 s; <5 in a 10+ min track = "no breathers" → cards must supply them |
| Segments | none 0 s; few 1 s | 0-s/1-s segments = TTS seams → Hold across, no shot of their own |
| Head/tail silence | 0.5 s+ tail | none → tail pad mandatory (1.5–2.5 s) |
| Peak / RMS | peak < -1 dBFS; RMS -20–-26 | headroom note for a music bed (bed -24–-28 dB RMS under voice) |
| Length vs content type | how-to 1–5; explainer/opinion 8–12; training <6 | name the band the video falls in |

## 2. Shot / rhythm targets (the timeline plan)

| Metric | Target | Rationale |
|---|---|---|
| Average shot length | 5–15 s for a narration piece (3–6 s is the cut-energy ideal; do not force it over an 11-min story) | median 8 s was the working value in v4 |
| Longest static visual | ≤ 18 s | >60 s of one frame is a long-form retention hole; 18 s with a slow push-in is the ceiling for a text beat |
| Longest continuous audio run needing visual change | split >60 s runs at 7–10 s cadence | the 132-s "what Guidance is" beat → 17 shots |
| Text beats (cards/slides) | one every ~30–60 s; 4–12 words each | the reading layer that compensates for fast narration; ≤12–20 words is the hard ceiling |
| Chapters / acts | 5–15 for a 10–15 min video; first chapter 30 s–2 min in; 1 per 1–3 min | 8 acts for 11.5 min was the working value |
| Re-hooks | structural pivot every 2–4 min; a pivot inside every >60 s run | v4 had the 132-s run with no pivot → inner re-hook card at its midpoint |
| Payoffs | a named payoff roughly every 3–5 min | credentials, thesis, reveal, promise |
| 40–55% zone | must contain a pivot/re-hook (second hook) | the slump zone; v4's pivot landed at 5:15 (45%) — good |
| Opening 0–30 s | goal/promise stated by 30 s; strong first frame (not a blank or new-tab screen) | identity-first openers underperform — put a title card behind them |
| End (last 20 s) | content until the end; end card; exactly one CTA target; CTA synced to its spoken mention; 1.5–2.5 s tail pad | never link the same target in both a card and the end screen |

## 3. Story-role taxonomy (column H of the xlsx)

Hook · Promise · Establish · Story · Proof · Re-hook · Payoff ·
Reveal · Principle · Transition · CTA · Close

- **Hook** — first 30 s grab; **Promise** — what the viewer gets.
- **Establish** — who/what/why; **Story** — narrative motion (the default).
- **Proof** — credentials, scale, clients; **Re-hook** — re-grab mid-video.
- **Payoff / Reveal** — the big gives; **Principle** — a rule/standard.
- **Transition** — act change; **CTA** — next step; **Close** — sign-off.

## 4. QA rubric (qa_timeline.py) — 100 points, 12 checks

Weights are fixed. Score per check = weight × fraction passing
(proportional scoring, not all-or-nothing) — unless the check is marked
HARD, in which case any violation is a **hard fail** regardless of score.

| # | Check | Weight | PASS criterion (proportional on the fraction of rows/shots that pass) |
|---|---|---|---|
| 1 | Chain integrity (HARD) | 15 | rows sorted, no overlap >0.5 s, no gap >2.5 s (gap tolerance covers 0–1 s seams), coverage from 0:00 to audio end |
| 2 | Tail pad (HARD) | 5 | last row extends ≥1.5 s past audio end and is End Card / Hold |
| 3 | No 0-s rows (HARD) | 5 | every row duration >0 |
| 4 | No oversized statics | 10 | ≤90% of rows ≤18 s; 0 rows >30 s |
| 5 | Long-run split | 10 | every audio run >60 s with no role in {Re-hook, Payoff, Reveal} inside is split into ≥2 rows |
| 6 | Text-beat cadence | 10 | a card/slide (type Slide or New Image with text) within every 75 s window (first window from 0:00) |
| 7 | Opening | 8 | goal/promise row starts ≤30 s and first row is a New Image/Slide (strong frame) |
| 8 | Act/chapter plan | 8 | acts ≥4 and ≤15 for a >8-min video; act lengths 60–240 s; first act ≤90 s |
| 9 | Slump-zone pivot | 8 | at least one row with role Re-hook/Payoff/Reveal/CTA whose In time falls in the 40–55% band |
| 10 | Ending | 8 | End Card row inside the last 20 s; exactly one CTA row; no two CTA rows |
| 11 | Fills complete | 7 | ≥90% of rows have a non-empty What-to-show; ≥80% have plain-text ≤60 words in the summary column |
| 12 | Status hygiene | 6 | 100% of rows have a valid Status; rows of types New Image/Slide/Video Clip without an Asset file must not be marked Done |

**Grade:** A ≥90 · B ≥75 · C ≥60 · D <60. **Definition of done:**
grade ≥ B and zero hard fails.

Evidence to quote from the JSON: total score, grade, the 3 lowest-scoring
non-hard checks, and any hard fails with the offending row numbers.
