---
name: audiocpp-tts-controls
description: "Advanced synthesis controls on the loaded audio.cpp TTS model: deterministic output via seed, forcing the spoken language, voice-design from a text description (instructions), and enumerating builtin speaker ids — none of which the built-in GuideAnts audio tools expose. Use when the user wants reproducible audio, a specific language, a described voice, or a list of available speakers."
metadata:
  guideants:
    enabled: true
    display_order: 32
    requires_toolsets: [sandbox]
---

# audio.cpp synthesis controls (experimental)

The GuideAnts TTS wrapper contract is `{text, voice, speed}`. The raw engine
underneath (`127.0.0.1:18084`) accepts more. This skill calls it directly;
deliverables are WAV files in `Output/`, the live voice path is untouched.

## Preflight

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/preflight.py --for tts-controls
```

Trust its verdict over this document. `open: false` usually means no TTS model
is loaded — ask the user to load one via GuideAnts Settings → Local models.

## The controls

All via one script (engine model id auto-detected from the wrapper's health):

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/engine_tool.py speech "Hello there" \
  -o Output/out.wav \
  [--seed 42]                       # deterministic, reproducible synthesis
  [--language de]                   # force spoken language (family-specific values)
  [--instructions "a calm, deep narrator voice"]   # voice design — vdes-task models only
  [--voice Vivian]                  # builtin speaker id
```

Notes:
- `--instructions` only works on a model loaded with the `vdes` task (the
  VoiceDesign catalog entry). Other families reject it — the engine errors
  loudly; surface that text.
- `--language` values are family-specific (name or code); the error text names
  what the family accepts.
- Same text + same seed + same model ⇒ same audio. Useful for A/B-ing one
  parameter at a time.

## Listing available voices

```bash
python3 Output/Skills/audiocpp-tts-controls/scripts/engine_tool.py voices   # engine's cached/builtin ids
curl -s http://127.0.0.1:8084/admin/voice-pack   # wrapper's preset voice packs
curl -s http://127.0.0.1:8084/admin/voices       # wrapper's builtin speaker list
```

Known gap: some families (e.g. Qwen3 CustomVoice) keep builtin speakers in the
model's own config, invisible to `/v1/audio/voices` — if the list comes back
empty, the model dir's `config.json`/`generation_config.json` has the truth.

## Related

Voice cloning from a reference clip is the **audiocpp-voice-clone** skill;
running model families GuideAnts doesn't ship is **audiocpp-deferred-tts**.

## Reporting

End by telling the user what worked and what was blocked, quoting the
preflight/engine evidence.
