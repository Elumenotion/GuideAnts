# Model Parameter Override Fix Plan (Revised)

## Problem Summary

When `ChatDefaults:OverrideAllChatModels` is enabled and the default model is a Responses model (for example `gpt-5`), agent invocation can still send assistant-level typed sampling fields (`temperature`, `top_p`) that should have been ignored. This causes provider errors such as:

- `Unsupported parameter: 'temperature' is not supported with this model.`

The current implementation also does not carry global sampling warpers (`SamplingParametersJson`) through model resolution, so global model policy is incomplete.
Additionally, execution still relies on fallback/merge behavior that can mask defects.

---

## Current Behavior (Code Evidence)

### 1) Resolver returns only typed overrides, not sampling warpers

- `ResolvedChatModel` only contains `OverrideTemperature`, `OverrideTopP`, `OverrideReasoningEffort`:
  - `src/server/GuideAntsApi/Services/Routing/IChatModelResolver.cs:25`
  - `src/server/GuideAntsApi/Services/Routing/IChatModelResolver.cs:26`
  - `src/server/GuideAntsApi/Services/Routing/IChatModelResolver.cs:27`
- `ChatModelResolver` reads `ChatDefaults:Temperature`, `TopP`, `ReasoningEffort` but does not read `ChatDefaults:SamplingParametersJson`:
  - `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:37`
  - `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:38`
  - `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:39`
  - `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:53`
  - `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:54`
  - `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:55`
- But `ChatDefaults` schema/DTO includes `SamplingParametersJson`:
  - `src/server/GuideAntsApi/Models/Settings/SettingsDtos.cs:161`
  - `src/server/GuideAntsApi/Models/Settings/SettingsDtos.cs:174`
  - `src/server/GuideAntsApi/Settings/SettingsSectionRegistry.cs:446`

### 2) Execution path mutates assistant definition with override fields

- `ThreadRun` mutates the loaded assistant definition in-place:
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:160`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:162`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:165`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:167`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:170`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:172`
- Request is then built from assistant fields (including typed params):
  - `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs:293`
- Responses client forwards typed params directly:
  - `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiResponsesClient.cs:170`
  - `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiResponsesClient.cs:173`
  - `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiResponsesClient.cs:174`

### 3) Override fields are propagated from many entry points

- `ChatRunOptions` carries typed override fields:
  - `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs:56`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs:58`
  - `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs:60`
- These fields are set in:
  - `src/server/GuideAntsApi/Services/Conversations/Agent.cs:80`
  - `src/server/GuideAntsApi/Services/Conversations/Agent.cs:81`
  - `src/server/GuideAntsApi/Services/Conversations/Agent.cs:82`
  - `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs:1772`
  - `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs:1773`
  - `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs:1774`
  - `src/server/GuideAntsApi/Services/Conversations/ConversationManager.cs:44`
  - `src/server/GuideAntsApi/Services/Conversations/ConversationManager.cs:45`
  - `src/server/GuideAntsApi/Services/Conversations/ConversationManager.cs:46`
  - `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs:472`
  - `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs:473`
  - `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs:474`
  - `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs:858`
  - `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs:859`
  - `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs:860`
  - `src/server/GuideAntsApi/Endpoints/ChatEndpoints.cs:51`
  - `src/server/GuideAntsApi/Endpoints/ChatEndpoints.cs:55`
  - `src/server/GuideAntsApi/Endpoints/ChatEndpoints.cs:59`

### 4) Local/runtime parameter projection predicate is too broad

- DB materialization includes typed params and sampling json when `AssistantUsesLocalRuntime` is true:
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs:300`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs:302`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs:308`
- Predicate currently returns true for:
  - provider `llama-cpp`, **or**
  - any non-empty `Model.RuntimeConfigJson`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs:314`
  - `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs:317`
- But cloud models may also carry `RuntimeConfigJson` (runtime profile linkage):
  - `src/server/GuideAntsApi.DataModel/Models/Model.cs:50`

### 5) Intended warper system exists and works for llama path

- Assistant-level data-driven warpers are persisted:
  - `src/server/GuideAntsApi.DataModel/Models/Assistant.cs:80`
  - `src/server/GuideAntsApi/Services/Guides/GuidesService.cs:1293`
  - `src/server/GuideAntsApi/Services/Guides/GuidesService.cs:1549`
- Assistant definition hydrates `SamplingParameters`:
  - `src/server/AntRunner.Chat/AntRunner.Chat/AssistantUtility.cs:121`
  - `src/server/AntRunner.Chat/AntRunner.Chat/AssistantUtility.cs:126`
- Llama client merges profile defaults + request sampling overrides:
  - `src/server/AntRunner.Chat/AntRunner.Chat.LlamaCpp/LlamaCppChatClient.cs:509`
  - `src/server/AntRunner.Chat/AntRunner.Chat.LlamaCpp/LlamaCppChatClient.cs:519`
  - `src/server/AntRunner.Chat/AntRunner.Chat.LlamaCpp/LlamaCppChatClient.cs:521`

---

## Non-Negotiable Requirements

1. Authority is binary only:
- `GlobalOverride`
- `AssistantDefinition`
2. No third authority state (for example `DefaultApplied`) is allowed.
3. No runtime fallback in parameter resolution:
- If authority is `GlobalOverride`, assistant parameters must not be merged.
- If authority is `AssistantDefinition`, `Use default` is valid and must resolve via configured default model; fail-fast only when the effective model/policy cannot be resolved or is invalid.
4. Parameter source for `AssistantDefinition` must be explicit and deterministic:
- Assistant explicitly selects a model: use that model's configured parameter bag.
- Assistant is set to `Use default`: use the configured default model's parameter bag (for example `ChatDefaults` model params and `SamplingParametersJson`).
- This is inheritance within `AssistantDefinition` authority, not `GlobalOverride`.
5. Parameter decisioning must be based on resolved model configuration/runtime profile, not deployment location labels (`cloud`/`local`).
6. Execution must consume one resolved parameter bag, not ad-hoc named override knobs.
7. Mutation of cached/shared assistant definitions is prohibited.
8. Evaluator path must consume the exact same resolved policy as the primary run.

---

## Revised Design Principles

1. Runtime control uses explicit binary authority, not inferred provenance.
2. Execution consumes a single resolved parameter policy object.
3. Execution is a deterministic projection layer from resolved policy/config to provider request.
4. Location labels are not a valid decision input for parameter support.
5. Assistant-definition mutation is treated as a correctness bug (P1).
6. No compatibility shim behavior in execution logic.

---

## Proposed Changes

## A) Introduce an explicit resolved execution policy object (fully specified)

Use one canonical structure for execution-time model parameters:

```csharp
public enum ParameterAuthority
{
    AssistantDefinition,
    GlobalOverride
}

