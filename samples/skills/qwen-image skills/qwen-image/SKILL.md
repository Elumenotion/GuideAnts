---
name: qwen-image
description: "Probe GPU host BF16 Qwen Image readiness via the skill gateway and route to generate, edit, or inpaint skills. Use when the user needs Comfy/Qwen workflows beyond product SD tools, or when readiness is unclear."
metadata:
  guideants:
    enabled: true
    display_order: 40
    requires_toolsets: [sandbox]
---

# Qwen Image (umbrella)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/qwen-image/scripts/` relative to it, so run the commands in this file
exactly as written. Write every deliverable to the CWD with a **bare filename**
(e.g. `-o gen.png`): never prefix an output path with `Output/` — the CWD *is*
the output directory, so `Output/…` would create a nested `Output/` folder.

Product notebook SD tools cover plain txt2img/img2img without Qwen/Comfy. Use this
family when the user needs **Qwen Image 2512 BF16** generate, edit, or inpaint via
the ComfyUI-video adapter on the GPU host.

**PC sandbox → GPU host gateway.** Skills run on the workstation; the comfyui-video
container runs on the GPU host (`guideants-video-stack`); the gateway endpoint and token come from the guide Environment (`QWEN_IMAGE_SKILL_BASE_URL`, `QWEN_IMAGE_SKILL_TOKEN`).
Do **not** use `127.0.0.1` — that is the PC, not the GPU host.

## When to use product SD tools instead

If the request is a simple notebook image with no Qwen-specific need, use GuideAnts'
built-in SD image tools — not these skills.

## GPU host gateway (env-configured)

The scripts read `QWEN_IMAGE_SKILL_BASE_URL` and `QWEN_IMAGE_SKILL_TOKEN` from the
guide Environment automatically — do **not** hardcode or export them inline.

Verify before running:

```bash
printenv QWEN_IMAGE_SKILL_BASE_URL >/dev/null && printenv QWEN_IMAGE_SKILL_TOKEN >/dev/null && echo "env ok" || echo "env missing"
```

If either is missing, stop and ask the user to set it in the guide's Environment
variables. Never scan the LAN or guess the GPU host's IP.


GPU host stack: `GA_COMFYUI_VIDEO_PORT=8189`, `GA_QWEN_IMAGE_SKILL_TOKEN` in
`guideants-video-stack/.env`. Header: `X-Qwen-Image-Skill-Token`.

## Probe first

```bash
python3 Skills/qwen-image/scripts/probe.py
python3 Skills/qwen-image/scripts/preflight.py --for probe
```

## Route to a task skill

| User wants… | Skill |
|-------------|-------|
| New image from text | `qwen-image-generate` |
| Restyle / edit existing image | `qwen-image-edit` |
| Masked fill / whiteboard completion | `qwen-image-inpaint` |
| Unclear / “is the GPU host ready?” | stay here; run probe |

## Reporting

Quote probe/preflight evidence when blocked. Deliverables are PNGs written to the CWD (bare filenames).

Honest limits: cold BF16 UNet load can take many minutes; single-host queue; no UI picker.

Edit/inpaint always use tested Lightning (`steps=4 cfg=1 lora_strength=1`). Do not invent
20-step sampler settings for those skills.

See `references/cold-start-vram.md` for VRAM coexistence with InfiniteTalk and the
failure handoff at `artifacts/qwen-image-edit/FAILURE-HANDOFF-20260812.md`.

ComfyUI Workflows browser: `guideants/` (templates) and `guideants-jobs/` (submitted graphs).
