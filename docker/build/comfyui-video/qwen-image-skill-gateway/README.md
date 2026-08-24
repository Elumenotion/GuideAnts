# Qwen Image skill gateway

Token-gated transparent reverse proxy from PC sandboxes to the ComfyUI-video
adapter inside the comfyui-video container.

## Nginx prefix

`/qwen-image-skill/` on the published comfyui-video port (default LAN `:8189`).

## Auth

`X-Qwen-Image-Skill-Token` must match `GA_QWEN_IMAGE_SKILL_TOKEN`.

## PC sandbox env

```text
QWEN_IMAGE_SKILL_BASE_URL=http://<max-lan-ip>:8189/qwen-image-skill
QWEN_IMAGE_SKILL_TOKEN=<same as Max GA_QWEN_IMAGE_SKILL_TOKEN>
```

## Upstream

Loopback adapter at `127.0.0.1:8190` (same target as nginx `/video/`).

## Gateway-owned routes

| Route | Purpose |
|-------|---------|
| `GET /health` | Gateway + adapter probe |
| `POST /files` | Stage notebook uploads on Max |

All `/v1/*` adapter paths are proxied transparently.
