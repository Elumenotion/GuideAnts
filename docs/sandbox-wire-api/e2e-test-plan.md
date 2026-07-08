# Sandbox Wire API — E2E Test Plan

Last updated: 2026-07-07

Manual end-to-end acceptance testing for the **Sandbox Wire API** feature on branch
`feature/sandbox-ai-endpoints`. Validates that a guide with Run Python and sandbox
wire configuration can invoke the OpenAI Python SDK inside the sandbox and route calls
to a crew target assistant.

Companion docs:

- Runtime contract: [`README.md`](./README.md)
- Published wire SDK patterns (adapt for sandbox): [`../published-wire-api/admin-wire-api-guide.md`](../published-wire-api/admin-wire-api-guide.md)

---

## 1. Scope

### In scope (v1 acceptance)

- Guide editor: enable **Run Python** + **AI model access for Python tools**
- Crew target assistant with **no instructions**
- Chat-driven Run Python smoke tests (models list + chat completion)
- Usage attribution on Guide Usage page (`sandbox_wire_api` source channel)

### Out of scope (v1)

- Responses API, embeddings, images, audio wire endpoints
- Scheduled job wire overrides (`ExposeSandboxWireApi`)
- Cost limit enforcement (optional second pass)
- Bootstrap guide bundle / automated Playwright test suite

---

## 2. Environment prerequisites

Before running tests, confirm:

| # | Requirement | How to verify |
|---|-------------|---------------|
| 1 | Branch `feature/sandbox-ai-endpoints` deployed | Migration `AddSandboxWireApiFields` applied |
| 2 | `SandboxWireApi:SigningKey` set (≥ 32 chars) | API starts; JWT mint does not fail |
| 3 | `SandboxWireApi:InternalBaseUrl` reachable from sandbox containers | Default: `http://guideants-webapi-ui:8080/api/internal/sandbox/openai/v1` |
| 4 | At least one working inference provider | Settings → Models; target assistant has a model |
| 5 | Docker sandbox stack running | Script execution agent + python sandbox image |
| 6 | `openai` package in sandbox | Pre-installed in `docker/build/Sandboxes/python311Slim/requirements.txt` |

---

## 3. Test entities

Create via UI (validates `SandboxWireApiPanel` UX).

### 3a. Crew assistant — **Wire Target**

| Field | Value |
|-------|-------|
| Name | `Wire Target` |
| Instructions | **Empty** |
| Model | Any working model in the stack |
| Tools | None |

Purpose: bare routing target for sandbox wire. Success is verified via stdout and
usage metering, not distinctive reply formatting.

### 3b. Guide — **Sandbox Wire SDK Test**

| Field | Value |
|-------|-------|
| Name | `Sandbox Wire SDK Test` |
| Instructions | See §4 |
| Model | Any working chat model |
| Crew | Add `Wire Target` |
| Tools (Tools tab) | Enable **Run Python** |

### 3c. Sandbox wire configuration (Tools tab)

Panel label: **AI model access for Python tools** (`data-tour-id="guide.tools.sandbox-wire"`).

| Setting | Value |
|---------|-------|
| Enable | ✓ Give this guide's Python tools AI model access |
| Target assistant | `Wire Target` |
| Daily/monthly limits | Optional for first pass |

Save the guide. Server rejects: target = owner guide, circular references.

---

## 4. Guide instructions

Paste into the guide's **instructions** field (not a separate SKILL.md).

```markdown
## Sandbox Wire API — Python testing

When the user asks to test AI access from Python, use the **Run Python** tool.

The sandbox automatically provides:
- `OPENAI_BASE_URL` — internal GuideAnts wire endpoint
- `OPENAI_API_KEY` — short-lived execution token (do not print or hardcode)

### Rules
- Use `from openai import OpenAI` and `client = OpenAI()` with no arguments.
- Use model alias `"guide"` — never a provider-native model ID like `gpt-4o`.
- Do not set `base_url` or `api_key` manually.
- Do not use streaming (`stream=True`) — not supported.
- Prefer `client.chat.completions.create()` over Responses API.
- The `openai` package is pre-installed; do not pip install.

### Smoke test — list models

```python
import json
from openai import OpenAI

client = OpenAI()
models = client.models.list()
ids = sorted(m.id for m in models.data)
print("SANDBOX_WIRE_MODELS:", json.dumps(ids))
assert any(id.lower() == "guide" for id in ids), f"'guide' alias missing: {ids}"
print("PASS: models")
```

### Smoke test — chat completion

```python
from openai import OpenAI

client = OpenAI()
resp = client.chat.completions.create(
    model="guide",
    messages=[{"role": "user", "content": "Reply with one short sentence confirming you received this message."}],
)
text = (resp.choices[0].message.content or "").strip()
print("ASSISTANT_REPLY:", text)
assert text, "empty assistant reply"
print("PASS: chat.completions")
```

On success, print stdout verbatim. On failure, print the full exception and stderr.
```

---

## 5. E2E chat scenarios

Create a **new project** from the guide. Open a notebook chat.

| Step | User message | Pass criteria |
|------|--------------|---------------|
| **1 — Models** | Run the models smoke test from your instructions using Run Python. | stdout contains `SANDBOX_WIRE_MODELS:` with `"guide"` and `PASS: models` |
| **2 — Chat** | Run the chat completion smoke test from your instructions. | stdout contains `ASSISTANT_REPLY:` (non-empty) and `PASS: chat.completions` |
| **3 — Agent-written** | Write and run a short Python script that asks the wire API what 17×23 is and prints only the numeric answer. | stdout shows `391` (or equivalent) using `model="guide"` |

---

## 6. Observability verification

