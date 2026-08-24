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

Avatar + audio + background → MP4 using workflow `infinitetalk-i2v-v1`.

## Environment (required for PC → Max)

```text
TALKING_HEAD_SKILL_BASE_URL=http://<max-lan-ip>:8189/talking-head-skill
TALKING_HEAD_SKILL_TOKEN=<same as Max GA_TALKING_HEAD_SKILL_TOKEN>
```

## Preflight

```bash
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

Default: `width=416 height=256 steps=14 cfg=1 fps=25 seed=-1` (seed `-1` → random
before submit; always logged). Poll budget **3600s**. Quiet poll: progress-key
changes + seed + 60s heartbeat.

## Job control

```bash
python3 Output/Skills/talking-head-i2v/scripts/video_tool.py status <job_id>
python3 Output/Skills/talking-head-i2v/scripts/video_tool.py cancel <job_id>
python3 Output/Skills/talking-head-i2v/scripts/video_tool.py result <job_id> -o Output/out.mp4
```

## Reporting

State output path, job id, resolved seed, readiness flags, and preflight evidence if blocked.
Writes `*-run-meta.json` next to the output.
