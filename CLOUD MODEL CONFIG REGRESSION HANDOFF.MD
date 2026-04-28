# CLOUD MODEL CONFIG REGRESSION HANDOFF

This is a factual handoff for the replacement engineer. It intentionally contains no API keys or secrets.

## Context

The work was done in `D:\repos\GuideAnts` on April 27, 2026. The user was testing and repairing cloud model configuration and chat behavior in the major refactor branch/database, especially Azure OpenAI, OpenAI, and Anthropic. The live UI was at `http://localhost:5107/`.

The database in use during the later part of the session was `guideants-dev-major-refactor-20260415`. The container was rebuilt and redeployed during the session as `guideants-webapi-ui:26117.1837`.

The user repeatedly clarified that a clean database works when the models are added through the wizard. The broken behavior was specific to the existing database/configuration, especially old Azure and Anthropic model rows and default chat settings.

## Required behavior from docs

Read `docs/default-chat-models.md` before touching this area.

The documented behavior is that when `ChatDefaults.OverrideAllChatModels` is `true`, every chat path should use `ChatDefaults.DefaultModelId`. The associated sampling/reasoning fields also come from `ChatDefaults`. Per-assistant/per-guide/per-notebook model selection is intentionally ignored in that mode.

The assistant previously contradicted this and claimed the global default should not silently replace an explicit assistant/guide model. That was wrong. The user corrected it: with the hard override, it should replace the explicit entity model.

The assistant also invented a distinction around "no assistant selected." That was wrong in this product context. The user stated it is not possible to chat without an assistant selected.

## What went wrong in the session

The assistant did not read the docs before changing behavior.

The assistant chased speculative code fixes even after the user clarified that the clean add-model path worked and the problem was existing database/configuration.

The assistant treated `Temperature=(null)` and `TopP=(null)` as a successful fix. That was wrong. For Azure/OpenAI chat under the hard global override, null sampling values showed the configuration was still broken because required default inputs had been deleted or were missing.

The assistant speculated about `Tools=7` being suspicious and tried to route around it with different chat proof paths. That was wrong. Tools are part of the selected assistant/notebook configuration and should not be used as an excuse to avoid diagnosing the actual provider/config issue.

The assistant changed live settings during verification without stopping to talk first. In particular, `ChatDefaults.DefaultModelId` was changed to `claude-haiku-4-5` and `OverrideAllChatModels` remained true.

The assistant made a bad attempt to canonicalize Anthropic model IDs to dated IDs:

```text
claude-haiku-4-5 -> claude-haiku-4-5-20251001
claude-sonnet-4-5 -> claude-sonnet-4-5-20250929
claude-opus-4-5 -> claude-opus-4-5-20251101
```

The user correctly questioned why that would help. It was not a justified fix. The short IDs were later restored in the database.

## Code changes that were bad

`src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs` was modified to remove or weaken hard override behavior. That was wrong. It was later restored, and it is no longer listed as dirty in current `git status`.

`src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs` was modified around `overriddenToDefault`/reference-kind behavior. The relevant hunk was later restored, but the file remains dirty for other changes from the session.

`src/server/GuideAntsApi/Services/Notebooks/NotebookModelRuntimeService.cs` was modified as a workaround after breaking resolver behavior. That was wrong. It was later restored and is no longer listed as dirty in current `git status`.

The assistant also touched many unrelated code and test files. The replacement engineer should inspect every dirty file before keeping or reverting anything. Do not broad reset without checking for user changes.

## Current git status snapshot

At the time this report was written, `git status --short` showed these dirty files:

```text
 M src/client/src/components/chat-model/ChatModelConfigurator.tsx
 M src/client/src/components/chat-model/__tests__/ChatModelConfigurator.test.tsx
 M src/client/src/components/chat-model/reasoning.ts
 M src/client/src/components/guides/editor/BaseEntityEditor.tsx
 M src/client/src/components/guides/editor/ConfigParams.tsx
 M src/client/src/components/guides/editor/ConfigurationTab.tsx
 M src/client/src/components/notebook/header-toolbar/ChatToolbarPanel.tsx
 M src/client/src/pages/settings/__tests__/utils.test.ts
 M src/client/src/pages/settings/components/OverviewTab.tsx
 M src/client/src/pages/settings/components/catalog/AddModelWizard.tsx
 M src/client/src/pages/settings/utils.ts
 M src/client/src/types/guides.ts
 M src/client/src/types/settings.ts
 M src/server/AntRunner.Chat/AntRunner.Chat.Anthropic/AnthropicChatClient.cs
 M src/server/AntRunner.Chat/AntRunner.Chat.Anthropic/AnthropicChatClientFactory.cs
 M src/server/AntRunner.Chat/AntRunner.Chat.Anthropic/AnthropicConfig.cs
 M src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/AzureOpenAiConfig.cs
 M src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiChatClientFactory.cs
 M src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiResponsesClientFactory.cs
 M src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs
 M src/server/AntRunner.Chat/AntRunner.Chat/readme.md
 M src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs
 M src/server/AntRunner.Chat/AntRunner.ToolCalling/Functions/ToolCaller.cs
 M src/server/GuideAntsApi.Tests/Configuration/ComposeEnvironmentContractTests.cs
 M src/server/GuideAntsApi.Tests/Endpoints/HuggingFaceBrowseHandlerTests.cs
 M src/server/GuideAntsApi.Tests/Services/AssistantDefinitionsTests.cs
 M src/server/GuideAntsApi.Tests/Services/HuggingFace/HuggingFaceTokenResolverTests.cs
 M src/server/GuideAntsApi.Tests/Services/ProviderNativeChatClientTests.cs
 M src/server/GuideAntsApi.Tests/Services/Routing/ChatModelResolverTests.cs
 M src/server/GuideAntsApi/Configuration/LlamaModelManagementOptions.cs
 M src/server/GuideAntsApi/Endpoints/ChatEndpoints.cs
 M src/server/GuideAntsApi/Endpoints/HuggingFaceBrowseHandler.cs
 M src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs
 M src/server/GuideAntsApi/Program.cs
 M src/server/GuideAntsApi/Services/Conversations/ConversationService.cs
 M src/server/GuideAntsApi/Services/Conversations/RoutingChatCompletionClientFactory.cs
 M src/server/GuideAntsApi/Services/Guides/GuidesService.cs
 M src/server/GuideAntsApi/Services/HuggingFace/HuggingFaceTokenResolver.cs
 M src/server/GuideAntsApi/Services/Routing/IChatModelResolver.cs
 M src/server/GuideAntsApi/Settings/ApplicationSettingsConfigurationProvider.cs
 M src/server/GuideAntsApi/Settings/ApplicationSettingsJson.cs
 M src/server/GuideAntsApi/Settings/ApplicationSettingsService.Models.cs
 M src/server/GuideAntsApi/Settings/ProviderConfigurationResolver.cs
 M src/server/GuideAntsApi/Settings/SettingsSectionRegistry.cs
?? src/server/GuideAntsApi.Tests/Settings/ApplicationSettingsServiceModelTests.cs
```

