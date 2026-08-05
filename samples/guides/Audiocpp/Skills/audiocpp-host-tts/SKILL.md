---
name: audiocpp-host-tts
description: "Synthesize speech against the user's own host-native audiocpp_server build (outside the GuideAnts container) — including model families the container binary lacks, like Kokoro or Parakeet forks. TTS only. Use when the user says they run their own audio.cpp server or asks for a family the container build cannot load."
metadata:
  guideants:
    enabled: true
    display_order: 36
    requires_toolsets: [sandbox]
---

# audio.cpp host-native engine (experimental)

The sandbox can reach an `audiocpp_server` the user runs natively on the host —
same raw API as the container engines, but with whatever models and loaders
*their* build has (e.g. a fork compiled with Kokoro enabled). Deliverables are
WAV files in `Output/`.

## Preflight

```bash
python3 Output/Skills/audiocpp-host-tts/scripts/preflight.py --for host-tts
```

Resolution order: `AUDIOCPP_ENGINE_URL` env (set via the guide editor's
Environment variables UI), else `http://host.docker.internal:8080`. The
preflight also distinguishes a real audio engine from a llama-server that
happens to answer `/health` on the same port.

## Synthesizing

`--model` is required — there is no wrapper to auto-detect from:

```bash
python3 Output/Skills/audiocpp-host-tts/scripts/engine_tool.py speech "Hello" \
  --engine-url http://host.docker.internal:8080 --model <engine-model-id> \
  -o Output/hello.wav [--voice <id>] [--seed 42] [--language en] [--voice-ref ...]
```

List the models the host build serves: `GET <base>/v1/models`; voices:
`engine_tool.py voices --engine-url <base> --model <id>`.

The consent rule applies to `--voice-ref` cloning here too: proceed for the
user's own voice or a consenting speaker; decline third-party imitation.

## Hard limit: TTS only

The host engine's `/v1/audio/transcriptions` takes a *server-local path* in its
JSON body — files in this sandbox are not visible to the host process, so
transcription against the host engine cannot work from here. Use the container
ASR (the **audiocpp-asr-extended** skill) instead.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| Unreachable | Server not bound to `0.0.0.0`, or Windows Firewall blocking the Docker adapter | Ask the user to check both |
| `/health` 200 but no `/v1/audio/*` | A llama-server on that port, not audiocpp | Ask which port their audio.cpp build listens on |
| Model errors | Family/option mismatch in their build | Surface the engine's error text — it names the problem |

## Reporting

End by telling the user what worked and what was blocked, quoting the
preflight/engine evidence.
