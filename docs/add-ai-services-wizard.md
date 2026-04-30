# Add AI Services Wizard

Last updated: 2026-04-30

This document describes the **as-built** Home onboarding wizard behavior.

## 1. Purpose

The wizard helps operators reach a minimally usable AI setup by guiding:

- provider connection setup,
- initial chat model creation,
- optional non-chat service setup.

It complements Settings; it does not replace Settings ownership boundaries.

## 2. Entry points

- Automatic on Home (`/`) during first-launch predicate checks.
- Manual from Home header button (`Setup Wizard`).

## 3. Auto-open predicate (as built)

Home checks:

- `GET /api/settings/sections`
- `GET /api/settings/models`

Wizard opens when:

- configured connection sections count is zero, **or**
- catalog model count is zero.

Configured connections use `CONNECTION_SECTION_NAME_SET` and `readinessStatus === configured`.

Current connection section set is derived from `connectionSections.ts` and includes:

- `AzureOpenAI`, `OpenAI`, `Anthropic`, `GoogleGeminiApi`, `OpenRouter`, `HuggingFace`
- `AzureSpeechService`, `AzureOpenAiImages`, `AzureOpenAiEmbedding`, `AzureDocumentIntelligence`

Auto-open is skipped when dismissal key exists:

- `guideants.firstLaunch.addAiServicesWizard.dismissed.v1`

If probe calls fail, auto-open is skipped (non-blocking behavior).

## 4. Providers and steps

Provider options (current):

- `foundry` (`Microsoft Foundry`)
- `google-gemini` (`Google Gemini`)

Step sequence (current):

1. `Provider`
2. `Connection details`
3. `Models`
4. `Optional services`
5. `Finish`

## 5. Footer and dismissal behavior

Footer actions are always visible:

- `Not now`
- `Configure manually`
- `Back`
- `Next`
- `Finish`

Behavior:

- `Finish` on non-final step persists current step and jumps to `Finish` step.
- Wizard closes only when `Finish` is clicked on `Finish` step.
- Overlay click dismissal is disabled.
- Checkbox persists dismissal preference for future auto-open behavior.

## 6. Provider-specific notes

### Microsoft Foundry path

- Core section: `AzureOpenAI`
- Optional service sections: `AzureOpenAiEmbedding`, `AzureOpenAiImages`, `AzureSpeechService`, `AzureDocumentIntelligence`
- Model provider labels map to:
  - `Completions` -> `azure-openai-chat`
  - `Responses` -> `azure-openai-responses`

### Google Gemini path

- Core section: `GoogleGeminiApi`
- Default chat model id prefill: `gemini-2.5-flash`
- Optional service defaults include Gemini model ids/timeouts and TTS voice defaults.

## 7. Extension guide

When extending wizard behavior:

1. Keep step/provider constants in `components/home/addAiServicesWizard/constants.ts` authoritative.
2. Keep first-launch predicate aligned with `CONNECTION_SECTION_NAME_SET` and model-count logic in `Home.tsx`.
3. Reuse existing Settings APIs/contracts; do not introduce parallel configuration ownership.
4. Add tests for:
   - predicate matrix,
   - provider/step persistence,
   - finish/dismiss behavior,
   - dismissal key persistence.

## 8. Related docs

- Operator setup: [setup-guide.md](setup-guide.md)
- Settings architecture: [settings-page-provider-model-llama-redesign.md](settings-page-provider-model-llama-redesign.md)
- Default chat model behavior: [default-chat-models.md](default-chat-models.md)
- Requirements baseline: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
