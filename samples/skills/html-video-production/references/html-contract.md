# The HTML contract (f(T))

The capture is only as deterministic as the page obeys this contract. Verify
each clause against the actual file before the first full capture; a page
that violates one will render garbage frames that look plausible in a 24-frame
speed test.

## Required page globals

```js
// 1. A single time variable. The only input to the render path.
let T = 0;                       // or var / const-let pair - read it back after ?t= load

// 2. A pure render function: given T, the page state is fully determined.
//    No Math.random() inside render(), no Date.now(), no wall-clock timers,
//    no CSS/SMIL animations the function doesn't drive.
function render() { /* reads T, writes the whole visible state */ }

// 3. Boot: read ?t=, set T, render once.
const t0 = new URLSearchParams(location.search).get('t');
if (t0 !== null) T = parseFloat(t0);
render();
```

The capture does, per frame: `page.evaluate((t) => { T = t; render(); }, t)`
then `page.screenshot()`. If `render()` is not pure (hidden state accumulates
across calls), every frame after the first is suspect.

## Auto-scaling

The stage is authored at a fixed size (e.g. 1920x1080) and a `fit()` function
scales it to the viewport. The capture viewport is set in the plan
(`viewport: [1280, 720]`); `fit()` must key off the *viewport* (or window),
not a hardcoded size, or the output resolution silently drifts from the plan.
Every frame is then exactly the plan's viewport pixels.

## Hidden UI

The page ships with developer UI that must not appear in the video:

- `#transport` - a `position: fixed` bottom playback bar (play/pause, scrubber).
- `#dbg` - an on-screen T/phase readout.

`capture.py` hides both (ids come from the plan's `hide`) after load and
before frame 0. If the page renames them, update the plan - do not edit the
page. The verify step's bottom-band check is the tripwire: a `flat` band
(mean clearly off from the content above, near-zero std) in the bottom 7% of
a spot frame means UI leaked through.

## Frame formula

For a scenario with first time `t0` (SEG0) and last time `t1` (SEG1) at
`fps`:

```
N = round((t1 - t0) * fps)            # frames, indexed 0..N-1
t_i = t0 + i / fps                    # T for frame i
total_duration = sum(N_k) / fps
```

Example, 24 fps:

| scenario | t0 | t1 | N |
|---|---|---|---|
| sc1 | 12.00 | 180.00 | 4032 |
| sc2 | 180.00 | 300.00 | 2880 |
| total | | | **6912 = 288.00s** |

The audio slice is `wav` from scenario 0's `t0` (12s in the example); its duration must
match `total_duration` within one frame; `-shortest` in the mux trims the
remainder.

## Lead-in gaps

A scenario's content may not move until after its `t0` (e.g. sc2's title lands
at T=182.4, 2.4s after the cut at 180). The stage shows its initial
state during the gap. **This is correct** - the narrator is finishing the
previous scenario or starting the next. Do not retiming `t0` to chase motion.

## Canonical commands (example)

```bash
# audio
ffmpeg -y -i narration.wav -ss 12 -c:a aac -b:a 128k combined_audio.m4a

# mux
ffmpeg -y -framerate 24 -i combined_frames/f_%05d.png -i combined_audio.m4a \
  -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \
  -c:a aac -b:a 128k -shortest \
  combined.mp4
```

Capture specifics: playwright chromium, headless, viewport 1280x720,
`goto('file://<abs>/<page>.html?t=<t0>')`, wait ~800ms, hide `#transport` +
`#dbg`, then the per-frame `T=t; render(); screenshot()` loop above.

## Failure modes this skill exists to prevent

| Failure | Cause | Guard |
|---|---|---|
| Transport bar in the video | `#transport` not hidden (or renamed) | plan `hide` + verify bottom-band flag |
| Wrong resolution / letterboxed | `fit()` keys off wrong size; viewport mismatch | preflight prints expected viewport; verify checks actual |
| Drift vs audio | real-time playback instead of `T` stepping | contract clause 2; capture is step-only |
| Blank video | `?t=` not read at boot, or `render()` not idempotent | speed-test frames inspected before full run |
| Reused stale frames | old `qa_frames_*` dir (bar present) fed to mux | assemble takes only dirs named in the plan |
| Install spiral | `require('playwright')` fails in a fresh cwd | preflight reports the blocker; never `npm install` |
| Re-doing finished work | deleting frame dirs after an interruption | capture resume: existing frames are skipped |
| Duration mismatch | frame math off by one; audio slice at wrong offset | preflight + final print both expected values; `-shortest` |
| Hung run | 15-min capture inside a synchronous sandbox call | background + status file; poll the JSON, never the log |
