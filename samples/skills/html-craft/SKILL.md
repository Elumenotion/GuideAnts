---
name: html-craft
description: "Create and verify single-file interactive HTML pages in the sandbox: build or edit the file, then prove the result with headless-Chromium screenshots, in-page DOM/geometry assertions, and JS-error capture (Playwright). No GPU, no service, no network. Use when the user wants an HTML page, animation, demo, dashboard, or a screenshot/verification of an existing HTML file."
metadata:
  guideants:
    enabled: true
    display_order: 40
    requires_toolsets: [sandbox]
---

# html-craft

Paths - fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/html-craft/scripts/` relative to it, so run the commands in this file
exactly as written. Write every deliverable to the CWD with a **bare filename**
(e.g. `-o page.png`): never prefix an output path with `Output/` - the CWD *is*
the output directory, so `Output/...` would create a nested `Output/` folder.

Builds **single-file HTML artifacts** (inline CSS + JS, no build step, no
network) and verifies them the way a user would see them: a real headless
Chromium loads the file, drives it (key presses, clicks, waits), screenshots
the moments that matter, and asserts on the DOM. Everything runs inside the
sandbox: sandbox-venv Python + `playwright` + `openai` SDK + the Chromium binary already in
the image.

## Preflight (cheap; once per session)

```bash
python3 Skills/html-craft/scripts/preflight.py
```

Prints one JSON verdict: `{"scenario", "open": bool, "blockers": [...],
"warnings": [...], "evidence": {...}}` after a live 320x200 render.
`open: false` - stop and report the blockers; do not guess browser paths.

## Commands

```bash
python3 Skills/html-craft/scripts/html_craft.py probe  PAGE
python3 Skills/html-craft/scripts/html_craft.py shot   PAGE -o name.png
python3 Skills/html-craft/scripts/html_craft.py text   PAGE --sel "#el" [--sel "#el2"]
python3 Skills/html-craft/scripts/html_craft.py eval   PAGE "document.title"
python3 Skills/html-craft/scripts/html_craft.py compare A.png B.png -o cmp.png
python3 Skills/html-craft/scripts/vision_qa.py  NAME.png --ask "1) ... 2) ..." --label "..."
```

Drive flags shared by `probe` / `shot` / `text` / `eval`:

| Flag | Meaning |
|------|---------|
| `--action "press:Space"` | repeatable, applied in order: `press:KEY`, `click:SELECTOR`, `wait:MS` |
| `--wait MS` | settle after page load (default 700) |
| `--settle MS` | wait after each action (default 1200) |
| `--viewport WxH` | default `1280x800` |
| `--full-page` | `shot`: capture the full scrollable page |
| `--timeout MS` | navigation timeout (default 60000) |
| `--browser PATH` | chromium binary; else `$HTML_CRAFT_BROWSER`; else auto-find |

Every page command prints one JSON object including `pageerrors` (uncaught JS
exceptions) and `console_errors`. Treat a non-empty `pageerrors` as a failure
even when the screenshot looks right.

## The build-verify loop (every time, in this order)

1. **Read the whole source first.** For an existing file, read it end-to-end
   before touching anything - phases, timing constants, key handlers, IDs.
   Every verification wait must come from the page's own `TIMING`/phase
   lengths, never from guessing.
2. **Minimal-diff edits.** Change only what the request names. If the user
   asked for *one element* bigger, make that element bigger - do not
   rearrange the layout, move panels, or restructure the DOM "while at it".
   Before moving on, diff your edit against the original (`diff -u`) and
   confirm every hunk maps to the request.
3. **`probe`** - the file loads with zero `pageerrors`.
4. **`shot` the key states.** Drive the page like a user via `--action` (its
   own key handlers). For animations, screenshot each phase/state - not just
   one frame.
5. **Assert, don't eyeball.** `text` for visible copy; `eval` for geometry.
   Measure in **screen pixels** (`getBoundingClientRect`, or `getCTM()` +
   `getBBox()` for SVG) - never trust source units, because CSS/SVG scaling
   changes what the user actually sees. Audit rendered `font-size` values to
   catch invisible shrink-to-fit: a `fitText`-style scaler silently makes
   text 6 px unless you check the final attribute.
6. **Sweep every state.** If the page has N turns/steps, verify all N - the
   failure mode (overflow, overlap, clipping) usually shows up in the
   *fullest* state, not the first.
7. **Vision QA the key states.** `vision_qa.py` on the screenshots with
   specific numbered checks (see "Vision QA" below) - the second opinion on
   what the measurements produce.
8. **`compare` before/after** when editing an existing file, so the delta is
   provable.
9. **Report** the deliverable (bare CWD name) plus the screenshots, embedded
   with `Output/...` notebook-tree paths, and the JSON evidence (pageerrors
   empty, measured sizes, vision PASS/FAIL bullets).

## Driving animations like a user

- Use the page's own handlers: if it listens for Space/Enter/R/A,
  `--action "press:Space"` fires the real code path - no DOM hacking.
- Time waits from the source: with phases `show:3.0, fold:3.2, crank:4.6`,
  the crank phase is ~6.2-7.8 s after the press
  (`--action "press:Space" --action "wait:7000"`).
- One `shot` call per state. Five states = five calls; cleaner than one
  long-running sweep.

## In-page assertion recipes

Copy-paste `eval` expressions - SVG-to-screen geometry, font-size audit,
overlap checks, state sweeps: `references/verify-recipes.md`.

## Vision QA (after Playwright, on the screenshots)

Playwright proves the page *works*; the Wire vision model proves it *looks
right*. Run `vision_qa.py` on the screenshots from step 4 - each one already
on disk, one call per state:

```bash
python3 Skills/html-craft/scripts/vision_qa.py t1.png \
  --ask "1) Is all text on the paper sheet legible? 2) Does any element clip or overlap another? 3) Is the highlighted question clearly marked?" \
  --label "INTAKE frame: conversation sheet held over machine hopper"
