# Workflows (BF16 v1)

| Workflow id | Skill | Use |
|-------------|-------|-----|
| `qwen-image-bf16-v1` | `qwen-image-generate` | Text → PNG |
| `qwen-image-edit-bf16-v1` | `qwen-image-edit` | Restyle / complete scene |
| `qwen-image-edit-bf16-inpaint-v1` | `qwen-image-inpaint` | Masked fill / backgrounds |

All workflows use BF16 UNet weights mounted by compose — skills must not mutate
workflow JSON on disk.

## Seeing them in ComfyUI

On Max, open ComfyUI and use **Workflows**:

| Folder | Contents |
|--------|----------|
| `guideants/` | Template API graphs (same files the adapter loads) |
| `guideants-jobs/` | Exact rendered graph for each submitted job (`{workflow}__{jobId}.json`) plus `_RUNNING__{workflow}.json` |

Load a `guideants-jobs/*.json` file (API format) to inspect the graph that is / was running.

Legacy FP8 generate (`qwen-image-v1`) is retired from the active adapter surface.
