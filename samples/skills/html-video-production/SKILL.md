---
name: html-video-production
description: "Turn deterministic HTML animations (the pure f(T) contract: global T + render()) plus a narration audio track into a finished MP4: headless-Chromium frame-by-frame capture at fixed fps, audio slice, multi-scenario renumber, h264+aac mux, scripted verify. Use when the user has HTML animation files and narration audio and wants a finished video, including concatenating multiple scenarios into one file. No real-time playback, no GPU, no network."
metadata:
  guideants:
    enabled: true
    display_order: 60
    requires_toolsets: [sandbox]
---

# html-video-production

Paths - fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/html-video-production/scripts/` relative to it, so run the commands
in this file exactly as written. Write every deliverable to the CWD with a
**bare filename** (e.g. `combined.mp4`): never prefix an output path
with `Output/` - the CWD *is* the output directory. Inputs (the HTML files,
the narration wav) are bare CWD names; resolve nothing else.

## What this skill does

```text
scenario-*.html (pure f(T) contract)  +  narration.wav
  -> 1. capture      python3 capture.py --plan plan.json      per-scenario f_%05d.png (1280x720, no UI)
  -> 2. audio        ffmpeg -ss <t0> -c:a aac              combined_audio.m4a
  -> 3. assemble     python3 assemble.py s1 s2 ...         combined_frames/f_%05d.png (one sequence)
  -> 4. mux          ffmpeg -framerate 24 ... -shortest    <name>.mp4
  -> 5. verify       python3 verify.py final --mp4 ...     JSON verdict + spot frames
```

The animation is rendered **frame-by-frame by setting `T` and calling
`render()`** - never by playing the page in real time. Determinism is what
makes a 75-minute capture resumable and re-runnable.

**Read `references/html-contract.md` first** - the exact contract the capture
depends on (global `T`, pure `render()`, `?t=`, `fit()`, `#transport`,
`#dbg`, the frame-count formula) and the failure modes this skill exists to
prevent.

## Inputs

| Input | Required | Notes |
|---|---|---|
| `<scenario>.html` per scenario | yes | pure f(T); see the contract reference |
| `narration.wav` | yes | full narration; the video starts at scenario 0's `t0` |
| word-timing json | no | only for choosing/confirming SEG boundaries |

Per scenario: `html`, `t0` (first T = SEG0), `t1` (last T = SEG1). The video's
first frame is scenario 0 at T=`t0`; the audio slice starts at that same `t0`.
If a scenario's content doesn't move until later than its `t0`, the stage
correctly shows its initial state in that gap - do not "fix" it; the gap is
where the audio transition lives.

Frame math (the scripts do it; know it to spot-check):
`N_k = round((t1_k - t0_k) * fps)`, `t_i = t0_k + i/fps`, i in 0..N_k-1.
Total duration = sum(N)/fps must match the audio slice within one frame;
`-shortest` trims the remainder.

## Step 0 - Write the plan

One JSON file in the CWD - the single source of truth for the run. Example
(two scenarios; add one entry per scenario you concatenate):

```json
{
  "fps": 24,
  "viewport": [1280, 720],
  "hide": ["transport", "dbg"],
  "wait_ms": 800,
  "status": "capture_status.json",
  "wav": "narration.wav",
  "out_audio": "combined_audio.m4a",
  "out_mp4": "combined.mp4",
  "scenarios": [
    {"name": "sc1", "html": "scenario-a.html",    "t0": 12,    "t1": 180, "out": "sc1_frames"},
    {"name": "sc2", "html": "scenario-b.html",    "t0": 180,   "t1": 300, "out": "sc2_frames"}
  ]
}
```

## Step 1 - Preflight (cheap; once)

```bash
python3 Skills/html-video-production/scripts/verify.py preflight --plan plan.json
```

One JSON verdict `{ok, blockers, warnings, expected:{fps, viewport,
total_frames, duration_s, per_scenario}}`. It checks node + a resolvable
playwright (it **does not install anything** - a missing playwright is the
blocker to report, not a thing to fix), ffmpeg, ffprobe, and the existence of
every plan input, and prints the expected frame counts and duration that Step
7 verifies against. `ok:false` - stop and report the blockers.

## Step 2 - Speed test (24 frames)

```bash
node Skills/html-video-production/scripts/capture.py --plan plan.json --test 24
```

Renders 24 frames of scenario 0 into `speedtest/` (throwaway) and prints
`{frames, seconds, rate_fps, total_frames, eta_s}`. Use the measured rate for
the ETA. On this stack a 1280x720 capture runs ~10-12 fps, so 11k frames is
~15 minutes.

## Step 3 - Capture

