# GuideAnts-Native Signal Guide Publishing Proposal

## Goal

Extend Guide publishing so a published guide can optionally expose chat over Signal, with no OpenClaw dependency and no requirement for a public inbound GuideAnts address.

## Why this fits GuideAnts

GuideAnts already has the core we need:

- publish-time governance on `PublishedGuide`
- auth/limits/usage controls on published endpoints
- conversation execution via `IPublishedConversationService`
- external identity attribution via `ExternalUserIdentity`

This proposal adds a Signal connector layer that reuses existing conversation and governance logic.

## Constraint summary

- Signal does not provide an official hosted bot/business API equivalent to WhatsApp Cloud API.
- Practical automation is typically self-hosted using `signal-cli`-based connectors.
- Many GuideAnts deployments are private/NATed, so inbound public webhooks are not a safe assumption.

## Architecture

### 1) Publish-time channel config (Signal only)

Add optional Signal channel settings to published guide config.

```json
{
  "channels": {
    "signal": {
      "enabled": true,
      "provider": "signal_cli_rest",
      "accountE164": "+15551234567",
      "baseUrl": "http://signal-gateway:8080",
      "apiKeyRef": "secret://signal/api-key",
      "ingressMode": "local_connector",
      "groupIsolation": true
    }
  }
}
```

Storage options:

- Minimal churn: add `ChannelConfigJson` to `PublishedGuide`.
- Long-term: add `PublishedGuideChannelBinding` rows keyed by `PublishedGuideId + Channel + Account`.

### 2) Native connector layer

Add:

- `IPublishedChannelConnector`
  - `NormalizeInbound(...)`
  - `SendOutbound(...)`
  - `ValidateConfig(...)`
- `SignalCliConnector` implementation

### 3) Inbound model (private-friendly first)

Primary mode: `local_connector`

- `SignalInboundWorker` subscribes to `signal-cli-rest-api` (websocket/SSE/poll depending on gateway mode).
- Worker forwards normalized inbound messages directly to GuideAnts application services.
- No public inbound GuideAnts endpoint required.

Optional push mode:

- `POST /api/published/channels/signal/events` for external push-based connectors.

### 4) Conversation identity strategy

Use deterministic external identity keys:

- DM: `channel:signal:{account}:{user}`
- Group: `channel:signal:{account}:group:{groupId}:user:{user}`

This enables stable conversation continuation per sender/thread.

### 5) Shared execution path

For each inbound message:

1. Resolve published guide by Signal channel binding.
2. Normalize sender/thread to external identity key.
3. Resolve/create conversation for that identity.
4. Execute via `IPublishedConversationService.SendMessageStreamAsync(...)`.
5. Collect final assistant output.
6. Send outbound reply through `SignalCliConnector`.

### 6) Outbound formatting policy

Add:

- `IPublishedChannelMessageFormatter`
- `PublishedChannelRateLimiter`

Phase 1:

- text-first replies
- split/truncate policy for channel limits
- link fallback for generated files/media

Phase 2:

- richer media/reaction mappings

## UI and user journey

### Publish dialog changes

In `Publish Guide` add a `Channels` tab with a Signal section:

- `Enable Signal publishing` toggle
- `Ingress mode` (default `local_connector`)
- `Signal account (E.164)` field
- `Signal gateway base URL` field
- `API key secret ref` field (optional if gateway is private/trusted)
- `Group isolation` toggle
- `Test connection` action

### Save flow

1. User enables Signal and enters connector settings.
2. User runs `Test connection`.
3. On success, user saves publish settings.
4. Guide is now available via web and Signal channels.

## Backend changes

- Data model:
  - `PublishedGuide.ChannelConfigJson` (or binding table)
- DTOs:
  - `PublishedChannelsConfigDto`
  - `PublishedSignalConfigDto`
  - include in publish/update/read DTOs
- Endpoints/services:
  - update `GuidesPublishingEndpoints` for Signal config persistence
  - add `SignalInboundWorker`
  - add `SignalCliConnector`
  - add `PublishedChannelConversationResolver`
  - add `PublishedChannelMessageFormatter`

## Security and governance

- Keep existing published guide limits/auth enforcement.
- Require authenticated connector access when `baseUrl` is non-local/private.
- Add sender/group allowlists (optional).
- Sign or authenticate worker-to-core handoff boundaries when split-process.

## Rollout plan

1. Add schema + DTO + hidden UI fields.
2. Implement Signal connector and connection test.
3. Implement inbound worker + shared execution wiring.
4. Implement outbound formatting and rate controls.
5. Add telemetry (message counts, errors, latency by channel).
6. Enable feature flag for pilot users, then broaden rollout.

## Test plan

- Unit:
  - identity-key normalization
  - formatter splitting/truncation
  - connector config validation
- Integration:
  - publish with Signal enabled
  - inbound DM creates conversation
  - repeated DM resumes same conversation
  - group message isolation works
  - limits/auth errors return safe channel responses
- E2E:
  - `signal-cli-rest-api` roundtrip in private-network mode

## Tradeoffs

- Pros:
  - no OpenClaw dependency
  - private-network friendly by default
  - maximum reuse of current published-guide runtime
- Cons:
  - depends on self-hosted Signal connector operations
  - richer Signal media support will need incremental formatter work

## Recommendation

Proceed Signal-only now with `local_connector` as the default ingress mode.

This delivers channel publishing for private self-hosted GuideAnts deployments without introducing public webhook infrastructure requirements.
