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

## Max gateway (env-configured)

The scripts read `QWEN_IMAGE_SKILL_BASE_URL` and `QWEN_IMAGE_SKILL_TOKEN` from the
guide Environment automatically — do **not** hardcode or export them inline.

Verify before running:

```bash
printenv QWEN_IMAGE_SKILL_BASE_URL >/dev/null && printenv QWEN_IMAGE_SKILL_TOKEN >/dev/null && echo "env ok" || echo "env missing"
```

If either is missing, stop and ask the user to set it in the guide's Environment
variables. Never scan the LAN or guess Max's IP.


## Sampler profile (locked — tested Lightning)

Always use the CLI defaults. Do **not** pass `--steps`, `--cfg`, or `--lora-strength`.
`image_tool.py` forces steps=4, cfg=1, lora_strength=1 (same as BF16 overlay acceptance).

## Preflight

```bash
python3 Output/Skills/qwen-image-edit/scripts/preflight.py --for edit-bf16
```

Requires `image_edit_bf16_ready`.

## Edit

```bash
python3 Output/Skills/qwen-image-edit/scripts/image_tool.py edit \
  Output/uploads/source.png "prompt…" -o Output/edit.png
```

Source paths must stay under the notebook (e.g. `Output/uploads/…`).

In ComfyUI: Workflows → `guideants/` or `guideants-jobs/` for the submitted graph.

## Job control

```bash
python3 Output/Skills/qwen-image-edit/scripts/image_tool.py status <job_id>
python3 Output/Skills/qwen-image-edit/scripts/image_tool.py result <job_id> -o Output/out.png
```

## Reporting

Quote preflight if blocked. Output always under `Output/`.
