---
name: qwen-image-inpaint
description: "BF16 inpaint via Max ComfyUI-video adapter (qwen-image-edit-bf16-inpaint-v1). Use when the user has source + mask PNGs and a fill prompt — masked completion, whiteboard erase, etc."
metadata:
  guideants:
    enabled: true
    display_order: 43
    requires_toolsets: [sandbox]
---

# Qwen Image inpaint (BF16)

Source + mask + prompt → PNG. Mask convention: **white = editable**, **black = preserve**.

Workflow (required): `qwen-image-edit-bf16-inpaint-v1`

## Environment (required for PC → Max)

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<max-lan-ip>:8189/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as Max GA_QWEN_IMAGE_SKILL_TOKEN>
```

## Sampler profile (locked — tested Lightning)

Always use the CLI defaults. Do **not** pass `--steps`, `--cfg`, or `--lora-strength`.
`image_tool.py` forces:

| Parameter | Value |
|-----------|-------|
| steps | 4 |
| cfg | 1 |
| lora_strength | 1 |
| denoise | 1 |
| shift | 3.1 |
| megapixels | 1.6 |

This is the whiteboard / background profile that passed AC-I1. Never invent `steps=20`.

## Preflight

```bash
python3 Output/Skills/qwen-image-inpaint/scripts/preflight.py --for inpaint-bf16
```

Requires `image_edit_bf16_inpaint_ready`.

## Inpaint

```bash
python3 Output/Skills/qwen-image-inpaint/scripts/image_tool.py inpaint \
  Output/uploads/source.png Output/uploads/mask.png "prompt…" -o Output/inpaint.png
```

## Job control

Same as edit skill (`status`, `cancel`, `result` on `image_tool.py`).

## Reporting

Mask is required. Quote preflight evidence when blocked.
