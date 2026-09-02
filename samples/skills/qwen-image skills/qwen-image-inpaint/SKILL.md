---
name: qwen-image-inpaint
description: "BF16 inpaint via GPU host ComfyUI-video adapter (qwen-image-edit-bf16-inpaint-v1). Use when the user has source + mask PNGs and a fill prompt — masked completion, whiteboard erase, etc."
metadata:
  guideants:
    enabled: true
    display_order: 43
    requires_toolsets: [sandbox]
---

# Qwen Image inpaint (BF16)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/qwen-image-inpaint/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o inpaint.png`); never prefix with `Output/`.

Source + mask + prompt → PNG. Mask: **white = editable**, **black = preserve**.

Workflow (required): `qwen-image-edit-bf16-inpaint-v1`

## GPU host gateway (env-configured)

The scripts read `QWEN_IMAGE_SKILL_BASE_URL` and `QWEN_IMAGE_SKILL_TOKEN` from the
guide Environment automatically — do **not** hardcode or export them inline.

Verify before running:

```bash
printenv QWEN_IMAGE_SKILL_BASE_URL >/dev/null && printenv QWEN_IMAGE_SKILL_TOKEN >/dev/null && echo "env ok" || echo "env missing"
```

If either is missing, stop and ask the user to set it in the guide's Environment
variables. Never scan the LAN or guess the GPU host's IP.


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
python3 Skills/qwen-image-inpaint/scripts/preflight.py --for inpaint-bf16
```

Requires `image_edit_bf16_inpaint_ready`.

## Inpaint

```bash
python3 Skills/qwen-image-inpaint/scripts/image_tool.py inpaint \
  uploads/source.png uploads/mask.png "prompt…" -o inpaint.png
```

In ComfyUI: Workflows → `guideants/` (templates) or `guideants-jobs/` (exact submitted graph for this job).

## Job control

Same as edit skill (`status`, `cancel`, `result` on `image_tool.py`). Status includes
`comfy_workflow_file` when published.

## Reporting

Mask is required. Quote preflight evidence when blocked.
