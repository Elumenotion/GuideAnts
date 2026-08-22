# Audiocpp raw gateway (sandbox side-channel)

Not part of GuideAntsApi or ServiceModes. Remote clients call this host over the
AI gateway (`/audiocpp-skill/`) with `X-Audiocpp-Skill-Token` matching
`GA_AUDIOCPP_SKILL_TOKEN`.

Transparent reverse proxy to full `audiocpp_server`:

- `/asr/*` → ASR engine
- `/tts/*` → TTS engine
- `/private/*` → skill-spawned private engine
- `/files` → stage uploads for path-based engine JSON
- `/admin/*` → model fetch + private start/stop

See `samples/skills/audiocpp skills/README.md` for PC env wiring.

## Private multi-model (Gate M)

`POST /admin/private/start` accepts either legacy single-model fields
(`path` + `family` + `task`) or an additional `models: [{path,family,task,model_id,options}, ...]`.
When both are present, `path`/`family` is the first model and `models` are appended.

`spawn_engine.py start ... --extra path=...;family=...;task=...` sends the
`models` array. audio.cpp co-loads them on one private port.

VRAM note: co-loading ForcedAligner + Sortformer with product ASR/TTS can OOM
the ROCm host — unload product engines or use sequential private spawns when
needed. Multi-model config itself is proven; coexistence with product ASR is a
capacity concern, not an API gap.