public sealed record ResolvedExecutionPolicy(
    string ModelId,
    string Provider,
    ParameterAuthority Authority,
    IReadOnlyDictionary<string, JsonElement> Parameters
);
```

Notes:
- `Parameters` is the single runtime source-of-truth for model knobs.
- Runtime execution should not branch on `ReferenceKind`; provenance can remain for telemetry/UI only.
- `ParameterAuthority` must never include a third mode.

Files:
- `src/server/GuideAntsApi/Services/Routing/IChatModelResolver.cs`
- `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs`

Changes:
- Resolver returns `ResolvedExecutionPolicy` (or equivalent) with explicit `Authority`.
- Parse and include `ChatDefaults:SamplingParametersJson`.
- Preserve `ReferenceKind` only for diagnostics/UX, not execution control.
- If authority would otherwise require a synthetic fallback state, resolver must fail instead.
- Define parameter-source resolution rules in resolver contract:
  - `GlobalOverride`: model + parameter bag from global override settings.
  - `AssistantDefinition` + explicit assistant model: parameter bag from that model definition.
  - `AssistantDefinition` + `Use default`: model + parameter bag from configured default model settings.

## B) ThreadRun must not mutate assistant definition

Files:
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`

Changes:
- Stop in-place assignments to `assistantDef.Temperature/TopP/ReasoningEffort/SamplingParameters`.
- Build request parameters from local policy data only.
- If `Authority` is `GlobalOverride`, assistant-level params must not participate.
- Ensure evaluator path reuses the same resolved policy (currently copied at `ThreadRun.cs:440-442`).

## C) Replace typed override plumbing with policy plumbing

Files:
- `src/server/AntRunner.Chat/AntRunner.Chat/ChatRunOptions.cs`
- `src/server/GuideAntsApi/Services/Conversations/Agent.cs`
- `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs`
- `src/server/GuideAntsApi/Services/Conversations/ConversationManager.cs`
- `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs`
- `src/server/GuideAntsApi/Endpoints/ChatEndpoints.cs`

Changes:
- Remove/retire `OverrideTemperature`, `OverrideTopP`, `OverrideReasoningEffort`.
- Pass resolved execution policy (including parameter bag and authority mode) to execution.
- Do not introduce compatibility shims in execution behavior.

## D) Fix runtime classification for assistant param projection

Files:
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`

Changes:
- Remove location-based inference from parameter projection decisions.
- Do not infer parameter semantics from deployment location (for example `cloud` vs `local`) or from `RuntimeConfigJson` presence alone.
- Drive projection by the resolved model configuration/runtime profile contract, with tests to prevent regressions.

## E) Deterministic request shaping from resolved parameter bag (required)

Files:
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiResponsesClient.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiChatClient.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat.LlamaCpp/LlamaCppChatClient.cs`

Changes:
- Do not implement ad-hoc runtime capability maps or location heuristics.
- Treat configuration as source-of-truth for allowed parameter keys/warpers per model/runtime profile.
- Execution projects only the resolved parameter bag into provider request fields for the selected adapter.
- If configuration is invalid for a provider/model, surface provider/config error directly (no silent fallback/auto-rewrite).
- Keep shaping logic deterministic and identical across primary and evaluator paths.

---

## Suggested Implementation Order (Revised)

1. Hotfix: stop `ThreadRun` mutation of cached assistant definitions (clone before modify).  
2. Add resolved execution policy with binary authority and wire it through all entry points (main + evaluator).  
3. Replace request shaping in `ThreadRun` and provider clients to project from resolved policy bag only.  
4. Tighten `AssistantUsesLocalRuntime` with regression tests.  
5. Remove old override-field plumbing and dead merge code.  

---

## Test Cases to Add/Update

1. `OverrideAllChatModels=true`, default model `gpt-5` (responses), agent invocation:
- outbound request must omit unsupported typed params unless explicitly allowed by policy.

2. Global override with warper bag:
- resolved policy carries `SamplingParametersJson` and execution uses it.

3. Assistant-definition authority (global override disabled):
- assistant-level params apply.
- assistant may explicitly use default model; runtime must resolve both model id and parameter bag from configured default model settings.
- this inheritance is `AssistantDefinition` authority behavior, not `GlobalOverride`.

4. No assistant-definition mutation across runs:
- cached assistant object remains unchanged after execution.

5. Parameter projection classification:
- models with `RuntimeConfigJson` are not automatically assigned parameter behavior by location; behavior is config-contract-driven.

6. Assistant set to `Use default` with global override disabled:
- resolves to configured default model policy; fail-fast only if default model/policy is missing or invalid.

7. Evaluator parity:
- evaluator run receives identical resolved execution policy and cannot reintroduce unsupported parameters.