Foreground when the speed-test ETA is under ~3 minutes; otherwise background
it - sandbox calls are stateless and each one must finish:

```bash
nohup python3 Skills/html-video-production/scripts/capture.py --plan plan.json \
  > capture.log 2>&1 & echo $!
```

Poll the status file (one tiny read per call - never `cat` the log):

```bash
python3 -c "import json;print(json.load(open('capture_status.json')))"
```

`{"phase":"complete"}` is done; the file also carries `rate_fps` and `eta_s`.

**Resume is built in.** Existing frame files (size > 0) are skipped. If a run
is interrupted, cancelled, or the sandbox dies, **re-run the exact same
command** - it continues where the frames left off. Never delete a partially
captured directory to "start clean"; the frames on disk *are* the state. The
one exception: if the scenario's `.html` changed since its frames were
written, delete that scenario's out dir (only that dir) first.

## Step 4 - Audio slice

```bash
ffmpeg -y -i <wav> -ss <t0 of scenario 0> -c:a aac -b:a 128k combined_audio.m4a
```

(e.g. with the example plan above: `ffmpeg -y -i narration.wav -ss 12 -c:a
aac -b:a 128k combined_audio.m4a`.) Confirm with ffprobe that the slice duration
matches `expected.duration_s` within a frame.

## Step 5 - Assemble one sequence

```bash
python3 Skills/html-video-production/scripts/assemble.py combined_frames \
  sc1_frames sc2_frames --fps 24
```

Symlinks each `<scenario-out>/f_*.png`, in the given argument order, into
`combined_frames/f_%05d.png` renumbered 0..N-1 (add `--copy` to materialize).
Prints `{total_frames, duration_s}` - must equal the plan's expected total.

## Step 6 - Mux

```bash
ffmpeg -y -framerate 24 -i combined_frames/f_%05d.png -i combined_audio.m4a \
  -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \
  -c:a aac -b:a 128k -shortest \
  combined.mp4
```

`-framerate 24` is the plan fps. `-shortest` trims the video to the audio
duration. Encoding ~11k frames takes ~4-6 minutes; if the call can't hold
that long, background it like the capture and write a sentinel
(`echo done > encode_status.json` once ffmpeg returns).

## Step 7 - Verify (scripted; quote the JSON)

```bash
python3 Skills/html-video-production/scripts/verify.py final \
  --mp4 combined.mp4 --plan plan.json
```

JSON verdict `{ok, checks:[{name,pass,evidence}], spot:[{t, file, band}]}`.
Hard checks: video codec h264, resolution = plan viewport, fps = plan fps,
**nb_frames = expected total**, duration within 2 frames of total/fps, audio
stream aac present. Spot frames default to t=0, each scenario boundary
(exactly at the cut), boundary+2.5s, and the last frame; add
`--at <seconds>` for known beats (a title landing, an end card). Each spot
frame also gets a bottom-band heuristic - a flat band clearly distinct from
the content above is a transport bar that wasn't hidden; a band flag is a
warning that blocks delivery until a human looks at the frame. Spot frames
are written to the CWD as `verify_*.jpg`.

`ok:false` - fix and re-verify; do not deliver on a fail.

## Step 8 - Report

End with: the mp4 (bare name + `Output/...` embed), duration vs expected,
frame count, the verify JSON bottom line (every check pass/fail), and the
spot-frame paths. No options, no "check if" - the verdict is in the JSON.

## Binding rules (from a real post-mortem)

1. **Never install playwright/chromium.** If preflight reports it missing,
   that is the answer; report it. It is expected to already resolve from
   `node_modules` up the CWD tree.
2. **Never reuse an older capture directory** (a `qa_frames_*` set, a prior
   run's leftovers) as input. Old frames may contain the transport bar or a
   different viewport; only directories produced by `capture.js` for this run
   qualify.
3. **One execution path.** Pick the command, run it. Do not present
   alternatives, do not hedge on facts the plan fixes.
4. **No real-time playback.** Every frame is `T=t; render()` + screenshot. A
   page left to animate itself drifts against the audio and is not
   resumable.
5. **Hiding the UI is part of the capture.** `hide` is applied per scenario,
   after load, before frame 0; the verify bottom-band check is the tripwire
   if it ever regresses.
6. **The gap between scenarios is content.** A scenario's `t0` can precede
   the moment its content starts moving; the blank lead-in matches the
   audio.

## Out of scope

Authoring/editing the HTML animations themselves (`html-craft`), narration
TTS and timing (the audiocpp skills), story/timeline planning
(`video-timeline`), captions/burn-in, thumbnails, publishing.

## References

- `references/html-contract.md` - the f(T) contract, the frame formula,
  canonical commands, and the failure modes this skill exists to prevent.
