---
name: qwen-image-generate
description: "BF16 text-to-image via Max ComfyUI-video adapter (qwen-image-bf16-v1). Use when the user needs a new PNG from a prompt with Qwen Image 2512 — not product SD txt2img."
metadata:
  guideants:
    enabled: true
    display_order: 41
    requires_toolsets: [sandbox]
---

# Qwen Image generate (BF16)

Text → PNG using workflow `qwen-image-bf16-v1`. FP8 generate is not available.

## When to use product SD tools instead

Plain notebook txt2img without Qwen/Comfy → use built-in SD tools.

## Max gateway (192.168.0.111)

```bash
export QWEN_IMAGE_SKILL_BASE_URL=http://192.168.0.111:8189/qwen-image-skill
export QWEN_IMAGE_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
```

## Sampler profile (default = tested Lightning)

Default and AC-G1 path: `steps=4 cfg=1 lora_strength=1`. Use that unless the user
**explicitly** asks for a full-quality experiment (`steps=20 cfg=4 lora_strength=0`).

## Preflight

```bash
export QWEN_IMAGE_SKILL_BASE_URL=http://192.168.0.111:8189/qwen-image-skill
export QWEN_IMAGE_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
python3 Output/Skills/qwen-image-generate/scripts/preflight.py --for generate-bf16
```

Requires `image_generate_bf16_ready`.

## Generate

```bash
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py generate \
  "prompt text…" -o Output/gen.png
```

In ComfyUI: Workflows → `guideants/` or `guideants-jobs/`.

## Job control

```bash
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py status <job_id>
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py cancel <job_id>
python3 Output/Skills/qwen-image-generate/scripts/image_tool.py result <job_id> -o Output/out.png
```

First load can exceed 30 minutes; poll budget defaults to 1800s.

## Reporting

State output path, job id, readiness flags used, and preflight evidence if blocked.
