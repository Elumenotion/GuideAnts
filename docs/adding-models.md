# Adding Models in Settings

Operator guide for registering models under **Settings → Models & Runtime**.

Last updated: 2026-08-10

**Architecture:** Every catalog model — cloud API and local llama — owns its sampling, reasoning, and request shaping on the **model row**. Runtime profiles are not part of operator workflow. See [model-chat-behavior-contract.md](model-chat-behavior-contract.md).

---

## Overview

Models are registered in the **Catalog**. Each row binds a provider to a model ID and carries the parameter surface (temperature, top-p, reasoning effort, thinking control) that guide and assistant builders expose.

Before adding a model, configure the provider connection under **Settings → Connections**.

---

## 1. Navigate to Models & Runtime

Open **Settings**, then **Models & Runtime** in the left navigation.

The workspace has two sub-tabs:

| Sub-tab | Purpose |
|--------|---------|
| **Catalog** | Add, edit, and delete model registrations (including chat behavior JSON on each row) |
| **Local Llama Runtime** | Inventory, load/unload, and lifecycle actions for local llama-cpp models |

---

## 2. Model chat behavior (on the catalog row)

Parameter and reasoning controls live on the **model row**, not on a shared profile.

When you add or edit a catalog entry:

| Field | Purpose |
|-------|---------|
| **Sampling Parameters JSON** | Defines sliders (temperature, top-p, etc.) and defaults for guide/assistant builders |
| **Reasoning Choices JSON** | Allowed reasoning effort values (e.g. `["none","low","medium","high"]`) |
| **Thinking Control JSON** (llama-cpp, HF, OpenRouter) | Maps each reasoning choice to API actions |
| **Request fields when tools present** (llama-cpp, HF, OpenRouter) | Extra body fields when tools are attached (e.g. `parallel_tool_calls`) |

**Sampling Parameters JSON** example:

```json
{
  "temperature": {
    "key": "temperature",
    "label": "Temperature",
    "description": "Controls randomness",
    "min": 0.0,
    "max": 2.0,
    "step": 0.1,
    "default": 1.0,
    "displayOrder": 0,
    "exposedInGuideBuilder": true
  }
}
```

Set `exposedInGuideBuilder: true` for parameters that should appear as sliders in guide and assistant configuration panels.

**Thinking Control JSON** example:

```json
{
  "defaultChoice": "medium",
  "choiceActions": {
    "none": [],
    "low": [],
    "medium": [],
    "high": []
  }
}
```

For llama-cpp, actions can set request fields, nested template kwargs, or `SystemMessagePrefix` strings. For cloud providers, reasoning effort is typically forwarded directly unless you define explicit actions (HF / OpenRouter support row-owned thinking control).

Known-model typeahead may pre-fill these fields from seeds in the client (`parameterSurfaceSeeds.ts`, `knownCloudModels.json`).

---

## 3. Adding a cloud model — walkthrough

Switch to **Catalog** and click **Add Model**.

Settings and Home's Add AI Services wizard share the same backend: `POST /api/settings/models:add`.

### Step 1 — Choose provider

Select the API integration. The provider must be connected under **Settings → Connections** for the model to reach Ready readiness.

### Step 2 — Catalog entry

| Field | Description |
|-------|-------------|
| **Model ID** | Provider's canonical identifier (unique in catalog) |
| **Display Name** | Label in selectors |
| **Description** | Optional |
| **Display Order** | Sort order (lower first) |
| **Active** | Uncheck to hide without deleting |

### Step 3 — Parameter surface

Configure **Sampling Parameters JSON** and **Reasoning Choices JSON** on the form (and thinking / request fields when the provider supports them). There is no runtime-profile picker.

Provider-specific toggles (e.g. OpenAI Responses reasoning, Anthropic thinking) may appear below the JSON editors.

### Step 4 — Review and create

Cloud models are added synchronously.

### Result

The catalog shows the new row with readiness. **Ready** means the provider connection is healthy. **Blocked** usually means a missing API key under Connections.

---

## 4. Adding a local llama-cpp model

### Curated install (recommended)

Pick a model from the shipped catalog and quant. The manifest supplies HF artifacts, router preset, and install-time chat-behavior defaults; after install, behavior is stored on the **model row**.

Use **Repair** / **Adopt curated** on the installation panel to re-apply curator router preset changes. Edit sampling and reasoning on the catalog row form.

### Custom Hugging Face / attach existing alias

Advanced paths still offer a **runtime profile** dropdown **at install only**. Selected profile fields are copied onto the model row at creation. Ongoing edits happen on the catalog row.

---

## 5. Editing a catalog row

Click **Edit** on any row to change display metadata, active flag, and all chat-behavior JSON fields.

Changing the parameter surface affects new guide/assistant configuration immediately; in-flight conversations are not retroactively changed.

---

## 6. Tips and troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Model shows Blocked | Provider not connected | Configure connection under **Settings → Connections** |
| Temperature / Top P missing in guide builder | Empty `SamplingParametersJson` or no `exposedInGuideBuilder: true` | Edit catalog row; populate sampling JSON |
| Reasoning effort missing | Empty `ReasoningChoicesJson` or `ThinkingControlJson` | Edit catalog row |
| Llama model fails at chat | Missing `ThinkingControlJson` on row | Edit catalog row; ensure thinking control is configured |

---

## Related docs

- [model-chat-behavior-contract.md](model-chat-behavior-contract.md) — authority model and contributor rules
- [llama-model-download-and-runtime-management.md](llama-model-download-and-runtime-management.md) — local lifecycle
- [settings-architecture.md](settings-architecture.md) — persistence and API
