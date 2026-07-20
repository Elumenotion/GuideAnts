---
name: audiocpp-deferred-tts
description: "Run TTS model families audio.cpp supports but GuideAnts does not ship — Qwen3 CustomVoice builtin speakers, VibeVoice multi-speaker dialogue, MioTTS, VoxCPM2, PocketTTS, Vevo2 voice conversion — by downloading the model and spawning a private audiocpp_server in the sandbox. Use when the user names one of these models or asks for a TTS capability the loaded catalog model rejects."
metadata:
  guideants:
    enabled: true
    display_order: 35
    requires_toolsets: [sandbox]
---

# audio.cpp deferred TTS families (experimental)

The GuideAnts TTS wrapper is strictly catalog-gated, but the container ships
the full `audiocpp_server` binary with every *released* loader — including
families GuideAnts deferred for packaging/UI reasons, not engine reasons. The
blockers are download/conversion chores a script can do by hand. Deliverables
are WAV files in `Output/`; these models never appear in the GuideAnts voice
picker or the live voice path.

## Consent rule

Several of these families clone from reference audio (`voice_ref`). That is a
supported use **when the speaker consents** (the user's own voice, or stated
permission). Decline only imitation of a third party without consent.

## Preflight

```bash
python3 Output/Skills/audiocpp-deferred-tts/scripts/preflight.py --for deferred-tts
```

Trust its verdict over this document. Heed its VRAM warning: a second engine
competes with the wrapper's loaded models — prefer asking the user to unload
the GuideAnts TTS model first (Settings, or
`curl -X POST http://127.0.0.1:8084/admin/unload`). Ask, never do it silently.

## The pattern

```bash
python3 Output/Skills/audiocpp-deferred-tts/scripts/fetch_model.py <hf-repo> --dest <dir> [--include <prefix>]
# any per-family conversion step — see references/deferred-models.md
python3 Output/Skills/audiocpp-deferred-tts/scripts/spawn_engine.py start --path <dir> --family <family> --task tts
python3 Output/Skills/audiocpp-deferred-tts/scripts/spawn_engine.py status   # poll until ready
python3 Output/Skills/audiocpp-deferred-tts/scripts/engine_tool.py speech "Hi" \
  --engine-url http://127.0.0.1:18099 --model <id> [--voice <speaker>] -o Output/hi.wav
python3 Output/Skills/audiocpp-deferred-tts/scripts/spawn_engine.py stop     # always stop when done
```

Per-family recipes (repos, file scoping, conversions, known speaker ids) are in
`references/deferred-models.md` — start with **Qwen3 CustomVoice**, it has the
fewest new variables. Engine endpoints, config schema, and task tokens are in
`references/engine-api.md`.

## Ground rules

- **Script budget is ~5 minutes per call.** `spawn_engine.py start` detaches
  and returns; poll `status` in a follow-up call instead of blocking. Big
  downloads resume across `fetch_model.py` calls.
- **Always `stop` the private engine** when the task is done or the user moves
  on.
- If the detached engine does not survive between script calls (preflight
  reports this), the flow is limited to synth requests issued in the same
  script call that spawned the engine — workable for small models only.
- The engine errors loudly on unsupported options; surface that text.

## Not possible with the container binary (don't burn time)

`kokoro_tts` and `parakeet_tdt` loaders are not in release builds, and some
families are downloader-only upstream — no download fixes a missing loader.
Those need the user's own custom build: see the **audiocpp-host-tts** skill.

## Reporting

This skill exists to answer "is it possible?". End by telling the user which
steps worked and what was blocked and why, quoting preflight/engine evidence,
so the experiment log stays honest.
