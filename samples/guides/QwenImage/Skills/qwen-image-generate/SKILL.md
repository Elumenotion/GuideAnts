---
name: qwen-image-generate
description: "BF16 text-to-image via Max ComfyUI-video adapter (qwen-image-v1). Use when the user needs a new PNG from a prompt with Qwen Image 2512 — not product SD txt2img."
metadata:
  guideants:
    enabled: true
    display_order: 41
    requires_toolsets: [sandbox]
---

# Qwen Image generate (BF16)

Text → PNG using API workflow `qwen-image-v1` (BF16 UNet + Lightning LoRA on Max).
Capabilities flag: `image_generate_ready` (also `precision: bfloat16`).

## When to use product SD tools instead

Plain notebook txt2img without Qwen/Comfy → use built-in SD tools.

## Environment (required for PC → Max)

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<max-lan-ip>:8189/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as Max GA_QWEN_IMAGE_SKILL_TOKEN>
```

## Sampler profile (default = tested Lightning)

Default and AC-G1 path: `steps=4 cfg=1 lora_strength=1`, canvas `1328×1328`. Use that
unless the user **explicitly** asks for a full-quality experiment
(`steps=20 cfg=4 lora_strength=0`).

## Preflight

```bash
python3 Output/Skills/qwen-image-generate/scripts/preflight.py --for generate
```

Requires `image_generate_ready`. Do not wait for `image_generate_bf16_ready` — Max does
not advertise that flag.

## Generate

```bash
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py generate \
  "prompt text…" -o Output/gen.png \
  [--width 1328 --height 1328] [--steps 4 --cfg 1] [--seed 42]
```

## Job control

```bash
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py status <job_id>
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py cancel <job_id>
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py result <job_id> -o Output/out.png
```

First load can exceed 30 minutes; poll budget defaults to 1800s.

## Reporting

State output path, job id, readiness flags used, and preflight evidence if blocked.
