---
name: qwen-image-edit
description: "BF16 image edit via Max ComfyUI-video adapter (qwen-image-edit-bf16-v1). Use when the user has a source PNG and a prompt to restyle or complete the scene — not product SD img2img."
metadata:
  guideants:
    enabled: true
    display_order: 42
    requires_toolsets: [sandbox]
---

# Qwen Image edit (BF16)

Image + prompt → PNG using `qwen-image-edit-bf16-v1`.

## When to use product SD tools instead

Simple img2img without Qwen edit graphs → built-in SD tools.

## Environment (required for PC → Max)

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<max-lan-ip>:8189/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as Max GA_QWEN_IMAGE_SKILL_TOKEN>
```

## Preflight

```bash
python3 Output/Skills/qwen-image-edit/scripts/preflight.py --for edit-bf16
```

Requires `image_edit_bf16_ready`.

## Edit

```bash
python3 Output/Skills/qwen-image-edit/scripts/image_tool.py edit \
  Output/uploads/source.png "prompt…" -o Output/edit.png \
  [--workflow qwen-image-edit-bf16-v1] [--megapixels 1.6] [--steps 4 --cfg 1]
```

Source paths must stay under the notebook (e.g. `Output/uploads/…`).

## Job control

```bash
python3 Output/Skills/qwen-image-edit/scripts/image_tool.py status <job_id>
python3 Output/Skills/qwen-image-edit/scripts/image_tool.py result <job_id> -o Output/out.png
```

## Reporting

Quote preflight if blocked. Output always under `Output/`.
