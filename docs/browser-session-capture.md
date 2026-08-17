# Browser session capture

Live screen + narration capture while you browse in a dedicated Chrome window. Every session shares one clock across video, mic, browser checkpoints, and OS foreground-window bounds so you can jump to a timecode later and recover what was on screen.

For scripted, repeatable demos see [walkthroughs/README.md](../walkthroughs/README.md). This tool is for **human-driven** sessions: any site, multiple tabs, switching to other apps.

## CLI reference

| Command | Purpose |
|---------|---------|
| `devices` | List monitors and microphones |
| `preflight` | Prove monitor + exact dshow mic endpoint before capture |
| `start` | Begin live capture |
| `sessions` | List sessions that can be resumed |
| `at` | Lookup state at a timecode (optional frame/crop extract) |
| `audit` | Read-only integrity audit with rejection codes |
| `status` | Session status + audit summary |
| `validate` | Build per-app window crops + narration clips |
| `analyze-idle` | Detect visually static + silent ranges (requires passing audit) |
| `compact` | Build verified compact media + `edit_map.json` (requires passing audit) |
| `visual-salvage` | Video-only salvage for damaged sessions (`visual_only_degraded`) |
| `prune` | Move verified source media to `.source` backups (requires content proof) |
| `salvage` | Rebuild `session.json` from interrupted capture |

