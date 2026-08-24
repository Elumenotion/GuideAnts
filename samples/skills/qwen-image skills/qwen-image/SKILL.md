---
name: qwen-image
description: "Probe Max BF16 Qwen Image readiness via the skill gateway and route to generate, edit, or inpaint skills. Use when the user needs Comfy/Qwen workflows beyond product SD tools, or when readiness is unclear."
metadata:
  guideants:
    enabled: true
    display_order: 40
    requires_toolsets: [sandbox]
---

# Qwen Image (umbrella)

Product notebook SD tools cover plain txt2img/img2img without Qwen/Comfy. Use this
family when the user needs **Qwen Image 2512 BF16** generate, edit, or inpaint via
the ComfyUI-video adapter on Max.

**PC sandbox → Max gateway.** Skills run on the workstation; the comfyui-video
container runs on Max (`guideants-video-stack`, LAN `192.168.0.111`, port `8189`).
Do **not** use `127.0.0.1` — that is the PC, not Max.

## When to use product SD tools instead

If the request is a simple notebook image with no Qwen-specific need, use GuideAnts'
built-in SD image tools — not these skills.

## Max gateway (192.168.0.111)

```bash
export QWEN_IMAGE_SKILL_BASE_URL=http://192.168.0.111:8189/qwen-image-skill
export QWEN_IMAGE_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
```

Max stack: `GA_COMFYUI_VIDEO_PORT=8189`, `GA_QWEN_IMAGE_SKILL_TOKEN` in
`guideants-video-stack/.env`. Header: `X-Qwen-Image-Skill-Token`.

## Probe first

```bash
export QWEN_IMAGE_SKILL_BASE_URL=http://192.168.0.111:8189/qwen-image-skill
export QWEN_IMAGE_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
python3 Output/Skills/qwen-image/scripts/probe.py
python3 Output/Skills/qwen-image/scripts/preflight.py --for probe
```

## Route to a task skill

| User wants… | Skill |
|-------------|-------|
| New image from text | `qwen-image-generate` |
| Restyle / edit existing image | `qwen-image-edit` |
| Masked fill / whiteboard completion | `qwen-image-inpaint` |
| Unclear / “is Max ready?” | stay here; run probe |

## Reporting

Quote probe/preflight evidence when blocked. Deliverables are PNGs under `Output/`.

Honest limits: cold BF16 UNet load can take many minutes; single-host queue; no UI picker.

Edit/inpaint always use tested Lightning (`steps=4 cfg=1 lora_strength=1`). Do not invent
20-step sampler settings for those skills.

See `references/cold-start-vram.md` for VRAM coexistence with InfiniteTalk and the
failure handoff at `artifacts/qwen-image-edit/FAILURE-HANDOFF-20260812.md`.

ComfyUI Workflows browser: `guideants/` (templates) and `guideants-jobs/` (submitted graphs).
