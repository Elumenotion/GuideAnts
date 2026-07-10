# Product walkthrough scripts

Repeatable Playwright scenarios for **testing** and **screen-recorded demo capture**.

Each run produces a monitor MP4 plus a timestamped event timeline for post-production (trimming dead air, checking pointer placement, narration gaps).

## How it fits together

```
walkthrough_runner.py          Playwright test (walkthroughs/scenarios/)
        │                                │
        ├─ screen_recorder.py ───────────┤ drives Chrome on monitor N
        │   (MP4 on monitor)             ├─ timeline → events.jsonl
        │                                ├─ tutorial pointer overlay
        └─ merges clock + video ─────────┘
                    │
                    ▼
        recordings/runs/<timestamp>_<scenario>/
          video.mp4, timeline.json, segments.json, frames/
```

- **Python runner** starts monitor capture, spawns Playwright, merges `timeline.json`.
- **Playwright** is the production driver (headed Chrome, real viewport size).
- **`playwright-cli`** is for interactive selector debugging only — not used in recorded runs.
- **Clock**: `t0.epoch` is written at `scenario.start`. Video offset uses `recording_lead_in_ms` in `timeline.json` (trim dead air before the browser appears).

## Setup

```powershell
# From repo root — screen capture deps
pip install -r scripts/screen_record-requirements.txt

# Playwright + Chrome
cd walkthroughs
npm install
npm run install:browsers
```

**Monitor**: place Chrome on the capture monitor (default **monitor 1**). The runner sets `--window-position` from monitor geometry and sizes the viewport to match.

**Extension debugger banner**: `playwright.config.ts` passes `--silent-debugger-extension-api` for Playwright-launched Chrome. Use the same flag if you launch Chrome manually with `--extension`.

**Test credentials** (override via env): `Test@example.com` / `password`

## Run a walkthrough

From repo root:

```powershell
python scripts/walkthrough_runner.py --scenario notebook/toolbar-tour --compile-segments --extract-frames
```

A full pass takes ~30–45 seconds. Expect `1 passed` from Playwright when it finishes.

### Flags

| Flag | Default | Purpose |
|------|---------|---------|
| `--monitor` | `1` | Screen capture monitor index |
| `--fps` | `30` | Recording frame rate |
| `--base-url` | `http://localhost:5107` | App URL |
| `--mode` | `record` | `record` saves video even on soft failures; `test` fails CI-style |
| `--compile-segments` | off | Emit `segments.json` idle/active map |
| `--extract-frames` | off | PNG snapshots at timeline events (visual review) |
| `--run-dir` | auto | Explicit output directory under `recordings/runs/` |

### Output layout

```
recordings/runs/20260709_143000_notebook-toolbar-tour/
  video.mp4                 # monitor capture
  timeline.json             # merged events + clock + video metadata
  events.jsonl              # raw Playwright event stream
  playwright-manifest.json  # full manifest copy
  segments.json             # optional (--compile-segments)
  frames/                   # optional (--extract-frames)
    manifest.json
    0012ms_pointer_label_Welcome.png
    ...
  t0.epoch                  # scenario start epoch (ms)
  meta.json                 # runner summary
```

### Playwright only (no screen capture)

Faster iteration when tuning selectors or timing:

```powershell
cd walkthroughs
$env:WALKTHROUGH_RUN_DIR = '..\recordings\runs\debug-my-run'
$env:WALKTHROUGH_T0_EPOCH_MS = '1'
$env:WALKTHROUGH_BASE_URL = 'http://localhost:5107'
npx playwright test scenarios/notebook/toolbar-tour.spec.ts
```

## Environment variables

Set automatically by `walkthrough_runner.py` (or manually for `npx playwright test`):

| Variable | Purpose |
|----------|---------|
| `WALKTHROUGH_RUN_DIR` | Run output directory |
| `WALKTHROUGH_T0_EPOCH_MS` | Shared epoch for `t_ms` in events |
| `WALKTHROUGH_FPS` | FPS for frame extraction / clock |
| `WALKTHROUGH_BASE_URL` | Playwright `baseURL` |
| `WALKTHROUGH_NOTEBOOK_PATH` | Override notebook route in scenarios |
| `WALKTHROUGH_MODE` | `record` or `test` |
| `WALKTHROUGH_EMAIL` / `WALKTHROUGH_PASSWORD` | Sign-in credentials |
| `WALKTHROUGH_SIDEBAR_WIDTH` | Notebook/project sidebar width in px (default: `520`) |
| `WALKTHROUGH_WINDOW_POSITION` | Chrome window position (`left,top`) |
| `WALKTHROUGH_MONITOR_WIDTH` / `HEIGHT` | Viewport dimensions |

