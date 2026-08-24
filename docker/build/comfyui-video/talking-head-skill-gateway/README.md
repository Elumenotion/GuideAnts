# Talking-head skill gateway

Token-gated transparent reverse proxy from PC sandboxes to the ComfyUI-video
adapter inside the comfyui-video container (talking-head i2v jobs).

## Nginx prefix

`/talking-head-skill/` on the published comfyui-video port (default LAN `:8189`).

## Auth

`X-Talking-Head-Skill-Token` must match `GA_TALKING_HEAD_SKILL_TOKEN`.

## PC sandbox env

```text
TALKING_HEAD_SKILL_BASE_URL=http://<max-lan-ip>:8189/talking-head-skill
TALKING_HEAD_SKILL_TOKEN=<same as Max GA_TALKING_HEAD_SKILL_TOKEN>
```

## Upstream

Loopback adapter at `127.0.0.1:8190` (same target as nginx `/video/`).

## Listen

Default `127.0.0.1:8098` (`GA_TALKING_HEAD_SKILL_HOST` / `GA_TALKING_HEAD_SKILL_PORT`).
Log level warning; `access_log=False`.

## Gateway-owned routes

| Route | Purpose |
|-------|---------|
| `GET /health` | Gateway + adapter probe |
| `POST /files` | Stage notebook uploads on Max |

All `/v1/*` adapter paths are proxied transparently (including `/v1/talking-head/*`).
