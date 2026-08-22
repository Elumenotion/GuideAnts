---
name: audiocpp-deferred-tts
description: "Run TTS families audio.cpp supports but GuideAnts does not ship (Qwen3 CustomVoice, VibeVoice, MioTTS, VoxCPM2, PocketTTS, Vevo2) by downloading onto Max and spawning a private engine via the raw audiocpp gateway (/admin + /private)."
metadata:
  guideants:
    enabled: true
    display_order: 35
    requires_toolsets: [sandbox]
---

# audio.cpp deferred TTS families (experimental)

Product TTS is catalog-gated. Max still ships loaders for deferred families.
This skill downloads models onto Max and spawns a private engine through the
**raw audiocpp gateway** (`/admin/models/fetch`, `/admin/private/*`, then
`/private/v1/audio/speech`). WAVs land in `Output/`; these models never appear in
the GuideAnts voice picker.

## Environment (required for PC → Max)

```text
AUDIOCPP_SKILL_BASE_URL=http://<max-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as Max GA_AUDIOCPP_SKILL_TOKEN>
```

Optional: `HF_TOKEN` for gated repos.

## Consent rule

Families that take `voice_ref`: only with speaker consent.

## Preflight

```bash
python3 Output/Skills/audiocpp-deferred-tts/scripts/preflight.py --for deferred-tts
```

VRAM: a second engine competes with Max’s loaded TTS — ask the user to unload
product TTS via Settings if needed. Never unload silently.

## Pattern

Dest paths are rewritten under `/models-local/skill/` on Max when the gateway is
set. `--engine-url http://127.0.0.1:18099` selects the gateway private engine
(requests still go to `AUDIOCPP_SKILL_BASE_URL`):

```bash
python3 Output/Skills/audiocpp-deferred-tts/scripts/fetch_model.py <hf-repo> \
  --dest /models-local/tts/<DirName> [--include <prefix>]

python3 Output/Skills/audiocpp-deferred-tts/scripts/spawn_engine.py start \
  --path /models-local/tts/<DirName> --family <family> --task tts
python3 Output/Skills/audiocpp-deferred-tts/scripts/spawn_engine.py status

python3 Output/Skills/audiocpp-deferred-tts/scripts/engine_tool.py speech "Hi" \
  --engine-url http://127.0.0.1:18099 --model <id> [--voice <speaker>] -o Output/hi.wav

python3 Output/Skills/audiocpp-deferred-tts/scripts/spawn_engine.py stop
```

Per-family recipes: `references/deferred-models.md`. Start with **Qwen3
CustomVoice**. Engine schema: `references/engine-api.md`.

## Ground rules

- Script budget ~5 minutes; poll `status`; `fetch_model.py` resumes.
- Always `stop` the private engine when done.
- Surface engine error text for unsupported options.

## Not possible on Max’s binary

`kokoro_tts` / `parakeet_tdt` need a custom host build → **audiocpp-host-tts**.

## Reporting

End by saying which steps worked and what was blocked, quoting preflight evidence.
