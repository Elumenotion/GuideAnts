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

Use this family when the user needs **avatar + audio + background → composited MP4**
via the ComfyUI-video adapter on Max (`infinitetalk-i2v-v1`).

**Default path: PC sandbox → Max talking-head gateway.** Transparent reverse proxy
to the adapter (`/v1/capabilities`, `/v1/talking-head/*`, `/files`). Scripts use
`TALKING_HEAD_SKILL_BASE_URL` when set. Do not call `127.0.0.1:8189` or `:8190`
from a PC sandbox — those ports are inside Max.

V2V is not exposed by this pack.

## Environment (required for PC → Max)

```text
TALKING_HEAD_SKILL_BASE_URL=http://<max-lan-ip>:8189/talking-head-skill
TALKING_HEAD_SKILL_TOKEN=<same as Max GA_TALKING_HEAD_SKILL_TOKEN>
```

## Probe first

```bash
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

Honest limits: cold InfiniteTalk + CorridorKey composite can take many minutes;
single-host queue; no V2V skill.
