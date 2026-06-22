# Published Wire API Admin Guide

Last updated: 2026-06-22

This guide covers the OpenAI-compatible published API surface at:

- `/api/published/openai/{pubId}/v1`

## Base URL and Auth

Base URL format:

- `{origin}/api/published/openai/{pubId}/v1`

Supported auth headers depend on published-guide auth mode:

- API key mode:
  - `Authorization: Bearer <api-key>`
  - `x-guideants-apikey: <api-key>`
- Webhook mode:
  - `Authorization: Bearer <token>`
  - `X-Published-Auth: <token>`
- Anonymous mode:
  - no auth header required

## Endpoint Support Matrix

| Endpoint | Toggle key | Alias key | Notes |
|---|---|---|---|
| `GET /models` | `models` | n/a | Returns enabled aliases only. |
| `POST /chat/completions` | `chatCompletions` | `guide` | Non-streaming only. |
| `POST /responses` | `responses` | `guide` | Non-streaming only. |
| `POST /embeddings` | `embeddings` | `embeddings` | Uses routed embeddings service mode. |
| `POST /images/generations` | `imageGenerations` | `image` | Returns `b64_json`. |
| `POST /audio/transcriptions` | `audioTranscriptions` | `transcription` | Requires multipart file upload. |
| `POST /audio/speech` | `audioSpeech` | `speech` | Returns `audio/wav`. |

## Alias Rules

- Client `model` values must be alias names, not provider-native model IDs.
- Default aliases:
  - `guide`
  - `embeddings`
  - `image`
  - `transcription`
  - `speech`
- Missing/unknown alias returns OpenAI-shaped error code:
  - `model_alias_not_found`

## Unsupported and Validation Behavior

Common OpenAI-shaped error codes:

- `endpoint_disabled`
- `invalid_api_key` or `authentication_failed`
- `request_too_large`
- `insufficient_quota` (daily/monthly limits)
- `provider_not_ready`
- `unsupported_feature`

Known unsupported/strict behaviors:

- `chat.completions` and `responses` with `stream=true` return `unsupported_feature`.
- `audio.transcriptions` requires `multipart/form-data` and a non-empty file.
- `audio.speech` supports `response_format=wav` only.
- Invalid/empty content yields endpoint-specific `invalid_request_error` codes.

## Cost Attribution and Usage Reporting

Wire API usage events are attributed with:

- `SourceChannel = wire_api`
- `PublishedGuideId`
- `ExternalRequestId`
- optional `ExternalUserIdentity`

Guide usage now includes API usage reporting grouped by:

- source channel
- endpoint
- alias
- provider/service mode
- status family
- event count
- USD charge

Source filters:

- `all`
- `conversation`
- `published_chat`
- `mcp`
- `wire_api`

## SDK Examples

### curl

```bash
curl -X POST "${ORIGIN}/api/published/openai/${PUB_ID}/v1/chat/completions" \
  -H "Authorization: Bearer ${GUIDEANTS_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{
    "model":"guide",
    "messages":[{"role":"user","content":"Hello"}]
  }'
```

### OpenAI JavaScript SDK

```ts
import OpenAI from "openai";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: `${process.env.ORIGIN}/api/published/openai/${process.env.PUB_ID}/v1`,
});

const resp = await client.chat.completions.create({
  model: "guide",
  messages: [{ role: "user", content: "Hello" }],
});
```

### OpenAI Python SDK

```python
import os
from openai import OpenAI

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url=f"{os.environ['ORIGIN']}/api/published/openai/{os.environ['PUB_ID']}/v1",
)

resp = client.chat.completions.create(
    model="guide",
    messages=[{"role": "user", "content": "Hello"}],
)
```

## Troubleshooting

| Symptom | Typical error code | Action |
|---|---|---|
| 401/403 auth failures | `invalid_api_key`, `authentication_failed`, `endpoint_disabled` | Verify auth mode and header type for that guide. |
| Endpoint blocked | `endpoint_disabled` | Enable endpoint flag in Publish dialog > APIs tab. |
| Alias rejected | `model_alias_not_found` | Use configured alias from Publish dialog > APIs tab. |
| Provider routing issue | `provider_not_ready` | Verify service mode/profile and provider credentials. |
| Cost limit reached | `insufficient_quota` with `reason` (`daily_limit_exceeded` / `monthly_limit_exceeded`) | Raise limits or wait for UTC window reset. |
| Request too large | `request_too_large` | Reduce payload or raise endpoint max request bytes. |
| Unsupported request capability | `unsupported_feature` | Remove unsupported field (for example streaming). |

## Smoke Verification Pointers

Practical smoke coverage is provided by handler and resolver tests:

- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`
- `src/server/GuideAntsApi.Tests/Services/PublishedWireApi/PublishedApiExecutionContextResolverTests.cs`
- `src/server/GuideAntsApi.Tests/Services/PublishedWireApi/PublishedWireUsageRecorderTests.cs`

