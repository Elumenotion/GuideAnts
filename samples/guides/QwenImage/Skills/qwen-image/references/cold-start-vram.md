# Cold start and VRAM coexistence

## Readiness vs VRAM load

Adapter `image_*_ready` flags mean:

- workflow JSON is mounted
- bundle weight files exist on `/models` with expected size
- ComfyUI reports a GPU and required node types

They do **not** mean weights are already in VRAM. The first job for a workflow
family loads UNet/VAE/CLIP into GPU memory. That cold load can take **many minutes**
(BF16 generate UNet is ~38 GB on disk).

Skills default poll budget: **1800s**. For first BF16 generate on a fresh container,
increase `--timeout` on `image_tool.py` or re-run `status` / `result` after cold load.

## InfiniteTalk on the same host

ComfyUI-video shares one GPU queue (`VIDEO_MAX_CONCURRENT_JOBS=1`). InfiniteTalk
and Qwen Image jobs do not run in parallel on the same adapter instance.

If InfiniteTalk weights are resident, a Qwen Image job may trigger a heavy reload.
Run image jobs **sequentially**; do not assume both graphs stay hot simultaneously.

## Do not invent graphs

If edit/generate fails after preflight is green, read
`artifacts/qwen-image-edit/FAILURE-HANDOFF-20260812.md`. Do not substitute Diffusers,
mutate workflow JSON on disk, or guess weights from disk inventory.
