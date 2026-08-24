# Workflows (BF16 v1)

| Workflow id | Skill | Use |
|-------------|-------|-----|
| `qwen-image-bf16-v1` | `qwen-image-generate` | Text → PNG |
| `qwen-image-edit-bf16-v1` | `qwen-image-edit` | Restyle / complete scene |
| `qwen-image-edit-bf16-inpaint-v1` | `qwen-image-inpaint` | Masked fill |

All workflows use BF16 UNet weights mounted by compose — skills must not mutate
workflow JSON on disk.

Legacy FP8 generate (`qwen-image-v1`) is retired from the active adapter surface.

Optional later (not v1): BF16 20-step edit variant if still useful after Lightning defaults.
