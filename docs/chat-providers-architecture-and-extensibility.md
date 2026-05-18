# Chat Providers — Architecture and Extensibility

Last updated: 2026-05-18

This guide explains how the AntRunner chat stack is structured and how to add a **new chat provider** that plugs into the same execution pipeline as OpenAI (Chat Completions), OpenAI (Responses API), Anthropic, llama.cpp, and the current provider extensions.

Provider status convention used in this document:

- **Stable (operator-supported)**: shipped and documented for normal operator setup.
- **Experimental/Hidden**: implemented in code paths but partial/in-flight and not generally operator-facing.
- **Roadmap**: planned only; not shipped.

---

## 1. High-level architecture

The design separates three concerns:

| Layer | Responsibility |
|--------|----------------|
| **`AntRunner.Chat.Abstractions`** | Provider-neutral contracts and DTOs: requests, responses, streaming chunks, roles, tools. |
| **`AntRunner.Chat` (`ThreadRun`, `ChatRunner`)** | Conversation loop: builds messages and tools from assistant definitions, calls `IChatCompletionClient`, handles tool execution, streaming, retries, and run results. It does **not** know which vendor backs the model. |
| **Provider projects** (`AntRunner.Chat.OpenAI`, `AntRunner.Chat.Anthropic`, `AntRunner.Chat.LlamaCpp`) | Map abstractions ↔ vendor SDKs or HTTP APIs. |
| **`AntRunner.ToolCalling`** | OpenAPI-driven tool execution, assistant definitions, and related types used when `finish_reason` is `tool_calls`. |
| **`GuideAntsApi` (host app)** | **Routing**: picks the correct factory from the model catalog (`Models` table) and configuration. |

Execution always flows: **assistant definition + options → `ThreadRun.ExecuteAsync` → `IChatCompletionClientFactory.CreateClient` → `IChatCompletionClient`**.

---

## 2. Core contracts (`AntRunner.Chat.Abstractions`)

### 2.1 `IChatCompletionClient`

Every provider must implement:

```csharp
Task<ChatCompletionResponse> GetCompletionAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default);
Task<ChatCompletionResponse> StreamCompletionAsync(ChatCompletionRequest request, Action<ChatCompletionChunk> onChunk, CancellationToken cancellationToken = default);
```

- **`GetCompletionAsync`**: Non-streaming completion; must return a full `ChatCompletionResponse` with at least one `ChatChoice`.
- **`StreamCompletionAsync`**: Incremental assistant output via `onChunk`; the **final** return value must still be a complete `ChatCompletionResponse` (the runner merges streamed text and uses the final message like the non-streaming path).

Reference types worth internalizing:

- **`ChatCompletionRequest`**: Messages, optional tools, `Model`, `Temperature` / `TopP`, `ReasoningEffort`, and optional **`SamplingParameters`** (used heavily by llama.cpp for data-driven sampling overrides).
- **`ChatMessage`**: Role (`ChatRole`: System, Developer, User, Assistant, Tool), multimodal **`ChatContent`** (text and/or image URL), optional **`ToolCalls`**, **`ThinkingBlocks`**, and for tool results **`ToolCallId`** / **`FunctionName`**.
- **`ChatCompletionResponse`** / **`ChatChoice`**: Normalized “chat completions shape” with **`FinishReason`** and optional **`ChatCompletionUsage`**.

### 2.2 `IChatCompletionClientFactory`

```csharp
IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null);
string? DefaultDeploymentId { get; }
```

- **`deploymentId`**: In this codebase, this is the **catalog model id** (and/or deployment name depending on provider). The factory configures HTTP clients, API keys, and caches.
- Implementations may return `null` for **`DefaultDeploymentId`** when the deployment is always defined by the assistant/catalog row (see routing).

### 2.3 Finish reasons the runner depends on

`ThreadRun` branches on **`ChatChoice.FinishReason`**:

| Value | Behavior |
|--------|-----------|
| **`stop`** | End the turn loop (subject to evaluator logic). |
| **`tool_calls`** | Execute tools (or pause for client-handled tools). |
| **`length`** | Stop loop. |
| **`function_call`** | Stop loop (legacy path). |

New providers **must** map vendor-specific “stopped because tools” semantics to **`tool_calls`** when the model issued tool calls, and **`stop`** when the model returned final text without tools. Existing implementations set this explicitly when the upstream API uses different names (for example Anthropic maps `StopReason.ToolUse` to `"tool_calls"`).

---

## 3. Execution engine (`AntRunner.Chat`)

### 3.1 Entry points

- **`ChatRunner.RunThread`** — public wrapper that builds **`InvocationContext`** when project/notebook/conversation IDs are present and delegates to **`ThreadRun.ExecuteAsync`**.
- **`ThreadRun.ExecuteAsync`** — loads **`AssistantDefinition`**, merges **`DeploymentId`** with **`assistantDef.Model`**, creates **`IChatCompletionClient`** via the injected factory, builds the message list and **`ChatToolDefinition`** list from the assistant, then runs the **while** loop that calls the model and handles tools.

