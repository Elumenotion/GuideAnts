# html-video-production

Skill in the video-production family: turns **deterministic HTML animations**
(the pure f(T) contract - global `T` + idempotent `render()`, see
`references/html-contract.md`) plus a **narration audio track** into a
finished MP4, including concatenating several scenarios into one continuous
file. Frame-by-frame headless-Chromium capture (Playwright), ffmpeg audio
slice + h264/aac mux, and a scripted preflight/verify with a JSON verdict.
No real-time playback, no GPU, no network, nothing installed.

## Deliverable

One MP4 in the CWD: all scenarios in order, narration audio aligned to the
scenarios' T windows, 1280x720 (plan-set), plan fps, h264 + aac, no UI chrome.

## Scripts

| Script | Role |
|---|---|
| `scripts/capture.py` | per-scenario frame capture via Python playwright; resume-safe (skips existing frames); status file with rate/ETA; `--test 24` speed test |
| `scripts/assemble.py` | renumber per-scenario frame dirs into one `f_%05d.png` sequence (symlinks) |
| `scripts/verify.py` | `preflight` (deps + inputs + expected frame math) and `final` (ffprobe hard checks + spot frames + transport-band tripwire) - both print one JSON verdict |

## Example

Two scenarios concatenated end-to-end: per-scenario frame dirs assembled into
one sequence, the full narration sliced at scenario 0's `t0`, muxed ->
`combined.mp4`, 1280x720 h264+aac, every verify check passing.
