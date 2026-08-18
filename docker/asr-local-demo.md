# Local ASR V1 Acceptance + Demo Script

This checklist is the sign-off path for local ASR in `guideants-ai`.

## Acceptance Criteria

1. Build/start
   - `guideants-ai` builds successfully with ASR service included.
   - Container starts with all three internal processes running:
     - `llama-server` (`/llama-cpp/*`)
     - `ScriptExecutionAgent` (`/sandbox/*`)
     - local ASR service (`/asr/*`)
   - Gateway routes respond concurrently without regression.

2. Local model load
   - Select the installed ASR model through GuideAntsApi ServiceModes.
   - `/asr/ready` stays not-ready until the API submits and applies that plan.
   - `/asr/admin/load` is an internal executor endpoint, not an operator API.

3. API behavior
   - With `SpeechTranscription__Provider=local-asr`, `POST /api/speech/transcribe` returns valid `{ text, durationSeconds }`.
   - Client contract is unchanged.

4. UI behavior
   - Notebook conversation microphone flow transcribes with local ASR.
   - Returned text is inserted into the draft composer.
   - User can send the transcribed text as a normal message.
   - Works for two recordings in one session (short and medium).

5. Regression
   - `/sandbox/health` and `/llama-cpp/health` remain healthy.

## Demo Script

1. Start stack:
   - `docker compose up -d guideants-ai mssql-express`
   - `docker compose --profile webapi-ui up -d guideants-webapi-ui`

2. Confirm runtime health:
   - `GET http://localhost:8110/sandbox/health`
   - `GET http://localhost:8110/llama-cpp/health`
   - `GET http://localhost:8110/asr/health`

3. Check readiness:
   - `GET http://localhost:8110/asr/ready`
   - If already ready, continue to step 5.
   - If not ready, run step 4.

4. In Settings, select the installed local ASR model and activate the local
   SpeechTranscription provider. This updates ServiceModes; GuideAntsApi then
   submits the complete lifecycle plan.

5. Verify readiness:
   - `GET http://localhost:8110/asr/ready`

6. API smoke test:

```bash
curl -X POST http://localhost:5107/api/speech/transcribe \
  -F "audio=@./sample.wav"
```

7. UI demo:
   - Open app UI.
   - Open a notebook conversation.
   - Click microphone, record a short phrase, stop, verify inserted text.
   - Send message.
   - Repeat with a 10-20 second recording, verify inserted text again.

8. Evidence capture:
   - UI screenshot of inserted transcription.
   - Sample `/api/speech/transcribe` response JSON.
   - Correlated log lines with shared request id from:
     - API (`asr_api_request_start/success/failed`)
     - ASR service (`asr_transcribe_start/success/failed`)