Use [`scripts/browser_session_control.py`](../scripts/browser_session_control.py) during an active capture to drive the same Playwright Chrome session (see [Shared session control](#shared-session-control)).

```powershell
python scripts/browser_session_capture.py <command> --help
```

## Setup (once)

From the repo root:

```powershell
pip install -r scripts/screen_record-requirements.txt -r scripts/browser_session-requirements.txt
python -m playwright install chrome
```

`ffmpeg` must be on `PATH` for live crash-resilient video capture, frame extraction (`at --crop`), compaction, and media probing. Most Windows builds already have it; if not, install and reopen the terminal.

## Your hardware (example listing)

Run this any time to refresh monitor and microphone indices on your machine:

```powershell
python scripts/browser_session_capture.py devices
```

Typical output on this workstation:

| Monitor | Resolution | Origin | Use |
|---------|------------|--------|-----|
| **1** | 2560×1440 | (0, 0) | Primary — default capture target |
| **2** | 2560×1440 | (2560, 0) | Secondary — use `--monitor 2` if you demo there |

Common narration inputs (indices change if you plug/unplug devices — always run `devices` first):

| Index | Device |
|-------|--------|
| 1 | Headset (LE-Bose QC Ultra Headphones) |
| 2 / 10 / 21 | Microphone (HD Pro Webcam C920) |
| 22 | Microphone (Realtek HD Audio Mic input) |

Narration uses the **exact configured dshow endpoint** via unified FFmpeg A/V capture. Set the mic you want in **Settings → System → Sound → Input** before starting, then pass the exact device name to `preflight`. Capture does **not** silently substitute microphone backends on failure.

## Session status vocabulary

| Status | Meaning |
|--------|---------|
| `recording` | Live capture in progress |
| `complete` | All required tracks have validated coverage |
| `interrupted` | Capture stopped with gaps or errors |
| `recovered_with_gap` | Restart succeeded after a required-track failure |
| `salvaged` | Rebuilt from partial artifacts |
| `failed` | Restart failed; session stopped |
| `no_changes` | Compaction found nothing to remove |
| `verified` | Compact output passed content + coverage proof |
| `visual_only_degraded` | Video-only salvage; no audio, not suitable for transcription |

Run `audit` before `analyze-idle`, `compact`, or `prune`. A session with `AUDIO_COVERAGE_GAP`, `PLAYWRIGHT_EVIDENCE_EMPTY`, or `SESSION_INTERRUPTED` will be rejected.

## Quick start

```powershell
# From repo root — capture on primary monitor, two start tabs
python scripts/browser_session_capture.py start `
  --monitor 1 `
  --url https://example.com `
  --url https://en.wikipedia.org

# Browse, narrate, switch tabs and apps.
# Click the interactive terminal, then press Enter or Q to stop (Ctrl+C also works).
# Press C in the terminal for a manual checkpoint.
```

Sessions are written under `recordings/sessions/<timestamp>_<slug>/`.

Logins persist in `recordings/browser-profile/` (gitignored). This is a dedicated capture Chrome, not your daily browser profile.

## During a session

| Action | How |
|--------|-----|
| Stop recording | Click the **interactive terminal**, then press **Enter** or **Q** (Ctrl+C also works) |
| Manual checkpoint | Click the **terminal**, then press **C** |
| New tab | Ctrl+T in capture Chrome |
| Switch tabs | Ctrl+Tab or click tab bar |
| Switch apps | Alt+Tab — foreground window bounds are tracked for later crop |

## Shared session control

While capture is running, the capture process exposes a **localhost-only** Playwright control endpoint. Both you and an agent can operate the same dedicated Chrome window through that endpoint; navigations, tab changes, and manual checkpoints are recorded in `events.jsonl`, `index.json`, and `checkpoints/`.

When capture starts, the terminal prints the active control descriptor path, for example:

```text
Control endpoint: D:\repos\GuideAnts-qwen38-27b-gguf\recordings\sessions\20260817_demo_session\part-0002\control.json
```

That file contains the loopback host/port and an unguessable token. It is removed when capture stops.

### Control CLI

From the repo root:

```powershell
# Show active session + tabs
python scripts/browser_session_control.py status

# Navigate the focused capture tab
python scripts/browser_session_control.py goto http://192.168.0.111:5107/

# Open another tab
python scripts/browser_session_control.py tab-new https://example.com

# List tabs, switch tabs, write a manual checkpoint
python scripts/browser_session_control.py tab-list
python scripts/browser_session_control.py tab-select 0
python scripts/browser_session_control.py checkpoint

# Interact with the page
python scripts/browser_session_control.py click "text=Quick Start"
python scripts/browser_session_control.py fill "input[placeholder='Search conversations...']" "demo"
python scripts/browser_session_control.py press Enter
```

Target a specific capture part explicitly when multiple descriptors exist:

```powershell
python scripts/browser_session_control.py status `
  --session-dir recordings/sessions/20260817_demo_session/part-0002
```

### What gets recorded

- `goto`, `tab-new`, `tab-select`, and `tab-close` go through the capture Playwright context.
- Navigation checkpoints and MHTML archives are written by the existing browser observer.
- `checkpoint` triggers the same manual checkpoint path as pressing **C** in the capture terminal.
- Screen video, narration, and foreground-window crops continue on the same session clock.

Do **not** drive the capture Chrome with OS-level keyboard automation or a separate Playwright CLI session. Those bypass the capture observer and will not produce browser checkpoints.

## Session layouts

### Flat session (default)

A single capture run produces one directory with media and metadata at the top level:

```
recordings/sessions/<timestamp>_<slug>/
  session.json            # schema v2: clock, monitor, paths, media anchors
  session.provisional.json  # written at start, removed on finalize
  meta.json
  video.mp4
  narration.wav
  windows.jsonl           # foreground window + crop rect over time
  events.jsonl            # tab open/focus/close, view.activity, checkpoints
  index.json              # checkpoint index by tab
  checkpoints/
    000012/
      meta.json
      screenshot.png
      text.txt
      page.mhtml          # navigations / manual checkpoints (unless --no-mhtml)
  lookup/                 # created by `at`
```

### Chain session (rolling or resumed)

Rolling capture (`--roll-duration` / `--roll-size-mb`) or resuming a prior session uses a chain with independently recoverable parts:

```
recordings/sessions/<timestamp>_<slug>/
  chain.json
  part-0001/
    session.json
    video.mp4
    narration.wav
    windows.jsonl
    events.jsonl
    index.json
    checkpoints/
  part-0002/
    ...
```

On **first resume** of a flat session, existing artifacts are moved into `part-0001/` and `chain.json` is created; new capture goes to `part-0002/`.

## Rolling sessions

```powershell
python scripts/browser_session_capture.py start `
  --slug long-demo `
  --roll-duration 1800 `
  --roll-size-mb 2048
```

- Creates a chain layout from the first part.
- Browser tabs stay open across part boundaries.
- Each part has its own media, events, and checkpoints.
- Lookup across parts: `at <chain-dir> --t 45:00 --time-basis chain`

## Resume later

```powershell
# List sessions you can resume
python scripts/browser_session_capture.py sessions

# JSON output
python scripts/browser_session_capture.py sessions --json

# Resume a previous flat session or chain
python scripts/browser_session_capture.py start --resume recordings/sessions/20260817_demo_session
```

- Chrome reuses the persistent profile (`recordings/browser-profile/`).
- Initial tabs are reopened from the last part's open tab URLs when available.
- Override with explicit `--url` flags if needed.
- Chain lookup spans all parts: `at <chain-dir> --t 1:30:00 --time-basis chain`

## Sample scripts

Copy these into your own `.ps1` files or run blocks directly in PowerShell from the repo root.

### List monitors and microphones

```powershell
# scripts/samples/list-capture-devices.ps1
Set-Location $PSScriptRoot\..\..
python scripts/browser_session_capture.py devices
```

### Primary-monitor capture (default)

```powershell
# scripts/samples/capture-primary.ps1
Set-Location $PSScriptRoot\..\..
$slug = "demo-$(Get-Date -Format 'yyyyMMdd-HHmm')"
python scripts/browser_session_capture.py start `
  --monitor 1 `
  --fps 30 `
  --slug $slug `
  --url http://localhost:5107 `
  --url https://example.com
```

Place the capture Chrome on **monitor 1**. The tool positions the window at the monitor origin automatically; keep other windows from covering it when you want the browser in the recording.

### Secondary-monitor capture

```powershell
# scripts/samples/capture-secondary.ps1
Set-Location $PSScriptRoot\..\..
python scripts/browser_session_capture.py start `
  --monitor 2 `
  --fps 30 `
  --slug "secondary-demo" `
  --url https://example.com
```

### Local app + reference site (two tabs)

```powershell
# scripts/samples/capture-guideants-local.ps1
Set-Location $PSScriptRoot\..\..
python scripts/browser_session_capture.py start `
  --monitor 1 `
  --slug "guideants-walkthrough" `
  --url http://localhost:5107 `
  --url https://github.com
```

### Lightweight capture (no MHTML archives)

MHTML is large but useful for offline page recovery. Skip it for long sessions:

```powershell
python scripts/browser_session_capture.py start `
  --monitor 1 `
  --no-mhtml `
  --url https://example.com
```

### Fixed output directory

```powershell
$run = "recordings/sessions/my-rehearsal-001"
New-Item -ItemType Directory -Force -Path $run | Out-Null
python scripts/browser_session_capture.py start `
  --monitor 1 `
  --session-dir $run `
  --url https://example.com
```

Cannot combine `--session-dir` with `--resume`.

## Lookup

### At a timecode

```powershell
# JSON only (source timeline, flat session or part dir)
python scripts/browser_session_capture.py at recordings/sessions/20260817_103000_session --t 1:23.4

# Full frame + foreground-window crop PNG
python scripts/browser_session_capture.py at recordings/sessions/20260817_103000_session --t 1:23.4 --crop

# Chain timeline (pass chain dir, not part dir)
python scripts/browser_session_capture.py at recordings/sessions/my-chain --t 1:30:00 --time-basis chain

# Compact timeline (after compaction)
python scripts/browser_session_capture.py at recordings/sessions/<session> --t 1:00 --time-basis compact
```

Extracted images land in `<part>/lookup/` (`frame_*.png`, `crop_*.png`).

For chain sessions:
- `--time-basis chain` + chain directory → resolves across all parts.
- `--time-basis source` + `part-000N/` directory → local part timeline.
- `--time-basis compact` + part directory (after `compact`).

### Batch lookup from an edit decision list

```powershell
# scripts/samples/lookup-markers.ps1
param(
  [Parameter(Mandatory)][string]$SessionDir,
  [string[]]$Timecodes = @("0:15.0", "1:23.4", "2:05.0")
)

Set-Location $PSScriptRoot\..\..
foreach ($t in $Timecodes) {
  $safe = ($t -replace "[:.]", "-")
  $out = Join-Path $SessionDir "lookup\marker-$safe.json"
  New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
  python scripts/browser_session_capture.py at $SessionDir --t $t --crop |
    Out-File -Encoding utf8 $out
  Write-Host "Wrote $out"
}
```

### Lookup result shape

`at` returns JSON with:

- `status` — `ok`, `removed` (compact range), or `unavailable`
- `query.time_basis` / `query.query_ms` / `query.source_ms` / `query.video_ms` / `query.frame`
- `query.resolution` — chain part, mapping details when using non-source bases
- `surface` — `browser` when capture Chrome was on top, `other_window` otherwise
- `window` — title, process, `crop` rect in **video pixel space** (monitor-local)
- `foreground` — visible tab checkpoint (only when `surface` is `browser`)
- `tabs` — last checkpoint per open tab at that time
- `tab_list` — which tabs existed and which was focused

**Assembly hints:**

- Browser on top → use checkpoint screenshot/text/MHTML; cut video at `query.video_ms`
- Other app on top → use `window.crop` or `--crop` PNG; label with `window.title` / `window.process`
- Source narration → `narration.wav` on the part's source timeline
- **Final edit** → use `narration.compact.wav` + `video.compact.mp4` on the **compact** timeline (see below)

## Validation pack

Build per-app window crops and narration clips for quick review:

```powershell
python scripts/browser_session_capture.py validate recordings/sessions/<session-or-part>

# Wider narration context around each snapshot
python scripts/browser_session_capture.py validate recordings/sessions/<session> --pad-sec 2

# After compaction, validate against compact media
python scripts/browser_session_capture.py validate recordings/sessions/<part> --time-basis compact
```

Writes `validation/manifest.json` and one folder per detected app segment:

```
validation/
  manifest.json
  01_google-chrome-about-blank/
    window.png
    narration.wav
    meta.json
  02_windows-terminal-powershell/
    ...
```

Snapshot selection prefers stable, non-animating window bounds (avoids mid-resize crops).

## Idle analysis and compaction

Trim only when the **view is visually static** and narration is **silent**. Streaming agent output counts as activity even when you are not talking.

```powershell
# Analyze without changing media
python scripts/browser_session_capture.py analyze-idle recordings/sessions/<session>

# Tune detection
python scripts/browser_session_capture.py analyze-idle recordings/sessions/<session> `
  --min-idle-sec 8 `
  --silence-enter-db -42 `
  --silence-exit-db -38 `
  --pad-sec 0.75 `
  --sample-hz 2

# Build verified compact outputs + edit_map.json (keeps originals)
python scripts/browser_session_capture.py compact recordings/sessions/<session>

# Move originals to .source backups only after verified compact
python scripts/browser_session_capture.py prune recordings/sessions/<session>
```

Outputs:

| File | Purpose |
|------|---------|
| `activity.jsonl` | Visual and DOM activity pulses |
| `idle.json` | Proposed static+silent ranges and savings report |
| `edit_map.json` | Bidirectional source ↔ compact mapping |
| `video.compact.mp4` / `narration.compact.wav` | Verified compact media |

**Important:** `analyze-idle` uses frame differencing on the recorded video, not window-title or tab-event heuristics. DOM `view.activity` events are an additional keep signal only. Missing narration is an error, not silence. Compact verification probes the compact outputs themselves and permits only media-frame timing tolerance; source hashes must still match.

### Editorial workflow (compact timeline)

For final production where transcription must match video frames:

1. Run `analyze-idle` then `compact` on each part (or flat session).
2. Transcribe **`narration.compact.wav`**.
3. Use **compact timecodes** (`compact_ms`) as the editorial clock.
4. Extract frames from **`video.compact.mp4`** at the same compact times.
5. Use `at --time-basis compact` to cross-reference session metadata.

Expect ±1 frame (~33 ms at 30 fps) tolerance, not sample-perfect sync. Source media timelines can drift slightly; compact media is the aligned deliverable.

## Time bases

| Basis | `--t` means | Pass to `at` |
|-------|-------------|--------------|
| `source` (default) | Part-local original timeline | Part dir or flat session dir |
| `compact` | Timeline after idle compaction | Part dir (after `compact`) |
| `chain` | Cumulative time across all parts | Chain dir |

Removed compact ranges return `status: removed` with adjacent kept boundaries — never a guessed nearest time.

## Recovery

If capture was interrupted before finalize:

```powershell
# Rebuild session.json from video sidecar + artifacts
python scripts/browser_session_capture.py salvage recordings/sessions/<session-or-part>

# Salvage all parts in a chain
python scripts/browser_session_capture.py salvage recordings/sessions/<chain-dir>
```

Chain salvage scans every `part-*` directory, including provisional or empty interrupted parts, and records explicit `duration_known`, `unknown_parts`, and `duration_status` metadata. It never invents a duration or chain offset across an unknown part; resume refuses such a chain until it is repaired.

Live video is written as fragmented MP4 and narration is durably updated while recording. If an interruption leaves a recoverable partial WAV, salvage preserves the original bytes as `narration.partial.wav` before creating a repaired WAV. Recovered media should be documented with a recovery manifest; raw source files are never replaced by compaction or salvage.

### Open the latest session folder

```powershell
# scripts/samples/open-latest-session.ps1
Set-Location $PSScriptRoot\..\..
$latest = Get-ChildItem recordings/sessions -Directory |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
if ($latest) {
  explorer $latest.FullName
} else {
  Write-Host "No sessions found under recordings/sessions/"
}
```

## Monitor tips (dual 2560×1440)

1. **Pick one capture monitor** and keep the capture Chrome there for browser-heavy segments.
2. **Monitor 1** `(0,0)` is the default; use `--monitor 2` when demoing on the right display.
3. Window crop rects are stored relative to the **recorded** monitor origin — do not mix lookups across sessions recorded on different monitors.
4. If a window straddles monitors, `windows.jsonl` sets `clamped: true` and the crop is the visible portion on the capture monitor only.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `No module named 'playwright'` | Run setup pip install + `python -m playwright install chrome` |
| Narration capture failed | Set a working default mic; ensure `ffmpeg` on PATH for dshow fallback |
| Enter/Q to stop does not work | Confirm the capture terminal is interactive and focused. If it reports that stdin is unavailable, use the control CLI `stop` command. |
| Node `EPIPE` or Playwright thread warnings after stop | Treat these as teardown failures, not harmless noise. Stop the capture, close any remaining capture Chrome, and restart with the current capture code. |
| Chrome shows `--no-sandbox` warning banner | Capture strips `--no-sandbox` and `--enable-automation`. Restart capture after updating. |
| Google / sites show captcha immediately | Log into Google once in the capture profile, then reuse it. |
| `at` on chain dir fails with source basis | Use `--time-basis chain`, or point `at` at a specific `part-000N/` |
| `at --crop` fails | Install `ffmpeg`; confirm `video.mp4` exists |
| `compact` duration mismatch | Re-run `analyze-idle`; verify source media was not edited after analysis |
| `prune` refused | Compact outputs must be verified and source hashes must still match |
| `resume` fails | Run `sessions` to confirm path; session must contain `session.json` or `chain.json` |
| Chrome profile locked | Close other Playwright/capture sessions using `recordings/browser-profile/` |

## Related

- Scripted walkthroughs: [walkthroughs/README.md](../walkthroughs/README.md)
- Screen recorder implementation: [scripts/screen_recorder.py](../scripts/screen_recorder.py)
- CLI entry point: [scripts/browser_session_capture.py](../scripts/browser_session_capture.py)