```

Prints one JSON object: `{"ok": true, "review": "...", "usage": {...}}`.
The `review` is the model's answer in the tool's fixed PASS/FAIL bullet
format. `ok: false` carries the error and a hint.

**Wire prompt discipline - non-negotiable.** The Wire endpoint routes the
prompt to an *agentic* thread: a vague "critique this image" gave it license
to keep doing unrequested remediation work, and the call ran until the
platform killed it at its 30-minute timeout. The tool enforces the safe
shape - read-only boilerplate, numbered checks only, PASS/FAIL format,
capped bullets, capped `max_tokens` - and never relax it:

- Ask **numbered, answerable checks**, one per thing you care about
  ("Is X legible?", "Does A overlap B?") - never "critique", "review",
  or "improve this".
- **Read-only:** the prompt must not invite fixes, rewrites, or alternatives.
  (The tool's boilerplate states this explicitly.)
- **Small `max_tokens` (default 250).** Long generations on this endpoint
  hang; bounded output is what keeps the call in seconds, not hours.
- **One state per call.** Don't batch five screenshots into one prompt.
- **Retries are capped:** on a timeout, make the checks more specific and
  retry **once** with smaller `max_tokens`; do not loop. A hung call is a
  prompt problem, not a flake.

**Interpreting vision output:** the model can misread subtle styling (it
once described a highlight box as an underline) and has no motion context
(animations read as "disconnected" in stills). Treat each FAIL bullet as a
*hypothesis*: re-check the specific pixel region with `html_craft.py eval`
/ a zoomed crop before acting. Playwright measurements stay the source of
truth; vision is the second opinion on what those measurements produce.

## Failure modes

| Symptom | Cause / fix |
|---------|-------------|
| `file not found` for a real file | relative path or a guessed absolute path - pass the file as the CWD sees it; the tool resolves it to absolute and re-checks |
| `ERR_FILE_NOT_FOUND` inside Chromium | same root cause: `file://` needs a real absolute path *in the sandbox*, not a host path |
| `no chromium binary found` | `preflight.py` evidence lists the roots scanned; set `HTML_CRAFT_BROWSER` to the binary |
| launch crash / SIGTRAP | the tool always passes `--no-sandbox --disable-dev-shm-usage`; if it still dies, point `HTML_CRAFT_BROWSER` at a `chromium_headless_shell` build |
| `pageerrors` non-empty but the shot looks right | trust the errors - the exception can kill a phase *after* the captured frame |
| measured text smaller than the source font | a fit-to-width scaler is shrinking it - read the final `font-size` attribute and raise the wrap budget (verify-recipes.md) |
| `vision_qa.py` hangs / platform 30-min timeout | the prompt was vague and the Wire thread improvised work - rerun with specific numbered checks, `--max-tokens 250` (or less), and never retry more than once |
| `vision_qa.py` says `ok: false - OPENAI_API_KEY / OPENAI_BASE_URL not set` | the guide's environment variables are missing - set them in the guide editor (not secret values here: the key IS the secret) |
| vision FAILs a thing Playwright measured fine | the model misread (common with subtle styling / no motion context) - confirm with a zoomed crop or `eval` before changing the asset |

## Reporting

State the deliverable filename (bare, CWD), what you verified (number of
states swept, pageerrors, measured pixel sizes), and show the screenshots -
embedded in the reply via `Output/<name>` notebook-tree paths. If a state
failed, show the failing state's shot and the exact measured number that is
off.