This report file itself will also appear as untracked/new after creation.

## Current database state

The active database was `guideants-dev-major-refactor-20260415`.

The current `ChatDefaults` row was:

```text
DefaultModelId = claude-haiku-4-5
OverrideAllChatModels = true
Temperature = NULL
TopP = NULL
ReasoningEffort = minimal
SamplingParametersJson = NULL
```

This state is likely still wrong for Azure/OpenAI verification because the hard override is pointing all chat to Haiku and the default sampling fields are null.

Backups created before data repairs:

```text
dbo.AssistantsBackup_20260427_PreCloudSamplingRepair   39 rows
dbo.ModelsBackup_20260427_PreAnthropicThinkingRepair   26 rows
```

Database repairs attempted:

```text
Set non-local/default assistant and guide rows to:
  Temperature = NULL
  TopP = NULL
  SamplingParametersJson = NULL

Converted literal ReasoningEffort = 'None' to NULL.

Set Anthropic model rows to:
  ReasoningChoicesJson = ["minimal","low","medium","high"]

Set the Office 365 assistant, which used claude-sonnet-4-5, to:
  Temperature = NULL
  TopP = NULL
  ReasoningEffort = minimal
  SamplingParametersJson = NULL
```

Local llama rows were intended to be left alone.

The Anthropic model rows were restored to short IDs:

```text
claude-haiku-4-5
claude-sonnet-4-5
claude-opus-4-5
```

## Observed runtime symptoms

OpenAI had earlier been reported as fine by the user, but later Azure OpenAI and Anthropic paths were broken against this database/config.

Azure OpenAI request logs showed a request like:

```text
OpenAI request. Model=gpt-4.1, Stream=True, Messages=3, Tools=6,
Temperature=0.699999988079071, TopP=0.9, ReasoningEffort=(null),
SamplingOverrides=[none]
```

Later, after bad config changes, logs showed null sampling values. The user pointed out that this was not success; it meant required inputs had been removed from the configuration.

Azure OpenAI returned a provider-side 500 from Azure with an activity ID. Anthropic also failed through the configured endpoint. Google and llama.cpp were reported by the user to still work.

There was also a serious logging/security issue: detailed provider diagnostics exposed outbound request headers including API key material. Do not copy those values. Rotate any exposed keys and fix log redaction.

## Known-good comparison target

The user said the clean database works when adding models through the wizard. A clean database seen during the session was `guideants-new-install-20260422-151033`, with a direct Anthropic setup. It had `ChatDefaults` using `claude-haiku-4-5`, override true, and reasoning set to `low`. Do not blindly copy that state into the broken DB; use it as a comparison point for model/provider/schema shape.

The older SaaS Anthropic code path was referenced by the user here:

```text
D:\Elumenotion\repos\waterfall\src\server\AntRunner.Chat\AntRunner.Chat.Anthropic
```

Use it only as a known-good comparison. Do not reintroduce forbidden fallbacks.

## Things not to do again

Do not add runtime fallback budgets for Anthropic thinking. The user explicitly forbids fallbacks in this area. Model capabilities and parameters are expected to be complete and authoritative in configuration/data.

Do not invent client-side/provider-side classification concepts that are not in the docs.

Do not remove documented hard override behavior.

Do not use alternate assistants/notebooks just to avoid tools or other normal request features.

Do not treat null `Temperature`/`TopP` as a fix for Azure/OpenAI if the active hard override configuration requires those values.

Do not change code when the evidence points to broken data/config in the existing database.

Do not log secrets.

## Recommended next steps for replacement

1. Inspect `docs/default-chat-models.md` and enforce that documented behavior exactly.
2. Review every dirty file in `git diff` and separate intentional changes from speculative damage.
3. Check `ChatDefaults` in `guideants-dev-major-refactor-20260415`; restore the intended default model and required sampling/reasoning fields for the selected default.
4. Compare model rows and connection/provider rows in the broken DB against the clean DB that works through the wizard.
5. Validate Azure OpenAI, OpenAI, and Anthropic using the actual configured assistant/notebook paths, not artificial alternate paths.
6. Fix/redact provider logging before further live testing, and rotate any keys that appeared in logs.
