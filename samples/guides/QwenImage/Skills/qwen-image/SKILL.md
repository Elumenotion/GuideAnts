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

**Default path: PC sandbox → Max qwen-image gateway.** That host is a transparent
reverse proxy to the ComfyUI-video adapter (`/v1/capabilities`, `/v1/image/*`,
`/files`). Scripts use `QWEN_IMAGE_SKILL_BASE_URL` when set. Do not call
`127.0.0.1:8189` or `:8190` from a PC sandbox — those ports are inside Max.

## When to use product SD tools instead

If the request is a simple notebook image with no Qwen-specific need, use GuideAnts'
built-in SD image tools — not these skills.

## Environment (required for PC → Max)

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<max-lan-ip>:8189/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as Max GA_QWEN_IMAGE_SKILL_TOKEN>
```

Do **not** call `127.0.0.1:8189` or `:8190` from a PC sandbox.

## Probe first

```bash
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

Generate uses API id `qwen-image-v1` + `image_generate_ready`. Edit/inpaint always use
tested Lightning (`steps=4 cfg=1 lora_strength=1`). Do not invent 20-step sampler settings
for edit/inpaint.

See `references/cold-start-vram.md` for VRAM coexistence with InfiniteTalk and the
failure handoff at `artifacts/qwen-image-edit/FAILURE-HANDOFF-20260812.md`.
