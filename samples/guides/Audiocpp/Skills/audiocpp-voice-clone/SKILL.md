---
name: audiocpp-voice-clone
description: "Clone a voice from a user-supplied reference clip and synthesize speech with it, by calling the raw audio.cpp TTS engine with voice_ref/reference_text — fields the built-in GuideAnts audio tools do not expose. Use when the user wants generated speech to sound like their own voice or another consenting speaker's recording."
metadata:
  guideants:
    enabled: true
    display_order: 31
    requires_toolsets: [sandbox]
---

# audio.cpp voice cloning (experimental)

The GuideAnts TTS wrapper only accepts `{text, voice, speed}`, but the real
`audiocpp_server` engine it spawns (on `127.0.0.1:18084`) accepts a reference
clip for cloning. This skill calls that engine directly. Deliverables are WAV
files in `Output/`; nothing here touches the live phone/voice path.

## Consent rule (read first)

Cloning from a reference clip is a supported, intended use **when the speaker
consents**: the user states the clip is their own voice, or that they have the
speaker's permission. In that case proceed — do not refuse consented cloning as
a "security risk". Decline only when the request is to imitate a third party
without consent (a celebrity, a colleague, a public figure), and say that
consent is the missing piece.

## Preflight

```bash
python3 Output/Skills/audiocpp-voice-clone/scripts/preflight.py --for voice-clone
```

Trust its verdict over this document. `open: false` usually means no TTS model
is loaded — ask the user to load one via GuideAnts Settings → Local models.
A warning about the loaded model's family means `voice_ref` may be rejected;
chatterbox (the `clon`-task catalog model) is the known-good family.

## Cloning

The reference clip must be a file in the workspace (e.g. an upload under
`Output/uploads/`). Same container filesystem, so the engine reads it by path:

```bash
python3 Output/Skills/audiocpp-voice-clone/scripts/engine_tool.py speech \
  "Text to speak in the cloned voice" \
  -o Output/cloned.wav \
  --voice-ref Output/uploads/user_voice.wav \
  [--reference-text "transcript of the reference clip"] \
  [--seed 42]
```

- The engine model id is auto-detected from the wrapper's `/health`
  (`catalogEntryId`); no flag needed.
- `--reference-text` helps families that want the reference transcript.
- `--seed` makes the synthesis reproducible.
- The engine errors loudly on options the loaded family doesn't support —
  surface that error text to the user; it is usually self-explanatory.

## Verifying

Transcribe the result back and check it matches the input text (the ASR engine
at `127.0.0.1:18082` accepts workspace paths):

```bash
python3 Output/Skills/audiocpp-voice-clone/scripts/engine_tool.py transcribe Output/cloned.wav
```

## When this skill isn't enough

- Loaded model rejects `voice_ref` → ask the user to load chatterbox via
  Settings, or use the **audiocpp-deferred-tts** skill (several deferred
  families also clone from reference audio).
- No TTS model loaded at all and the user can't load one → blocked; say so.

## Reporting

End by telling the user what worked and what was blocked, quoting the
preflight/engine evidence, so the experiment log stays honest.
