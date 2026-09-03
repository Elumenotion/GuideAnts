# html-craft skill

Build **single-file HTML artifacts** (inline CSS + JS) in the sandbox and
verify them with a real headless browser - screenshots of every key state,
in-page DOM/geometry assertions, and JS-error capture. The workflow encodes
the build-verify loop used to iterate on the "Simplified LLM" animation page:
read the source, make minimal-diff edits, drive the page like a user, assert
in screen pixels, sweep every state.

The skill runs **inside the sandbox** (the guideants-ai container). No GPU,
no service, no network, no API keys.

## Skill

| Skill | What it does |
|-------|--------------|
| `html-craft` | `scripts/html_craft.py` - probe / shot / text / eval / compare via Playwright + Chromium |
| | `scripts/vision_qa.py` - read-only visual QA of screenshots via the Wire API (OpenAI SDK) |
| | `scripts/preflight.py` - JSON verdict after a live 320x200 render |

## Dependencies

- sandbox-venv Python + `playwright` (already in the image)
- a Chromium binary (already in the image under `/ms-playwright/`)
- `openai` SDK (sandbox venv) - only for `vision_qa.py`
- Pillow (sandbox venv) - only for `compare`

No pip installs required at runtime.

## How browser discovery works

`preflight.find_browser()` scans, in order: `HTML_CRAFT_BROWSER` (absolute
path), `~/.cache/ms-playwright`, `/ms-playwright`, `~/.cache/puppeteer`,
`/opt/playwright`, `/usr/lib/chromium*`, `/usr/bin`, `/usr/local/bin` - first
executable named `chrome` / `headless_shell` / `chromium` / `google-chrome`
wins. Launch args are always `--no-sandbox --disable-dev-shm-usage`
(container requirement). The same function is shared by `html_craft.py`, so
`preflight` open == `shot` works.

## Required Environment

None. Optional:

| Variable | Default | Purpose |
|----------|---------|---------|
| `HTML_CRAFT_BROWSER` | auto-find | absolute path to a chromium binary |
| `HTML_CRAFT_VISION_MODEL` | `guide` | Wire model alias for `vision_qa.py` |

`vision_qa.py` additionally reads `OPENAI_API_KEY` / `OPENAI_BASE_URL`
(the Wire endpoint). Prompt discipline is enforced in the tool: read-only
boilerplate, numbered checks, capped bullets/tokens - see SKILL.md "Vision
QA" for why (a vague prompt once drove the Wire thread into hours of
unrequested work).

## Test from the sandbox

```bash
python3 Skills/html-craft/scripts/preflight.py
# then, against any page in the notebook (CWD-relative or ../input paths):
python3 Skills/html-craft/scripts/html_craft.py probe  ../some/page.html
python3 Skills/html-craft/scripts/html_craft.py shot   ../some/page.html -o page.png
python3 Skills/html-craft/scripts/test_html_craft.sh
```

The packaged test (`test_html_craft.sh`) renders a tiny fixture page with a
key-driven state machine, drives it, and asserts: zero pageerrors, the state
label changes after the keypress, screenshot bytes are a valid PNG, and the
text command returns the expected copy.

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| `no chromium binary found` | `preflight.py` prints the scanned roots; point `HTML_CRAFT_BROWSER` at the binary |
| `file not found` | the path is as seen from the **host**, not the sandbox - use CWD-relative / `../` workspace paths |
| launch crash | the tool always adds `--no-sandbox`; if it still fails, set `HTML_CRAFT_BROWSER` to a `chromium_headless_shell` build |
| screenshot taken in the wrong phase | waits guessed, not derived - read the page's timing constants (see `references/verify-recipes.md`) |
| text illegible in the shot but "fine" in source | a shrink-to-fit scaler - run the font-size audit recipe |
