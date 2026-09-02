# Qwen Image skills

Experimental GuideAnts skills that reach **BF16-only** ComfyUI-video Qwen Image
generate / edit / inpaint workflows **without GuideAntsApi / ServiceModes changes**.

**Default deployment is a PC sandbox talking to the GPU host** over the qwen-image skill
gateway — a token-gated transparent reverse proxy to the video adapter inside the
comfyui-video container. Do not call `127.0.0.1:8189` or `:8190` from a PC sandbox.

Deliverables are written to the sandbox CWD (the notebook's output directory) as PNG files. Use bare filenames in commands (e.g. `-o gen.png`) — never prefix with `Output/`. Nothing here replaces product SD.cpp
`/txt2img` / `/img2img` or adds notebook image UI.

## Required Environment (PC → the GPU host)

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8189/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as the GPU host GA_QWEN_IMAGE_SKILL_TOKEN>
```

On the GPU host `.env` set `GA_QWEN_IMAGE_SKILL_TOKEN`. The comfyui-video stack publishes
port `8189` on `0.0.0.0` for LAN access.

If the vars are missing in the guide Environment, **stop and ask the user** to configure
them. Never scan the network or guess the GPU host's LAN IP — the operator supplies the URL.

### Gateway path map

| Path | Meaning |
|------|---------|
| `{BASE}/v1/capabilities` | Adapter readiness + workflow flags |
| `{BASE}/v1/image/jobs` | Edit / inpaint submit |
| `{BASE}/v1/image/generate/jobs` | Text-to-image submit |
| `{BASE}/v1/image/jobs/{id}` | Job status |
| `{BASE}/v1/image/jobs/{id}/cancel` | Cancel job |
| `{BASE}/v1/image/jobs/{id}/result` | Download PNG |
| `{BASE}/files` | Stage upload on the GPU host (optional) |

Auth header: `X-Qwen-Image-Skill-Token`.

## Skills (v1, BF16 only)

| Skill | What it does |
|-------|----------------|
| [`qwen-image`](qwen-image/) | Umbrella probe + routing |
| [`qwen-image-generate`](qwen-image-generate/) | Text → PNG (`qwen-image-v1`, BF16 weights) |
| [`qwen-image-edit`](qwen-image-edit/) | Image + prompt → PNG (`qwen-image-edit-bf16-v1`) |
| [`qwen-image-inpaint`](qwen-image-inpaint/) | Source + mask + prompt → PNG (inpaint workflow) |

## Common rules

- Run preflight/probe first; trust it over these docs.
- Paths must stay inside the notebook (bare filenames in CWD, `uploads/…`, `../…` for inputs outside CWD).
- First BF16 UNet load can take many minutes; default poll budget is 1800s.
- Report honestly what worked and what was blocked with preflight evidence.

## Precision policy

BF16 weights only on the GPU host. Generate API id remains `qwen-image-v1` with
`image_generate_ready` + `precision: bfloat16`. Do not invent FP8 skill paths.

## Limits

- Single-host job queue; cold-start VRAM load is heavy.
- Mask inpaint: white = editable region, black = preserve.
- Do not invent Comfy graphs or call ComfyUI `/prompt` directly from skills.