Key line (factory is external):

```csharp
options.DeploymentId = assistantDef.Model ?? options.DeploymentId ?? clientFactory.DefaultDeploymentId;
var api = clientFactory.CreateClient(options.DeploymentId, httpClient);
```

### 3.2 Streaming vs non-streaming

- If a streaming callback is provided, the runner uses **`StreamCompletionAsync`** and accumulates content; it still appends the **final** assistant message from the returned response to the transcript.
- Streaming treats **`FinishReason == "thinking"`** on a delta as assistant thinking (role surfaced as `assistant_thinking` in the progress event).

### 3.3 Tools

Tool definitions come from the assistant’s **`function`** tools. Tool execution uses **`AntRunner.ToolCalling`** (`ToolCaller`, `DoToolCalls` inside `ThreadRun`). Your provider only needs to emit **`ChatToolCall`** objects on the assistant message and **`finish_reason: "tool_calls"`** when appropriate; the runner handles the rest.

---

## 4. Built-in providers (reference patterns)

### 4.1 OpenAI — Chat Completions (`AntRunner.Chat.OpenAI`)

- **`OpenAiChatClient`** implements **`IChatCompletionClient`** using the OpenAI .NET SDK **`ChatEndpoint`**.
- **`OpenAiChatClientFactory`** builds **`OpenAIClient`** from **`AzureOpenAiConfig`** (used for both OpenAI platform and Azure OpenAI; resource name and API version distinguish Azure).
- Mapping lives in **`OpenAiChatClient.OpenAiMapper`**: converts **`ChatMessage`** (including tool calls and multimodal content) to SDK **`Message`** types and maps responses back.
- **Tool call arguments**: OpenAI expects **`function.arguments`** as a **string**; the mapper normalizes object-shaped arguments (e.g. from other providers) to JSON strings.

### 4.2 OpenAI — Responses API (`AntRunner.Chat.OpenAI`)

- **`OpenAiResponsesClient`** implements **`IChatCompletionClient`** using **`ResponsesEndpoint.CreateModelResponseAsync`**.
- **`OpenAiResponsesMapper`** converts the unified message list into **`IResponseItem`** input items (including tool calls as separate items where required).
- Streaming uses a custom **`ResponsesStreamHandler`** that turns Responses SSE events into **`ChatCompletionChunk`** deltas (text, refusal, reasoning), while preserving the contract that **`StreamCompletionAsync`** returns a full **`ChatCompletionResponse`**.

Use this implementation when the catalog **`Provider`** is **`openai-responses`** or **`azure-openai-responses`**.

### 4.3 Anthropic (`AntRunner.Chat.Anthropic`)

- **`AnthropicChatClient`** uses the Anthropic SDK **`Messages`** API.
- **`AnthropicMapper`** maps system messages, thinking blocks, tool use, and maps **`StopReason.ToolUse`** → **`"tool_calls"`** for the runner.
- **`AnthropicChatClientFactory`** caches **`AnthropicClient`** instances keyed by config signature; **`AnthropicConfig`** supplies API key / auth token, optional base URL, default model, max tokens, and **thinking budgets** tied to **`ReasoningEffort`**.

### 4.4 llama.cpp (`AntRunner.Chat.LlamaCpp`)

- **`LlamaCppChatClient`** talks to a **local HTTP** llama.cpp server (configured via **`LlamaCppConfig`**).
- Uses JSON request bodies, supports **parallel tool calls**, optional **thinking** strip rules, and **`LlamaCppRuntimeProfileData`** (runtime profiles from the host: sampling defaults, system/developer merge, thought-block regex, etc.).
- **`LlamaCppChatClientFactory`** implements **`IChatCompletionClientFactory`** but the API host also uses **`CreateClientForProfile(...)`** when routing **llama-cpp** models so profile data and flags are applied.

---

## 5. Server integration: routing (`GuideAntsApi`)

The library projects are reusable without the host. In **GuideAntsApi**, **`RoutingChatCompletionClientFactory`** is registered as the singleton **`IChatCompletionClientFactory`**.

### 5.1 How the model is chosen

1. **`IChatTargetResolver.Resolve(deploymentId)`** loads **`Models`** row by **`ModelId`** and returns **`ChatTarget`** (`ModelId`, **`Provider`**, **`RuntimeConfigJson`** for runtime-specific model configuration).
2. **`IChatTargetValidator.Validate`** ensures the provider string is known and required configuration keys exist (OpenAI / Azure OpenAI / Anthropic / llama-cpp specifics).
3. **`RoutingChatCompletionClientFactory.CreateClient`** maps **`Provider`** to the correct underlying factory. There is **no silent fallback** to another provider if resolution fails.

### 5.2 Provider strings (catalog, status-aware)

These are the values **`ParseProvider`** and **`ChatTargetValidator.KnownProviders`** expect.

Stable (operator-supported):

