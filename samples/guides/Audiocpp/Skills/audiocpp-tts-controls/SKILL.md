---
name: audiocpp-tts-controls
description: "Advanced synthesis on the loaded Max TTS model via the raw audiocpp gateway (/tts): seed, language, voice-design instructions, and builtin speakers — none of which the built-in GuideAnts audio tools expose."
metadata:
  guideants:
    enabled: true
    display_order: 32
    requires_toolsets: [sandbox]
---

# audio.cpp synthesis controls (experimental)

Product TTS is `{text, voice, speed}`. This skill calls raw
`/tts/v1/audio/speech` on Max. Deliverables are WAVs in `Output/`.

## Environment (required for PC → Max)

```text
AUDIOCPP_SKILL_BASE_URL=http://<max-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as Max GA_AUDIOCPP_SKILL_TOKEN>
```

A TTS model must already be loaded on Max via GuideAnts Settings.

## Preflight

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/preflight.py --for tts-controls
```

## Controls

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/engine_tool.py speech "Hello there" \
  -o Output/out.wav \
  [--seed 42] \
  [--language de] \
  [--instructions "a calm, deep narrator voice"] \
  [--voice Vivian]
```

- `--instructions` only on `vdes`-task models (VoiceDesign catalog entry).
- `--language` values are family-specific; engine errors name what is valid.
- Same text + seed + model ⇒ same audio.

## Voices

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/engine_tool.py voices
```

If the list is empty, some families keep speakers only in model `config.json`.

## Related

Cloning: **audiocpp-voice-clone**. Non-catalog families: **audiocpp-deferred-tts**.

## Reporting

End by telling the user what worked and what was blocked, quoting preflight evidence.
