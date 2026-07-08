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
