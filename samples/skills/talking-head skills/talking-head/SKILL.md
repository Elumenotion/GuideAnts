---
name: talking-head
description: "Probe Max InfiniteTalk talking-head readiness via the skill gateway and route to talking-head-i2v. Use when the user needs avatar+audio+background → MP4, or when readiness is unclear."
metadata:
  guideants:
    enabled: true
    display_order: 50
    requires_toolsets: [sandbox]
---

# Talking-head (umbrella)

Product notebook tools do not cover InfiniteTalk talking-head delivery. Use this
family when the user needs **avatar + audio + background → composited MP4** via
the ComfyUI-video adapter on Max (`infinitetalk-i2v-v1`).

**PC sandbox → Max gateway.** Skills run on the workstation; the comfyui-video
container runs on Max (`guideants-video-stack`, LAN `192.168.0.111`, port `8189`).
Do **not** use `127.0.0.1` — that is the PC, not Max.

V2V is not exposed by this pack.

## Max gateway (192.168.0.111)

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://192.168.0.111:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
```

Max stack: `GA_COMFYUI_VIDEO_PORT=8189`, `GA_TALKING_HEAD_SKILL_TOKEN` in
`guideants-video-stack/.env`. Header: `X-Talking-Head-Skill-Token`.

## Probe first

```bash
export TALKING_HEAD_SKILL_BASE_URL=http://192.168.0.111:8189/talking-head-skill
export TALKING_HEAD_SKILL_TOKEN=7c4e91a2b8d03f5e6a1c9d0e2f4b6a8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a2
python3 Output/Skills/talking-head/scripts/probe.py
python3 Output/Skills/talking-head/scripts/preflight.py --for probe
```

## Route to a task skill

| User wants… | Skill |
|-------------|-------|
| Avatar + audio + background → MP4 | `talking-head-i2v` |
| Unclear / “is Max ready?” | stay here; run probe |

## Reporting

Quote probe/preflight evidence when blocked. Deliverables are MP4s under `Output/`.

Honest limits: cold InfiniteTalk load and CorridorKey composite can take many minutes;
single-host queue; no UI picker; no V2V skill.

See `references/parameters.md` and `references/workflows.md`.

ComfyUI Workflows browser: `guideants/` (templates) and `guideants-jobs/` (submitted graphs).
