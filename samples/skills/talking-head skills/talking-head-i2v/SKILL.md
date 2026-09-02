---
name: talking-head-i2v
description: "InfiniteTalk i2v talking-head via GPU host ComfyUI-video adapter (infinitetalk-i2v-v1). Avatar + audio + background → composited MP4."
metadata:
  guideants:
    enabled: true
    display_order: 51
    requires_toolsets: [sandbox]
---

# Talking-head i2v

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/talking-head-i2v/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o talking-head.mp4`); never prefix with `Output/`.

Avatar + audio + background → MP4 using workflow `infinitetalk-i2v-v1` (generate +
CorridorKey composite). V2V is not available from this skill.

## GPU host gateway (env-configured)

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=<token: GA_TALKING_HEAD_SKILL_TOKEN from the GPU host stack .env>
```

## Default parameters

`width=416 height=256 steps=4 cfg=1 fps=25 seed=-1`

`seed=-1` resolves to a random int **before** submit. Always log the resolved seed.
Writes `*-run-meta.json` next to the output with `seed` and `jobId`.

## Preflight

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=<token: GA_TALKING_HEAD_SKILL_TOKEN from the GPU host stack .env>
python3 Skills/talking-head-i2v/scripts/preflight.py --for i2v
```

Requires `ready` and `composite_ready`.

## Run i2v

```bash
python3 Skills/talking-head-i2v/scripts/video_tool.py i2v \
  --avatar uploads/avatar.png \
  --audio uploads/voice.wav \
  --background uploads/plate.png \
  -o talking-head.mp4 \
  [--seed -1] [--width 416 --height 256] [--steps 4 --cfg 1 --fps 25]
```

In ComfyUI: Workflows → `guideants/` or `guideants-jobs/`.

## Job control

```bash
python3 Skills/talking-head-i2v/scripts/video_tool.py status <job_id>
python3 Skills/talking-head-i2v/scripts/video_tool.py cancel <job_id>
python3 Skills/talking-head-i2v/scripts/video_tool.py result <job_id> -o out.mp4
```

Jobs are long-running; poll budget defaults to **3600s**. Quiet poll: progress-key
changes + seed + 60s heartbeat (no per-poll transport spam).

## Reporting

State output path, job id, resolved seed, readiness flags used, and preflight
evidence if blocked.
