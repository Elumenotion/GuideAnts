# Default Chat Models (R-9)

This document describes the **global default chat model** feature: one instance-wide default catalog model (cloud or `llama-cpp`), an optional **hard override** that forces every chat turn to use that default, and the shared UI plus server resolver seam.

## Semantics

- **Hard override (`OverrideAllChatModels`)** — When **on**, every chat path uses the configured default **catalog model id** and the **sampling fields stored in `ChatDefaults`**. Per-entity `modelId` on guides/assistants is ignored for routing.
- **Override off** — Per-entity `modelId` is respected when set. If the entity uses **Use Default Model** (stored as omitted / empty `modelId`), the effective model is **`ChatDefaults:DefaultModelId`** and sampling overrides from `ChatDefaults` apply for that resolution.

## Server seam

- **`IChatModelResolver`** (`GuideAntsApi.Services.Routing`) — Single seam (R-1.6) used from conversation streaming, published conversations, `Agent.Invoke`, and conversation creation. Returns `ResolvedChatModel` with:
  - **`ModelId`** — Catalog model id string for routing.
  - **`ReferenceKind`** — `Direct` | `DefaultedTo` | `OverriddenToDefault` (R-9.2).
  - **`OverrideTemperature` / `OverrideTopP` / `OverrideReasoningEffort`** — Applied in `ThreadRun` when resolution is not `Direct` (from `ChatDefaults` configuration).
- **`POST /api/chat/run-thread/{assistantName}`** now resolves the effective deployment through `IChatModelResolver` before invoking `ChatRunner`, and fills missing sampling overrides from the resolved default model settings.
- **Evaluator continuity.** When `ThreadRun` performs evaluator invocations, it now forwards the resolved deployment id and override knobs so evaluator calls follow the same default/override semantics as the primary run.

- **Readiness** — `GET` chat-target readiness populates `referenceKind` on `ChatTargetReadinessDto` (`direct` | `defaultedTo` | `overriddenToDefault`).

## Persistence

- Application settings section **`ChatDefaults`** (JSON blob, row-versioned). Exposed to the client as **`GET/PUT /api/settings/chat-defaults`** (typed DTOs) in addition to the generic section APIs.

## UI

- **`ChatModelConfigurator`** — Shared component for Guide Builder (`mode="entity"`) and Settings Overview default card (`mode="default"`).
- **Guide Builder** — Model selector includes **Use Default Model (from Settings)** (`modelId` stored as empty / omitted). When the entity uses the default or when hard override is on, per-entity sampling controls are disabled with an explanatory hint.
- **Settings → Overview** — **Default Chat Model** card: configurator + override toggle + Save (concurrency via `rowVersion`).

## Local Llama (`llama-cpp`)

Choosing a `llama-cpp` catalog model as the global default does not change the admin protocol. **R-12** load orchestration in `NotebookModelRuntimeService` still applies when a chat turn resolves to that model.

## Migration note

Previous builds used a silent **`gpt-4.1`** string fallback in some assistant/template paths. That fallback is removed; unresolved model configuration surfaces as a **routing / readiness error** instead of a hidden default.
