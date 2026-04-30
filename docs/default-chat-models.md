# Default Chat Models

Last updated: 2026-04-30

This document describes the shipped default-chat behavior:
instance-wide default model, optional override-all behavior, and resolver integration.

## 1. Behavior

- `defaultModelId`: instance default chat catalog model.
- `overrideAllChatModels`:
  - `true`: all chat turns route to default model.
  - `false`: entity `modelId` is used when set; empty/omitted model uses default.

Sampling overrides from chat defaults apply for default/override paths.

## 2. Resolver seam

- `IChatModelResolver` is the canonical seam for effective chat model resolution.
- `IChatTargetResolver` and `IChatTargetValidator` handle target resolution and execution validation.

This separation is required to keep default behavior explicit and testable.

## 3. Supported chat provider ids

The current validated chat provider set is:

- `openai-chat`
- `openai-responses`
- `azure-openai-chat`
- `azure-openai-responses`
- `anthropic`
- `llama-cpp`
- `google-gemini-chat`
- `hf-inference-chat`
- `openrouter-chat`

## 4. Settings and UI surfaces

- `GET/PUT /api/settings/chat-defaults`
- `Settings -> Overview` default chat model controls
- Guide/assistant editor support for "Use Default Model"

## 5. Wizard integration

`Home -> Add AI Services Wizard` can set the first/added model as default chat model.
Wizard behavior details: [add-ai-services-wizard.md](add-ai-services-wizard.md)

## 6. Related docs

- Setup: [setup-guide.md](setup-guide.md)
- Settings architecture: [settings-page-provider-model-llama-redesign.md](settings-page-provider-model-llama-redesign.md)
- Requirements: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