| Catalog `Provider` | Backend |
|--------------------|--------|
| `openai-chat` | **`OpenAiChatClientFactory`** + OpenAI platform config |
| `openai-responses` | **`OpenAiResponsesClientFactory`** + OpenAI platform config |
| `azure-openai-chat` | **`OpenAiChatClientFactory`** + Azure OpenAI config |
| `azure-openai-responses` | **`OpenAiResponsesClientFactory`** + Azure OpenAI config |
| `anthropic` | **`AnthropicChatClientFactory`** |
| `llama-cpp` | **`LlamaCppChatClientFactory`** + **`RuntimeConfigJson`** + runtime profile resolution |
| `google-gemini-chat` | **`GoogleGeminiChatClientFactory`** |

Experimental/Hidden (implemented, partial/in-flight, not operator-facing setup guidance):

| Catalog `Provider` | Backend |
|--------------------|--------|
| `hf-inference-chat` | **`HuggingFaceChatClientFactory`** |
| `openrouter-chat` | **`OpenRouterChatClientFactory`** |

These hidden providers may appear in routing/readiness code paths, but should not be treated as fully shipped operator-facing setup.

### 5.3 Keyed DI for OpenAI factories

The host registers **two** factory **types** per credential section (`openai-platform` vs `azure-openai`): one **`OpenAiChatClientFactory`** and one **`OpenAiResponsesClientFactory`**, both keyed with the same string key but **different service types**. **`RoutingChatCompletionClientFactory`** injects each type separately via **`[FromKeyedServices(...)]`**.

---

## 6. Adding a new provider

### Step A — New class library

1. Add a project (e.g. `AntRunner.Chat.YourProvider`) targeting **`net8.0`** (match the rest of the stack).
2. Reference **`AntRunner.Chat.Abstractions`**.
3. Implement **`IChatCompletionClient`** with correct mapping for **`ChatCompletionRequest`** → vendor API and vendor response → **`ChatCompletionResponse`**.
4. Implement **`IChatCompletionClientFactory`** (or a dedicated factory type if you need extra parameters, similar to **`CreateClientForProfile`** for llama.cpp).

**Quality bar:** Support both **`GetCompletionAsync`** and **`StreamCompletionAsync`** unless you can prove the host never streams for your scenario (the runner always uses streaming when a stream handler exists).

### Step B — Align semantics with `ThreadRun`

- **Roles**: Map system/developer/user/assistant/tool consistently; tool result messages must carry **`ToolCallId`** and function name where your API requires it.
- **Tools**: Expose function tools using **`ChatToolDefinition`** / **`ChatFunctionDefinition`**; populate **`ChatToolCall`** on assistant messages; set **`finish_reason`** to **`tool_calls`** when the model requests tools.
- **Usage**: Populate **`ChatCompletionUsage`** when the vendor exposes token counts (helps **`ChatUsage`** and billing-related features).

### Step C — Wire into the host (GuideAntsApi)

1. Register your factory in DI (singleton or keyed singleton, depending on how you split credentials).
2. Extend **`RoutingChatCompletionClientFactory`**: add a **`Provider`** case in **`ParseProvider`** and dispatch to your factory.
3. Extend **`ChatTargetValidator.KnownProviders`** and **`Validate`** with required configuration keys for your provider.
4. Add or document the new **`Provider`** value for the **Models** catalog (and any migrations), so assistants resolve to your implementation.

### Step D — Tests

Follow patterns in **`RoutingChatCompletionClientFactoryTests`**: resolve targets, validate configuration, and assert the correct client type or behavior for each **`Provider`** string.

---

## 7. File map (quick reference)

| Area | Location |
|------|----------|
| Contracts | `src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/` |
| Run loop | `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`, `ChatRunner.cs` |
| OpenAI Chat | `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiChatClient.cs` |
| OpenAI Responses | `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiResponsesClient.cs` |
| Anthropic | `src/server/AntRunner.Chat/AntRunner.Chat.Anthropic/AnthropicChatClient.cs` |
| llama.cpp | `src/server/AntRunner.Chat/AntRunner.Chat.LlamaCpp/LlamaCppChatClient.cs` |
| Routing | `src/server/GuideAntsApi/Services/Conversations/RoutingChatCompletionClientFactory.cs` |
| Target resolution | `src/server/GuideAntsApi/Services/Routing/IChatTargetResolver.cs` |
| DI registration | `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` |

---

## 8. Principles to preserve

1. **Single abstraction at the runner boundary** — `ThreadRun` should not branch on provider name; only **`IChatCompletionClient`** behavior should differ.
2. **No provider fallback in routing** — a resolved catalog row and validation either dispatch to the configured backend or fail with a **`RoutingException`** (see comments in **`RoutingChatCompletionClientFactory`**).
3. **Normalized finish reasons** — map vendor stop reasons to **`stop`** / **`tool_calls`** / **`length`** so tool loops and evaluators behave predictably.

This should be enough to implement a new backend, register it, and expose it through the model catalog without changing the core conversation engine.
