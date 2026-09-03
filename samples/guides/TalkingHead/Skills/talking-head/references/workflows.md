# Workflows — tested talking-head i2v path

| Workflow id | Submit with |
|-------------|-------------|
| `infinitetalk-i2v-v1` | `talking-head/scripts/video_tool.py i2v` |

V2V is not a skill surface. Do not mutate workflow JSON.

## Stages (one adapter job)

| Stage | What runs | Size |
|-------|-----------|------|
| Generate | InfiniteTalk i2v, lossless green | 416×256 |
| Prepare | Crop to 16:9; plate focus-blur σ=1.5 | 416×234 FG |
| Key | CorridorKey at native LQ | 416×234 |
| Upscale | BasicVSR++ 4× on **keyed FG only** | 1664×936 |
| Composite | Fit FG over plate; H.264 MP4 | 1280×720 |

Avatar/FG are not blurred. FG sharpen is 0.

Progress: `sampling` during i2v, then `compositing` (CorridorKey frames, `basicvsrpp fg`
windows, encode). **Frame 0 of CorridorKey is often 30s–4 min** (MIOpen; red first
frame **216s**), then **~2.6s/frame** (`frame_elapsed_s: 2.6` on gray-t). Do not
cancel that window.

`video_tool.py i2v` submits and exits. Poll `status` on later sandbox calls
(`sleep 60 && … status <job_id>`). A sandbox script is killed after about 10
minutes; these jobs last much longer. Tested **10.56s / 264-frame** clips already
ran **1626s** (gray-t), **2273s** (blue-t two-arms; CK 973s, VSR 402s), **3114s**
(AC-T3 service), **3341s** (blue-shirt). Frame count is audio seconds × 25 (max
7200). Keep polling until `completed` or `failed`. There is no 3600s job deadline.

## After submit

```text
GET  /v1/talking-head/jobs/{jobId}          status (jobId is 32 hex chars)
POST /v1/talking-head/jobs/{jobId}/cancel   only if the user asks to cancel
GET  /v1/talking-head/jobs/{jobId}/result   MP4 (video_tool does this)
```

`video_tool.py i2v` writes `{stem}-run-meta.json` at submit (`seed`, `seedMode`,
`jobId`, `workflow`, `outputPath`). `result` updates that file after download.

ComfyUI `guideants-jobs/` shows the **generate** graph only. CK/VSR are
`run-corridorkey-composite.py`, not Comfy nodes.
