# Session archive anatomy

A `browser-session-capture` session (one folder, or its `.zip`) is a complete
recording of one browser window plus microphone, with a verified
idle-compaction pass on top.

## Top-level files

| File | Role |
|------|------|
| `meta.json` | Recording identity: session id, start/stop, monitor, host, status. |
| `session.json` | Clock (t0, fps), paths, media probes (video: fps/codec/sha256; narration: rate/channels/sha256), anchors, `compact` block with verification proof. |
| `video.mp4` | Raw screen capture (H.264, 30 fps, monitor-sized). |
| `narration.wav` | Raw mic track (48 kHz mono), wall-clock aligned. |
| `video.compact.mp4` / `narration.compact.wav` | Idle-compacted pair — **the ones to use** for downstream work. |
| `edit_map.json` | Verified compaction edit map: `kept`/`removed` ranges in source ms, source + compact sha256s, alignment proof (setpts ratio). |
| `idle.json` | Idle analysis: static (visual) + silent (audio) ranges, thresholds, savings. |
| `index.json` | Checkpoint index: per-checkpoint id, t_ms, tab, foreground flag, trigger, url, title, scroll, which artifacts exist. |
| `events.jsonl` | `tab.open` / `tab.focus` / `navigate` / `checkpoint` events. |
| `activity.jsonl` | Visual change-detection samples feeding idle detection. |
| `windows.jsonl` / `windows_intervals.jsonl` | Foreground window records + merged intervals (which app was in front when). |
| `checkpoints/NNNNNN/` | `meta.json`, `text.txt` (extracted page text), `page.mhtml` (archive), `screenshot.png` (early checkpoints only). |
| `derived/compact-*/` | Intermediate `.mkv` mux files from compaction. |
| `heartbeat.json`, `integrity.jsonl`, `live_status.json` | Recorder liveness/state markers. |

## What each phase uses

- **Ingest/report**: everything above; the checkpoint timeline + `text.txt`
  files reconstruct what was on screen; `windows_intervals.jsonl` shows when
  other apps were in front.
- **Transcribe**: `narration.compact.wav` (matches `video.compact.mp4`; use the
  raw `narration.wav` only if the user wants the untouched wall clock).
- **Script**: the clean transcript text + the user's story definition. The
  checkpoint texts are reference material (what the user was looking at while
  narrating), not script input.

## Timestamps

`session.json.clock` gives `t0_epoch_ms`; checkpoint `t_ms` values are
session-relative. The compaction re-times the pair to the audio wall clock
(`setpts_ratio` in `edit_map.json.proof.alignment`). Caption times from the
timed transcript are compact-timeline times; map to raw video via `edit_map`
if ever needed.

## Sanity checks worth quoting

- `edit_map.json.status == "verified"` and `proof.content_verified` true.
- `session.json.status == "complete"`.
- narration sample count / rate ≈ duration; video duration ≈ compact video
  duration + removed idle time.
