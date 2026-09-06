# Talking-head skill

PC sandbox → Max talking-head gateway. **One skill:** [`talking-head`](talking-head/).
Deliverable is a **1280×720 MP4** from **one** `video_tool.py i2v` job
(LongCat-Video-Avatar-1.5 416×256 → CorridorKey @ 416×234 → BasicVSR++ FG → 720p).
Workflow id remains `infinitetalk-i2v-v1`. V2V is
not a skill.

## Environment (guide Environment only)

```text
TALKING_HEAD_SKILL_BASE_URL=http://<max-lan-ip>:8189/talking-head-skill
TALKING_HEAD_SKILL_TOKEN=<same as Max GA_TALKING_HEAD_SKILL_TOKEN>
```

Do not hardcode these in skill commands. If missing, stop and ask the user. Never
scan the LAN.

Auth header: `X-Talking-Head-Skill-Token`.

| Path | Meaning |
|------|---------|
| `{BASE}/v1/capabilities` | Probe (`ready`, `composite_ready`, `fg_upscaler`, canvas) |
| `{BASE}/v1/talking-head/jobs` | Submit (only via `video_tool.py`) |
| `{BASE}/v1/talking-head/jobs/{id}` | Status |
| `{BASE}/v1/talking-head/jobs/{id}/cancel` | Cancel (user-asked only) |
| `{BASE}/v1/talking-head/jobs/{id}/result` | Download MP4 |

CWD is the notebook output directory. Commands in SKILL.md are literal
(`Skills/talking-head/scripts/…`, `-o talking-head.mp4`).

## Rules

- Preflight before submit; trust blockers over this README.
- Tested sampler: CLI defaults only (416×256, 8, cfg 1, 25 fps, `seed=-1`).
- `parameters` is one JSON form field inside `video_tool.py`. Do not POST by hand.
- `i2v` submits and exits. Poll `status` on later sandbox calls. Do not wait in one script.
- Do not call ComfyUI `/free`. Do not stack jobs. Do not cancel CK frame 0.
- Tested 10.56s InfiniteTalk 4-step clips already ran 1626–3341s. LongCat 1.5
  8-step sampling will be longer. Wall time scales with audio-derived frames.
