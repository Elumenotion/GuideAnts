# Default parameters

Adapter defaults (`IMAGE_DEFAULT_PARAMETERS` / workflow overrides). Start here;
tune only when preflight is green and the user asks for quality experiments.

## Edit / inpaint BF16 (`qwen-image-edit-bf16-v1`, inpaint variant)

| Parameter | Default |
|-----------|---------|
| steps | 4 |
| cfg | 1 |
| denoise | 1 |
| shift | 3.1 |
| megapixels | 1.6 |
| lora_strength | 1 |
| seed | 0 |

## Generate BF16 (`qwen-image-bf16-v1`)

| Parameter | Default | Notes |
|-----------|---------|-------|
| width / height | 1328 × 1328 | multiples of 8 |
| steps | 4 | Lightning |
| cfg | 1 | Lightning |
| lora_strength | 1 | Lightning on |
| shift | 3.1 | |
| megapixels | 1.6 | edit-only in adapter; ignored for generate canvas |
| seed | 0 | |

### Full-quality generate experiment (optional)

| Parameter | Value |
|-----------|-------|
| steps | 20 |
| cfg | 4 |
| lora_strength | 0 |

Artifact harnesses sometimes use higher `cfg` / `megapixels` for edit quality
experiments (e.g. overlay bf16 `cfg=4`, `megapixels=2.0`). Document those as
proven knobs, not invented adapter parameters.
