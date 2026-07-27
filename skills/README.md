# audio.cpp skills

Experimental GuideAnts skills that reach past the built-in audio tools'
narrow `{text, voice, speed}` contract into the raw `audiocpp_server` engine
underneath — without any GuideAnts code changes. They exist because
GuideAnts' TTS/ASR wrapper services each spawn a real audio.cpp engine on
localhost, on the same container filesystem as the sandbox; these skills
talk to that engine directly (or spawn a private one) to unlock capabilities
the wrapper doesn't expose.

All deliverables land in `Output/` as WAV/text/JSON files. Nothing here
touches the live phone/voice path or shows up in the GuideAnts voice picker
— it's a sandboxed side channel for "can audio.cpp do X?" experiments.

## Skills at a glance

| Skill | What it does | Requires |
|---|---|---|
| [`audiocpp`](audiocpp/) | All-in-one umbrella skill — a single doc that folds in everything the six skills below cover individually (synthesis controls, voice cloning, ASR, diarization, deferred TTS, host-native), organized as four access routes, plus a probe to see which routes are open | sandbox |
| [`audiocpp-tts-controls`](audiocpp-tts-controls/) | Deterministic synthesis (`seed`), forced language, voice-design from a text description (`instructions`), listing builtin speaker ids | TTS model loaded |
| [`audiocpp-voice-clone`](audiocpp-voice-clone/) | Clone a voice from a user-supplied reference clip (`voice_ref` + optional `reference_text`) and synthesize with it | TTS model loaded (chatterbox/`clon`-task known-good) |
| [`audiocpp-asr`](audiocpp-asr/) | Transcribe workspace files by path with a language hint (no upload, no size cap); sideload other qwen3-family ASR snapshots from Hugging Face | ASR model loaded |
| [`audiocpp-diarize`](audiocpp-diarize/) | Speaker diarization — who spoke when — producing a speaker-labeled, timestamped transcript from any recording | sandbox (spawns its own engine) |
| [`audiocpp-deferred-tts`](audiocpp-deferred-tts/) | Run TTS families audio.cpp supports but GuideAnts doesn't ship (Qwen3 CustomVoice, VibeVoice, MioTTS, VoxCPM2, PocketTTS, Vevo2) by downloading the model and spawning a private engine | sandbox, VRAM headroom |
| [`audiocpp-host-tts`](audiocpp-host-tts/) | Synthesize against the *user's own* host-native `audiocpp_server` build, including families the container binary lacks (e.g. Kokoro, Parakeet forks) | user runs their own audio.cpp server |

## `audiocpp` vs. the six narrower skills

`audiocpp` it's a large,
self-contained skill whose `SKILL.md` contains the same material
as the other six, reorganized around **four access routes** instead of one
skill per capability:

1. **Talk to the already-loaded model's engine directly** — covers what
   `audiocpp-tts-controls`, `audiocpp-voice-clone`, and `audiocpp-asr` each
   document on their own (seed, language forcing, voice-design
   `instructions`, voice cloning, transcription by path). Cheapest, no
   download.
2. **Sideload a non-catalog snapshot through the ASR wrapper** — the same
   sideload flow `audiocpp-asr` documents (qwen3_asr-family only; the TTS
   wrapper hard-rejects this).
3. **Spawn a private engine in the sandbox** for a model the catalog doesn't
   carry — the same pattern `audiocpp-deferred-tts` documents for TTS
   families.
4. **Reach the user's host-native engine** — the same thing
   `audiocpp-host-tts` documents.

It also inlines the full diarization recipe (same content as
`audiocpp-diarize`) as a worked scenario.

Practically: the six narrower skills are focused extracts of `audiocpp` —
each one lighter-weight and scoped to a single capability. `audiocpp` is the
comprehensive version, useful when the exact capability needed isn't known
yet or spans more than one route; run its `probe.py` first to see which
routes are actually open in a given deployment.

## Common patterns across these skills

- **Preflight/probe first, trust its verdict over the docs.** Every skill
  ships a `preflight.py` (or, for the umbrella skill, `probe.py`) that
  reports what's actually reachable — loaded model, ports, writable dirs,
  GPU state — before you try anything.
- **Consent rule for voice cloning.** Cloning from a reference clip is a
  supported use when the speaker consents (their own voice, or stated
  permission). Decline only unconsented third-party imitation.
- **Private engines must be stopped.** Skills that spawn their own
  `audiocpp_server` (`audiocpp-deferred-tts`, `audiocpp-diarize`) always
  pair `spawn_engine.py start` with `spawn_engine.py stop` when done — a
  second engine competes for VRAM with the wrappers' loaded models.
- **Script budget is ~5 minutes per call.** Spawning and large downloads
  detach and return immediately; poll `status` / rerun in a follow-up call
  rather than blocking.
- **Report honestly.** Every skill ends by telling the user what worked,
  what was blocked, and why — quoting the preflight/engine evidence — since
  the point of this skill set is answering "is it possible?", not papering
  over failures.

## Limits (apply across the set)

- Loaders not compiled into the container binary (`kokoro_tts`,
  `parakeet_tdt`) can't be unblocked by downloading — only a custom host
  build via `audiocpp-host-tts` can serve those.
- Service-level env vars (e.g. `GA_ASR_ENGINE_FAMILY`) need an actual
  GuideAnts compose/.env change; no skill can set those.
- Diarization tops out at 4 speakers (model variant) and is offline-only;
  speaker ids are arbitrary labels, not real names.
- `audiocpp-host-tts` is TTS-only — the host engine's transcription endpoint
  wants server-local paths the sandbox can't provide.
