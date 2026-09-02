# Adapter API (skill-facing)

Skills call the **video adapter** through the GPU host skill gateway — not ComfyUI directly.

## Deployment (PC sandbox → the GPU host)

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8189/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as the GPU host GA_QWEN_IMAGE_SKILL_TOKEN>
```

The operator sets these in the guide **Environment variables**. If missing, ask the user —
do **not** scan the LAN, ping hosts, or guess the GPU host's IP. Scripts use this automatically when
`QWEN_IMAGE_SKILL_BASE_URL` is set.

## Base URL

`QWEN_IMAGE_SKILL_BASE_URL` — full gateway prefix including `/qwen-image-skill`.

## Endpoints

| Action | Method / path |
|--------|----------------|
| Capabilities | `GET /v1/capabilities` |
| Edit / inpaint | `POST /v1/image/jobs` (multipart: `source`, optional `mask`, fields) |
| Generate | `POST /v1/image/generate/jobs` |
| Status | `GET /v1/image/jobs/{id}` |
| Cancel | `POST /v1/image/jobs/{id}/cancel` |
| Result | `GET /v1/image/jobs/{id}/result` |
| Stage file | `POST /files` |

## Readiness flags (live GPU host)

| Flag | Workflow API id |
|------|-----------------|
| `image_generate_ready` | `qwen-image-v1` (BF16 weights; see `precision`) |
| `image_edit_bf16_ready` | `qwen-image-edit-bf16-v1` |
| `image_edit_bf16_inpaint_ready` | `qwen-image-edit-bf16-inpaint-v1` |

When a flag is false, inspect adapter `missing` details — do not guess weights from disk.
Do not require `image_generate_bf16_ready`; that flag is not advertised on this gateway.

## Auth

Every request requires header `X-Qwen-Image-Skill-Token` matching the GPU host
`GA_QWEN_IMAGE_SKILL_TOKEN`.
