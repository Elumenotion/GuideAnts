# Sandbox Wire API

Guide-configured OpenAI-compatible API exposed inside sandbox Python executions (Run Python, sandbox module tools, and scheduled Python jobs).

## Runtime contract

When sandbox wire is enabled for a guide (or forced on a scheduled job), the API injects:

- `OPENAI_BASE_URL` — internal base URL (`SandboxWireApi:InternalBaseUrl`, default `http://guideants-webapi-ui:8080/api/internal/sandbox/openai/v1`)
- `OPENAI_API_KEY` — short-lived execution-scoped JWT (no persisted token registry)

Python clients using the official OpenAI SDK work unchanged:

```python
from openai import OpenAI

client = OpenAI()  # reads OPENAI_BASE_URL and OPENAI_API_KEY from the environment
response = client.chat.completions.create(
    model="guide",
    messages=[{"role": "user", "content": "Hello"}],
)
```

## Configuration

### Guide (Tools tab)

Enable **Sandbox Wire API** when Run Python and/or sandbox module tool sources are active. Set:

- Target assistant (must not be the owning guide; cycles are rejected at save and mint time)
- Endpoint flags, model aliases, request size limits
- Optional daily/monthly USD limits (`sourceChannel = sandbox_wire_api`)

### Scheduled Python jobs

Optional per-job overrides:

- `ExposeSandboxWireApi` — force-enable for the run even if guide config is off
- `WireTargetAssistantId` — override target assistant
- `WireDailyLimitUsd` / `WireMonthlyLimitUsd` — job-level caps (embedded in JWT)
- Attribution conversation — optional per-run conversation for usage attribution only (calls remain stateless)

## Security

- JWT audience: `GuideAnts.SandboxWire`
- Validation is the primary gate on `/api/internal/sandbox/openai/v1`
- Revocation is by TTL only (no DB execution registry)
- Owner guide cannot target itself; ancestor chain is checked at mint and validate time

## Operations

Set `SandboxWireApi:SigningKey` in production (minimum 32 characters). Compose services must resolve `InternalBaseUrl` from the sandbox network namespace.
