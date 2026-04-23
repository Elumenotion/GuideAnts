# Local TTS Demo (VibeVoice)

## Prerequisites

1. Build `guideants-ai` image (CPU or CUDA mode).
2. Download local TTS artifacts to mounted volume:

```powershell
cd docker/llama/run
.\download-tts-models.ps1
```

3. Ensure deployment config points API to local provider:
- `SpeechSynthesis__Provider=local-tts`
- `SpeechSynthesis__LocalTtsBaseUrl=http://guideants-ai:80/tts`

## Service Acceptance

1. Start compose stack:

```powershell
cd docker
docker compose up -d guideants-ai guideants-webapi-ui
```

2. Confirm TTS health:
- `GET http://localhost:8110/tts/health`
- `GET http://localhost:8110/tts/ready` (503 until model is loaded)

3. Load model explicitly:

```powershell
curl -X POST http://localhost:8110/tts/admin/load -H "Content-Type: application/json" -d "{}"
```

4. Synthesize test WAV:

```powershell
curl -X POST http://localhost:8110/tts/synthesize -H "Content-Type: application/json" -d "{\"text\":\"GuideAnts local TTS test.\"}" --output tts-test.wav
```

5. Confirm no regressions:
- `GET /sandbox/health`
- `GET /llama-cpp/health`
- `GET /asr/health`
- `GET /sd/health`

## Client Acceptance (`generate_podcast`)

1. In notebook conversation, run `generate_podcast` with short script.
2. Verify produced `.wav` appears in notebook output files.
3. Play back in client UI.
4. Repeat with medium script in same session.
5. Correlate `x-request-id` across API logs and `/tts` service logs.
