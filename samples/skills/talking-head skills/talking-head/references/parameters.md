# Parameters — tested talking-head i2v path

Do not invent a POST. Submit only with `talking-head/scripts/video_tool.py`.

## Environment (required)

Scripts read these from the **guide Environment**. Do not `export` them in the
skill command block. Do not paste a token into chat.

| Variable | Meaning |
|----------|---------|
| `TALKING_HEAD_SKILL_BASE_URL` | `http://<max-lan-ip>:8189/talking-head-skill` (no trailing slash) |
| `TALKING_HEAD_SKILL_TOKEN` | Same value as Max `GA_TALKING_HEAD_SKILL_TOKEN` |

Header sent: `X-Talking-Head-Skill-Token`.

If either is missing, stop and ask the user to set them. Do not scan the LAN.

## How `video_tool.py` submits

Multipart POST `{BASE}/v1/talking-head/jobs`.

**Files** (Content-Type from suffix, not `mimetypes.guess_type`)

| File | Part name | Allowed suffixes |
|------|-----------|------------------|
| Avatar | `source` | `.png` `.jpg` `.jpeg` `.webp` → `image/png` `image/jpeg` `image/webp` |
| Audio | `audio` | `.wav` `.mp3` `.flac` `.ogg` → `audio/wav` `audio/mpeg` `audio/flac` `audio/ogg` |
| Plate | `background` | same image types as avatar |

Wav is required in tests (`.wav` / `audio/wav`). Other audio types are adapter-legal
but not the tested clip.

`--audio-pad` (default 0.5) prepends/appends that many seconds of digital silence to a
.wav input before upload. .wav only. The padded file is written inside the notebook
next to the output MP4 and is what the adapter sees. Frame count is derived from the
PADDED duration.

**Form fields**

| Part | Value |
|------|--------|
| `output_filename` | **basename only** of `-o` (e.g. `talking-head.mp4`). Must end in `.mp4`. A path here is rejected. |
| `workflow_version` | `infinitetalk-i2v-v1` (API id; graph is LongCat-Video-Avatar-1.5) |
| `parameters` | **one JSON object string**, not separate `width=` fields |

CLI defaults (do not pass `--width`/`--height`/`--steps`/`--cfg`/`--fps` unless the
user explicitly asks to deviate):

```json
{"width":416,"height":256,"steps":8,"cfg":1.0,"fps":25,"seed":<int>}
```

`steps=8` / `cfg=1` is the LongCat-Video-Avatar-1.5 DMD distill path. Do not pass
`steps=4`; that was InfiniteTalk LightX2V.

- `seed=-1` on the CLI → tool picks `0..2^31-1` **before** submit and logs `seed=`.
- Do **not** put `frames` in `parameters`. The adapter derives frame count from audio duration.
- `--positive` / `--negative` exist. Omit them for the tested prompts (adapter defaults).

Sending `width=416` as its own form field is ignored. The adapter then uses
**832×480 / cfg 5**.

`-o` is the **notebook-local** MP4 path (CWD = notebook output dir; use a bare
filename like `talking-head.mp4`). The adapter never sees that directory.

## Composite (host env, not CLI)

Tested Max `comfyui-video.yml`:

| Env | Tested value | Role |
|-----|--------------|------|
| `VIDEO_COMPOSITE_FG_UPSCALER` | `basicvsrpp` | keyed FG upscale |
| `VIDEO_COMPOSITE_BACKGROUND_BLUR_SIGMA` | `1.5` | plate Gaussian **before** CorridorKey |
| `VIDEO_COMPOSITE_FOREGROUND_SHARPEN_AMOUNT` | `0` | no extra FG sharpen with VSR |

Delivery canvas **1280×720** is the adapter default (`VIDEO_COMPOSITE_WIDTH` /
`HEIGHT` if unset). Preflight requires `ready`, `composite_ready`, and
`infinitetalk-i2v-v1`. It does **not** fail when `fg_upscaler` / canvas keys are
absent. If those keys are present, they must be `basicvsrpp` and 1280×720.

Do not call ComfyUI `/free`. Do not start a second talking-head job while one is
sampling or compositing.

## Workflow graph (not CLI)

`audio_cfg_scale=1.0` is in the LongCat-Video-Avatar-1.5 graph. Do not invent a
CLI flag for it.
