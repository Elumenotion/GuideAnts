# Default parameters

Adapter defaults (`IMAGE_DEFAULT_PARAMETERS` / workflow overrides). Skills must use
these tested profiles. Do not invent sampler settings.

## Edit / inpaint BF16 (locked Lightning)

Workflows: `qwen-image-edit-bf16-v1`, `qwen-image-edit-bf16-inpaint-v1`

| Parameter | Value | Notes |
|-----------|-------|-------|
| steps | 4 | mandatory |
| cfg | 1 | mandatory |
| denoise | 1 | mandatory |
| shift | 3.1 | mandatory |
| megapixels | 1.6 | mandatory |
| lora_strength | 1 | Lightning LoRA on |
| seed | 0 | or caller seed |

`image_tool.py` edit/inpaint ignore CLI sampler overrides and always send this profile
(whiteboard / background AC-I1).

## Generate (`qwen-image-v1`, BF16 weights)

| Parameter | Default | Notes |
|-----------|---------|-------|
| width / height | 1328 × 1328 | multiples of 8 |
| steps | 4 | Lightning (AC-G1) |
| cfg | 1 | Lightning |
| lora_strength | 1 | Lightning on |
| shift | 3.1 | |
| seed | 0 | |

### Full-quality generate (only if user asks)

| Parameter | Value |
|-----------|-------|
| steps | 20 |
| cfg | 4 |
| lora_strength | 0 |

Never apply this profile to edit/inpaint.
