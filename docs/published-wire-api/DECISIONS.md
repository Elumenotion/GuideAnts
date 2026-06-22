# Published Wire APIs — Locked Decisions

Last updated: 2026-06-22  
Status: all decisions below are locked for this execution plan.

Any change here after implementation begins requires explicit deviation logging
and phase re-validation.

## PW-1: Base URL

Locked value:

- `/api/published/openai/{pubId}/v1`

## PW-2: Compatibility target

Locked value:

- v1 scope is OpenAI-compatible API surface only.
- Provider-native pass-through is out of scope.

## PW-3: Enabled endpoints

Locked value:

- `/models`
- `/chat/completions`
- `/responses`
- `/embeddings`
- `/images/generations`
- `/audio/transcriptions`
- `/audio/speech`

## PW-4: Model identifier contract

Locked value:

- Client-supplied `model` is a published alias.
- Raw provider model IDs are not part of the external contract.

Default aliases:

- `guide`
- `embeddings`
- `image`
- `transcription`
- `speech`

## PW-5: Provider selection ownership

Locked value:

- Provider selection remains internal.
- Chat routes use published guide/default chat resolution.
- Non-chat routes use `ServiceModes`.

## PW-6: Auth behavior

Locked value:

- Auth behavior follows `PublishedGuide.AuthMode`.
- API key mode accepts:
  - `Authorization: Bearer <key>`
  - `x-guideants-apikey: <key>`
- Webhook mode accepts:
  - `Authorization: Bearer <token>`
  - `X-Published-Auth: <token>`

## PW-7: OpenAI field handling and capability errors

Locked value:

- Unknown OpenAI fields are ignored.
- Known unsupported capabilities return an OpenAI-shaped `400`.

## PW-8: Cost-period semantics

Locked value:

- "Billing-period limit" is implemented as monthly UTC calendar limit.
- Daily limit also uses UTC day boundaries.

Rationale:

- No subscription billing-period model exists yet.

## PW-9: Usage attribution contract

Locked value for wire API usage:

- `SourceChannel = wire_api`
- `PublishedGuideId` set
- `ExternalRequestId` set when available
- `ExternalUserIdentity` optional

Implication:

- Metering for successful wire calls is required even outside conversations.
