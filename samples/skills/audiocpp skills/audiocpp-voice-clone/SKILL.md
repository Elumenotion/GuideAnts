---
name: audiocpp-voice-clone
description: "Clone a voice from a user-supplied reference clip via the Max raw audiocpp gateway (stage /files, then /tts/v1/audio/speech with voice_ref), which the built-in GuideAnts audio tools do not expose."
metadata:
  guideants:
    enabled: true
    display_order: 31
    requires_toolsets: [sandbox]
---

# audio.cpp voice cloning (experimental)

Product TTS only accepts `{text, voice, speed}`. This skill stages the reference
clip (`/files`) then calls raw `/tts/v1/audio/speech` with `voice_ref`. WAVs
land in `Output/`.

## Consent rule (read first)

Proceed only when the speaker consents (own voice or stated permission). Decline
unconsented third-party imitation.

## Environment (required for PC → Max)

```text
AUDIOCPP_SKILL_BASE_URL=http://<max-lan-ip>:8112/audiocpp-skill
AUDIOCPP_SKILL_TOKEN=<same as Max GA_AUDIOCPP_SKILL_TOKEN>
```

Load a clon-capable TTS model on Max first (chatterbox is the known-good catalog
entry).

## Preflight

```bash
python3 Output/Skills/audiocpp-voice-clone/scripts/preflight.py --for voice-clone
```

## Cloning

Reference clip must be a workspace file (e.g. `Output/uploads/…`). The script
uploads it to Max — no shared filesystem with the engine required:

```bash
python3 Output/Skills/audiocpp-voice-clone/scripts/engine_tool.py speech \
  "Text to speak in the cloned voice" \
  -o Output/cloned.wav \
  --voice-ref Output/uploads/user_voice.wav \
  [--reference-text "transcript of the reference clip"] \
  [--seed 42]
```

Model id is auto-detected from gateway TTS health (`catalogEntryId`).

## Verify

```bash
python3 Output/Skills/audiocpp-voice-clone/scripts/engine_tool.py transcribe Output/cloned.wav
```

## When this isn't enough

- Loaded model rejects `voice_ref` → load chatterbox on Max, or try
  **audiocpp-deferred-tts**.
- No TTS loaded on Max → blocked; say so.

## Reporting

End by telling the user what worked and what was blocked, quoting preflight evidence.
