---
name: talking-head
description: "Probe the GPU host InfiniteTalk talking-head readiness via the skill gateway and route to talking-head-i2v. Use when the user needs avatar+audio+background → MP4, or when readiness is unclear."
metadata:
  guideants:
    enabled: true
    display_order: 50
    requires_toolsets: [sandbox]
---

# Talking-head (umbrella)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/talking-head/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o clip.mp4`); never prefix with `Output/`.

Product notebook tools do not cover InfiniteTalk talking-head delivery. Use this
family when the user needs **avatar + audio + background → composited MP4** via
the ComfyUI-video adapter on the GPU host (`infinitetalk-i2v-v1`).

**PC sandbox → GPU host gateway.** Skills run on the workstation; the comfyui-video
container runs on the GPU host (`guideants-video-stack`, port `8189`).
Do **not** use `127.0.0.1` — that is the PC, not the GPU host.

V2V is not exposed by this pack.

## GPU host gateway (env-configured)

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=<token: GA_TALKING_HEAD_SKILL_TOKEN from the GPU host stack .env>
```

GPU host stack: `GA_COMFYUI_VIDEO_PORT=8189`, `GA_TALKING_HEAD_SKILL_TOKEN` in
`guideants-video-stack/.env`. Header: `X-Talking-Head-Skill-Token`.

## Probe first

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://<gpu-host-lan-ip>:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=<token: GA_TALKING_HEAD_SKILL_TOKEN from the GPU host stack .env>
python3 Skills/talking-head/scripts/probe.py
python3 Skills/talking-head/scripts/preflight.py --for probe
```

## Route to a task skill

| User wants… | Skill |
|-------------|-------|
| Avatar + audio + background → MP4 | `talking-head-i2v` |
| Unclear / “is the GPU host ready?” | stay here; run probe |

## Reporting

Quote probe/preflight evidence when blocked. Deliverables are MP4s written to the CWD (bare filenames).

Honest limits: cold InfiniteTalk load and CorridorKey composite can take many minutes;
single-host queue; no UI picker; no V2V skill.

See `references/parameters.md` and `references/workflows.md`.

ComfyUI Workflows browser: `guideants/` (templates) and `guideants-jobs/` (submitted graphs).
