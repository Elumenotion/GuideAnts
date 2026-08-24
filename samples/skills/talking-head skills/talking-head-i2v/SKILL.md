---
name: talking-head-i2v
description: "InfiniteTalk i2v talking-head via Max ComfyUI-video adapter (infinitetalk-i2v-v1). Avatar + audio + background → composited MP4."
metadata:
  guideants:
    enabled: true
    display_order: 51
    requires_toolsets: [sandbox]
---

# Talking-head i2v

Avatar + audio + background → MP4 using workflow `infinitetalk-i2v-v1` (generate +
CorridorKey composite). V2V is not available from this skill.

## Max gateway (192.168.0.111)

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://192.168.0.111:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
```

## Default parameters

`width=416 height=256 steps=14 cfg=1 fps=25 seed=-1`

`seed=-1` resolves to a random int **before** submit. Always log the resolved seed.
Writes `*-run-meta.json` next to the output with `seed` and `jobId`.

## Preflight

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://192.168.0.111:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
python3 Output/Skills/talking-head-i2v/scripts/preflight.py --for i2v
```

Requires `ready` and `composite_ready`.

## Run i2v

```bash
python3 Output/Skills/talking-head-i2v/scripts/video_tool.py i2v \
  --avatar Output/uploads/avatar.png \
  --audio Output/uploads/voice.wav \
  --background Output/uploads/plate.png \
  -o Output/talking-head.mp4 \
  [--seed -1] [--width 416 --height 256] [--steps 14 --cfg 1 --fps 25]
```

In ComfyUI: Workflows → `guideants/` or `guideants-jobs/`.

## Job control

```bash
python3 Output/Skills/talking-head-i2v/scripts/video_tool.py status <job_id>
python3 Output/Skills/talking-head-i2v/scripts/video_tool.py cancel <job_id>
python3 Output/Skills/talking-head-i2v/scripts/video_tool.py result <job_id> -o Output/out.mp4
```

Jobs are long-running; poll budget defaults to **3600s**. Quiet poll: progress-key
changes + seed + 60s heartbeat (no per-poll transport spam).

## Reporting

State output path, job id, resolved seed, readiness flags used, and preflight
evidence if blocked.
