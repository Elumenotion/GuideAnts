# Workflows (live GPU host adapter)

| Workflow id (API) | Skill | Use |
|-------------------|-------|-----|
| `qwen-image-v1` | `qwen-image-generate` | Text → PNG |
| `qwen-image-edit-bf16-v1` | `qwen-image-edit` | Restyle / complete scene |
| `qwen-image-edit-bf16-inpaint-v1` | `qwen-image-inpaint` | Masked fill / backgrounds |

Generate API id is `qwen-image-v1`. Compose points that path at BF16 UNet +
Lightning LoRA (`precision: bfloat16` on `/v1/capabilities`). Do not send
`qwen-image-bf16-v1` as `workflow_version` — The GPU host rejects it.

Skills must not mutate workflow JSON on disk.

## Seeing them in ComfyUI

On the GPU host, open ComfyUI and use **Workflows**:

| Folder | Contents |
|--------|----------|
| `guideants/` | Template graphs (UI format; placeholders like `{{PROMPT}}`) |
| `guideants-jobs/` | Exact **rendered** graph for each submitted job (`{workflow}__{jobId}.json`) plus `_RUNNING__{workflow}.json` |

Load a `guideants-jobs/*.json` file (UI format) to inspect the graph that is / was running — including the real prompt text and uploaded input filenames.

API-only `/history/{prompt_id}` also retains the graph under `prompt[2]`; skill jobs additionally attach UI metadata via `extra_pnginfo` so queue history / PNG drag-drop can reload the canvas.
