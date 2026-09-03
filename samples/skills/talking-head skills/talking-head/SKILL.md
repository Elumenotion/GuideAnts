---
name: talking-head
description: "Make a talking-head MP4 on Max: avatar + audio + plate → 416×256 InfiniteTalk, CorridorKey @ 416×234, BasicVSR++ FG, 1280×720. Submit with video_tool.py, then poll status on later sandbox calls."
metadata:
  guideants:
    enabled: true
    display_order: 50
    requires_toolsets: [sandbox]
---

# Talking-head

This is the only talking-head skill. Avatar + audio + background plate → **1280×720 MP4**.

One adapter job: InfiniteTalk 416×256 → crop 416×234 → CorridorKey @ native →
BasicVSR++ 4× on keyed FG → 720p. Plate blur σ=1.5 before CK; FG sharpen 0.

Sandbox CWD is the notebook **output directory**. Scripts:
`Skills/talking-head/scripts/`. Write the MP4 with a **bare filename**
(`-o talking-head.mp4`). Never prefix `-o` with `Output/`.

Do not invent a POST. `scripts/video_tool.py` submits: files `source` / `audio` /
`background`; form field `parameters` is **one JSON object**. Do not pass
`--width` `--height` `--steps` `--cfg` `--fps` unless the user explicitly asks to
deviate. Do not call ComfyUI `/free`. Do not start a second job while this one runs.
Do not cancel CorridorKey frame 0. V2V is not available.

## Environment

Scripts read `TALKING_HEAD_SKILL_BASE_URL` and `TALKING_HEAD_SKILL_TOKEN` from the
guide Environment. Do **not** hardcode or `export` them inline.

```bash
printenv TALKING_HEAD_SKILL_BASE_URL >/dev/null && printenv TALKING_HEAD_SKILL_TOKEN >/dev/null && echo "env ok" || echo "env missing"
```

If either is missing, stop and ask the user to set them. Never scan the LAN.

Contract: `references/parameters.md`. Stages and measured times: `references/workflows.md`.

## How long this takes (measured)

A sandbox script call is killed after about **10 minutes**. These jobs last much
longer than that. **`i2v` submits and exits.** You poll `status` on later calls.

The adapter sets `frames` from audio duration at 25 fps (max 7200). Tested clips
were **10.56s / 264 frames**. Pipeline wall-clock for those 10s clips:

| Log | jobId prefix | elapsed |
|-----|--------------|---------|
| `ac-t3-gray-t-…-plateblur-pipeline.log` | `a217a4bd` | **1626s** |
| `ac-t3-blue-t-two-arms-…-plateblur-pipeline.log` | `faf6d5a4` | **2273s** (CK 973s, VSR 402s) |
| `ac-t3-service-pipeline.log` | `902fc149` | **3114s** |
| `ac-t3-blue-shirt-pipeline.log` | — | **3341s** |

That is already 27–56 minutes **for ten seconds of audio**. After CK frame 0,
telemetry is **~2.6s/frame** (`frame_elapsed_s: 2.6`). Frame 0 itself is often
30s–4 min (red first frame **216s**). More audio → more frames → longer wall
time. An 80s wav is 2000 frames. Do not treat an hour as finished or stuck.
Keep polling until `state` is `completed` or `failed`.

## 1. Preflight

```bash
python3 Skills/talking-head/scripts/preflight.py --for i2v
```

Open when: gateway up, `ready`, `composite_ready`, `infinitetalk-i2v-v1` listed.
Do not fail because `fg_upscaler` / canvas keys are absent. If those keys **are**
present, they must be `basicvsrpp` and 1280×720. If `open` is false, quote blockers
and stop.

## 2. Submit (returns immediately)

Inputs must be inside the notebook. Use the paths the user actually has.

```bash
python3 Skills/talking-head/scripts/video_tool.py i2v \
  --avatar uploads/avatar.png \
  --audio uploads/voice.wav \
  --background uploads/plate.png \
  -o talking-head.mp4
```

CLI defaults (do not add flags): `width=416 height=256 steps=4 cfg=1 fps=25 seed=-1`.
Stderr logs `seed=` and `jobId=`. Stdout is one JSON object (`jobId`, `seed`,
`outputPath`, `runMetaPath`). Writes `talking-head-run-meta.json`. This call does
**not** wait for the MP4.

## 3. Poll on later sandbox calls

One wait + one status per call. Do not loop until done inside a single script.

```bash
sleep 60 && python3 Skills/talking-head/scripts/video_tool.py status <job_id>
```

`job_id` is 32 lowercase hex characters. Read `state` (and `progress.phase` /
`message` when present):

- `queued` / `uploading` / `waiting` / `sampling` / `compositing` → wait ~60s, poll again
- `completed` → download
- `failed` / `cancelled` → stop and quote `error`. Do **not** call `i2v` again.
  That redoes InfiniteTalk and discards `green.mkv`. Post-CorridorKey failures
  retry from saved EXRs (`--clip-root`) inside the adapter; keep polling while
  `state` is still `compositing`. Terminal `failed` after that retry is a stop.

Tell the user the phase and any frame counts from status. CorridorKey frame 0
can sit with no new frames for minutes. That is compile, not a hang. Do not
cancel. Do not submit again.

## 4. Download

```bash
python3 Skills/talking-head/scripts/video_tool.py result <job_id> -o talking-head.mp4
```

Cancel only if the user asks:

```bash
python3 Skills/talking-head/scripts/video_tool.py cancel <job_id>
```

## Reporting

`jobId`, resolved `seed`, last `state`/`phase`, output path when downloaded,
preflight evidence if blocked. Delivery is 1280×720 H.264.
