# Talking-head skills

Experimental GuideAnts skills that reach InfiniteTalk **i2v** talking-head
workflows on the GPU host **without GuideAntsApi / ServiceModes changes**.

**Default deployment is a PC sandbox talking to the GPU host** over the talking-head skill
gateway — a token-gated transparent reverse proxy to the video adapter inside the
comfyui-video container. Do not call `127.0.0.1:8189` or `:8190` from a PC sandbox.

Deliverables are written to the sandbox CWD as **MP4** files (avatar + audio + background →
composited delivery) with bare filenames — never prefix with `Output/`. V2V is not a skill surface.

## Required Environment (PC → the GPU host)

```text
TALKING_HEAD_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8189/talking-head-skill
TALKING_HEAD_SKILL_TOKEN=<same as the GPU host GA_TALKING_HEAD_SKILL_TOKEN>
```

On the GPU host `.env` set `GA_TALKING_HEAD_SKILL_TOKEN`. The comfyui-video stack publishes
port `8189` on `0.0.0.0` for LAN access.

If the vars are missing in the guide Environment, **stop and ask the user** to configure
them. Never scan the network or guess the GPU host's LAN IP — the operator supplies the URL.

### Gateway path map

| Path | Meaning |
|------|---------|
| `{BASE}/v1/capabilities` | Adapter readiness (`ready`, `composite_ready`, …) |
| `{BASE}/v1/talking-head/jobs` | Submit i2v job (multipart: source, audio, background) |
| `{BASE}/v1/talking-head/jobs/{id}` | Job status |
| `{BASE}/v1/talking-head/jobs/{id}/cancel` | Cancel job |
| `{BASE}/v1/talking-head/jobs/{id}/result` | Download MP4 |
| `{BASE}/files` | Stage upload on the GPU host (optional) |

Auth header: `X-Talking-Head-Skill-Token`.

## Skills (v1, i2v only)

| Skill | What it does |
|-------|----------------|
| [`talking-head`](talking-head/) | Umbrella probe + routing |
| [`talking-head-i2v`](talking-head-i2v/) | Avatar + audio + background → MP4 (`infinitetalk-i2v-v1`) |

No V2V skill. Do not submit video-driver jobs from this pack.

## Common rules

- Run preflight/probe first; trust it over these docs.
- Paths must stay inside the notebook (bare filenames in CWD, `uploads/…`, `../…` for inputs outside CWD).
- Jobs are long-running; default poll budget is **3600s**.
- Seed `-1` means the CLI picks a random int **before** submit; always log the resolved seed.
- Quiet poll telemetry: log state/progress key changes + seed, plus a 60s heartbeat — not every transport poll.
- Report honestly what worked and what was blocked with preflight evidence.

## Limits

- Single-host job queue; InfiniteTalk + CorridorKey composite is heavy.
- Background plate is required for i2v delivery.
- Do not invent Comfy graphs or call ComfyUI `/prompt` directly from skills.