## Project layout

```
walkthroughs/
  lib/
    timeline.ts      # events → events.jsonl + manifest
    pointer.ts       # tutorial ring + callout overlay
    typing.ts        # per-character typing events
    waits.ts         # pause(), waitForNetworkSettled()
    layout.ts        # sidebar width via localStorage
    dom-watch.ts     # MutationObserver for unplanned UI activity
    auth.ts          # signIn()
    window.ts        # Chrome monitor sizing via CDP
    clock.ts         # t_ms from t0.epoch
  fixtures/
    walkthrough.ts   # timeline, pointer, signedIn fixtures
  scenarios/
    notebook/
      toolbar-tour.spec.ts   # reference scenario
```

## Authoring scenarios

### Fixtures

```typescript
import { test, expect, withDomWatch, notebookPath } from '../../fixtures/walkthrough.js';

test('my flow', async ({ page, timeline, pointer, signedIn: _signedIn }) => {
  await withDomWatch(page, timeline, async () => {
    // signedIn: sizes Chrome, signs in, leaves page on post-login URL
    await page.goto(notebookPath());
    // ...
  });
});
```

| Fixture | Role |
|---------|------|
| `timeline` | Emits timestamped events; writes `events.jsonl` when `WALKTHROUGH_RUN_DIR` is set |
| `pointer` | Registers overlay init script + layout; removes overlay on teardown |
| `signedIn` | `prepareChromeWindow` + `signIn` — use `signedIn: _signedIn` to trigger it |

The pointer overlay is **not** shown during sign-in. Call `pointer.ensureInstalled()` only after the target page has settled.

### Presentation timing (lessons from production tuning)

The video is only as good as the **waits**. A common failure mode: the intro bubble flashes for a frame because the scenario starts before the page finishes painting.

**Recommended intro pattern** (see `toolbar-tour.spec.ts`):

```typescript
await page.goto(notebookPath(), { waitUntil: 'domcontentloaded' });
await ensureWalkthroughLayout(page);

// 1. Wait for real UI — not just DOM ready
await expect(page.getByTestId('notebook-service-toolbar')).toBeVisible();
await expect(page.getByText('Welcome to your notebook')).toBeVisible();
await waitForNetworkSettled(page); // capped; won't hang on polling

// 2. Hold on the clean page (no overlay yet)
await pause(timeline, { ms: 2_500, reason: 'page_settle_before_intro' });

// 3. Install pointer and show intro
await pointer.ensureInstalled();
await pointer.pointAtBox(toolbarBox, {
  title: 'Welcome!',
  subtitle: "Let's tour the notebook toolbar",
  showRing: false,
});

// 4. Hold for narration, then buffer before the first tour stop
await pause(timeline, { ms: 4_000, reason: 'intro_narration' });
await pause(timeline, { ms: 1_200, reason: 'intro_hold_after' });
```

| Phase | Typical hold | `reason` tag |
|-------|-------------|--------------|
| Page settle (no overlay) | 2–3s | `page_settle_before_intro` |
| Intro narration | 3–5s | `intro_narration` |
| After intro, before first stop | 1–1.5s | `intro_hold_after` |
| Per tour stop dwell | 1.2–1.5s | (built into `tourStop`) |
| Between stops | 250ms | `between_stops` |
| Outro | 1.2s+ | `outro_narration` |

Use explicit `pause()` for anything the narrator says — gaps with no events become **unplanned idle** in `segments.json`.

**Do not** use `waitForLoadState('networkidle')` as the only gate; apps with websockets/polling may never reach idle. Prefer visible-element assertions + `waitForNetworkSettled()` (8s cap).

### Tutorial pointer

`TutorialPointer` draws a ring highlight + edge-aware callout bubble in the page (not the monitor capture layer — it appears in both the video and the browser).