After steps 1–2:

| Check | Where | Pass criteria |
|-------|-------|---------------|
| Usage events | Guide → **Usage**, filter **Sandbox Wire API** | Events with `sourceChannel = sandbox_wire_api` |
| Endpoints | Same page | `models` and `chat.completions` recorded |
| Attribution | Usage detail | Linked to project/notebook; token counts or charges present |
| No auth leaks | Chat tool output | `OPENAI_API_KEY` value never printed |
| Server logs | API container | No 401 from `/api/internal/sandbox/openai/v1` during runs |

---

## 7. Negative cases (optional, second pass)

| Scenario | Expected |
|----------|----------|
| Sandbox wire **disabled**, Run Python with `OpenAI()` | Auth/connection failure (no env injection) |
| Script uses `model="gpt-4o"` | `model_alias_not_found` |
| Target assistant = owner guide | Blocked at save time in UI |
| `stream=True` | `unsupported_feature` |

---

## 8. Acceptance criteria

### Must pass

1. Guide saves with Run Python + sandbox wire enabled targeting crew assistant.
2. Chat step 1 (models list) completes with `PASS: models`.
3. Chat step 2 (chat completion) completes with `PASS: chat.completions`.
4. Guide Usage shows at least one `sandbox_wire_api` event for the test project.

### Should pass

5. Chat step 3 (agent-written script) succeeds without copy-paste from instructions.
6. API key never appears in chat output.

---

## 9. Browser automation — playwright-cli session

Interactive E2E setup and chat runs use **playwright-cli** with the **Chrome extension**
so the operator can observe and consent in a real browser tab.

### 9.1 Tooling

```powershell
# Verify CLI is available (global or npx)
playwright-cli --version
# e.g. 1.59.0-alpha-1771104257000

# If global command missing:
npx --no-install playwright-cli --version
```

Install if needed:

```powershell
npm install -g @playwright/cli@latest
```

### 9.2 Session name

Use a **named session** for this test work:

| Property | Value |
|----------|-------|
| Session name | `sandbox-wire-test` |
| Browser | Chrome (via extension) |
| App URL | `http://localhost:5107/` |

### 9.3 Open / reconnect

**First open** (operator must approve extension connection in Chrome):

```powershell
playwright-cli -s=sandbox-wire-test open --extension
```

Open and navigate in one step:

```powershell
playwright-cli -s=sandbox-wire-test open --extension http://localhost:5107/
```

**Reconnect to an existing session** (browser already open):

```powershell
playwright-cli -s=sandbox-wire-test snapshot
```

If session is closed, open again with the same `-s=sandbox-wire-test` name.

### 9.4 Session management

```powershell
# List all sessions and status
playwright-cli list

# Expected when active:
#   sandbox-wire-test: status: open, browser-type: chrome

# Close this session only
playwright-cli -s=sandbox-wire-test close

# Close all sessions
playwright-cli close-all
```

### 9.5 Typical workflow commands

All commands target the named session with `-s=sandbox-wire-test`.

```powershell
# Page state (refs for clicks/fills)
playwright-cli -s=sandbox-wire-test snapshot

# Save snapshot artifact
playwright-cli -s=sandbox-wire-test snapshot --filename=after-guide-save.yaml

# Navigate
playwright-cli -s=sandbox-wire-test goto http://localhost:5107/

# Interact (use refs from latest snapshot, e.g. e19)
playwright-cli -s=sandbox-wire-test click e19
playwright-cli -s=sandbox-wire-test fill e5 "Sandbox Wire SDK Test"
playwright-cli -s=sandbox-wire-test press Enter

# Role/CSS selectors when refs are awkward
playwright-cli -s=sandbox-wire-test click "role=button[name='Open Settings']"

# Debug
playwright-cli -s=sandbox-wire-test console
playwright-cli -s=sandbox-wire-test network
```

Snapshot files are written under `.playwright-cli/` in the repo root (gitignored).

### 9.6 UI tour IDs (automation hints)

| Surface | `data-tour-id` |
|---------|----------------|
| Tools catalog | `guide.tools.catalog` |
| Sandbox wire panel | `guide.tools.sandbox-wire` |

### 9.7 Automation sequence (high level)

Operator observes in Chrome; agent drives via playwright-cli.

1. `open --extension` → consent in browser
2. Navigate to guide/assistant admin (Settings or guide editor routes)
3. Create **Wire Target** assistant (empty instructions)
4. Create **Sandbox Wire SDK Test** guide; paste §4 instructions
5. Tools tab: enable Run Python; configure sandbox wire → Wire Target; save
6. New project from guide → notebook chat
7. Send §5 user messages; verify tool stdout in chat
8. Open Guide Usage → filter Sandbox Wire API → confirm events

---

## 10. Evidence log

Record results here during the test run.

| Date | Tester | Step | Result | Notes |
|------|--------|------|--------|-------|
| | | 1 — Models | | |
| | | 2 — Chat | | |
| | | 3 — Agent-written | | |
| | | Usage `sandbox_wire_api` | | |

---

## 11. Related automated tests (not a substitute for E2E)

Server integration/unit coverage exists but does not replace UI + sandbox execution:

- `src/server/GuideAntsApi.IntegrationTests/Endpoints/SandboxOpenAiWireEndpointsTests.cs`
- `src/server/GuideAntsApi.Tests/Services/SandboxWireApi/SandboxWireEnvironmentProvisionerTests.cs`
- `src/server/GuideAntsApi.Tests/Services/SandboxWireApi/SandboxWireJwtServiceTests.cs`
- `src/client/src/components/guides/editor/__tests__/SandboxWireApiPanel.test.tsx`