| Method | Use |
|--------|-----|
| `pointAtBox(box, { title, subtitle, showRing, animate })` | Intro/outro or custom placement |
| `tourStop(locator, { title, subtitle, dwellMs })` | Hover target → animate ring → dwell |
| `setLabel(title, subtitle)` | Update bubble text |
| `flash()` | Green border pulse (outro) |

**Behavior details:**

- Callout is **hidden until the first `pointAt`** — avoids a flash of default text during page load.
- Movement uses **one `page.evaluate` + CSS transition** (~650ms). Do not animate by calling `evaluate` in a tight loop; that flooded CDP and caused 3-minute hangs (see Troubleshooting).
- Overlay uses `pointer-events: none` so it never blocks clicks/hovers.
- Right-edge targets (Help, Settings) get left-side bubbles automatically.
- `ensureWalkthroughLayout(page)` sets sidebar width via `localStorage` and reloads once if stale.

### Layout

Notebook sidebar width defaults to **520px** (`WALKTHROUGH_SIDEBAR_WIDTH`). Wider sidebars reduce truncated conversation titles in demos. `registerWalkthroughLayout` runs via the `pointer` fixture init script; `ensureWalkthroughLayout` verifies it applied after navigation.

## Iteration workflow

1. **Edit** scenario in `walkthroughs/scenarios/`
2. **Fast check** — `npx playwright test` (no recorder, ~25s)
3. **Record** — `walkthrough_runner.py --compile-segments --extract-frames`
4. **Review frames** — `recordings/runs/<run>/frames/` for pointer placement at each event
5. **Review segments** — `segments.json` for planned vs unplanned idle
6. **Trim video** — use `timeline.json` → `clock.recording_lead_in_ms` + event `t_ms` values
7. **Debug selectors** — `playwright-cli` interactively (not in the runner path)

### Frame extraction (standalone)

```powershell
python scripts/timeline_extract_frames.py recordings/runs/<run-id>
```

Requires `timeline.json` and `video.mp4` in the run directory.

### Segment compilation (standalone)

```powershell
python scripts/timeline_compile.py recordings/runs/<run-id>/timeline.json
```

## Timeline event kinds

`scenario.start/end`, `pointer.move`, `pointer.label`, `ui.hover`, `ui.click`,
`typing.start/char/end`, `idle.start/end`, `dom.mutation`, `navigate`, `assert.pass`, `note`

- **`pause()`** → `idle.start` / `idle.end` with `planned: true`
- **`tourStop`** → `ui.hover`, `pointer.label`, `pointer.move`
- **Sign-in** → `typing.*` per character (for cut points)

## Troubleshooting

### Test runs ~3 minutes then fails; pointer frozen on last stop

**Symptom**: pointer stops moving (often on Help tour), browser closes at 180s timeout.

**Cause**: hundreds of rapid `page.evaluate` calls (e.g. animating the pointer in a 40-step loop) combined with dom-watch mutation callbacks can deadlock the Playwright CDP channel.

**Fix**: use CSS transitions (current `pointer.ts` pattern) — one evaluate per move, then `waitForTimeout(650)`. Never loop `page.evaluate` for animation.

### Intro appears and vanishes instantly in the video

**Cause**: pointer shown before the page finishes rendering; insufficient `pause()` after intro; overlay init script visible during load.

**Fix**: follow the intro pattern above — wait for content, settle pause, then `ensureInstalled()` + long `intro_narration` + `intro_hold_after`.

### Sidebar too narrow / titles truncated

Increase `WALKTHROUGH_SIDEBAR_WIDTH` (try `560`–`600`). Call `ensureWalkthroughLayout(page)` after notebook `goto`.

### Bubble clips off-screen on right-edge buttons

Pointer placement prefers left-side bubbles when the target is in the right 32% of the viewport. If still clipped, check frame extracts and adjust target or bubble max-width in `pointer.ts`.

### `networkidle` never resolves

Use `domcontentloaded` + visible assertions + `waitForNetworkSettled()` instead of bare `networkidle`.

### Runner never finishes

1. Confirm Playwright exits: run `npx playwright test` alone first.
2. If Playwright passes but runner hangs, check `recorder.stop()` (screen capture thread).
3. Partial runs in `recordings/runs/` with only `events.jsonl` indicate an interrupted recording.

## Reference scenario

`scenarios/notebook/toolbar-tour.spec.ts` — notebook toolbar + header tour with intro/outro timing, service buttons, and header actions (Guide, Settings, Help).
