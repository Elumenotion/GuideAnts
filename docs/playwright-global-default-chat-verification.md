# Playwright Global Default Chat Verification

## Purpose

This document describes how Playwright was used to verify the cloud chat path for Azure OpenAI and Anthropic models when each model is selected as the global default.

The test target was the real notebook chat UI. The purpose was not to unit test provider clients or inspect settings pages. The purpose was to exercise the same path a user hits when they type into a notebook chat composer while `ChatDefaults.OverrideAllChatModels=true`.

## System Path Under Test

The Playwright test exercised this end-to-end path:

1. Set `ChatDefaults` to a specific model.
2. Open the notebook UI in a browser.
3. Create a new conversation from the left conversation list.
4. Type a prompt into the visible notebook chat composer.
5. Click the visible `Send` button.
6. Wait for the assistant response to stream into the UI.
7. Confirm the response came from the model selected as the global default.

This verifies the following system behavior together:

- `ChatDefaults` global override behavior
- notebook conversation creation
- chat model resolver
- provider router
- provider-specific request construction
- streaming response handling
- persistence to conversation turn/message tables
- rendered assistant response in the browser

## Tooling

Playwright was run through the CLI:

```powershell
npx --yes --package @playwright/cli playwright-cli ...
```

The browser was pointed at the already-running local stack:

```text
http://localhost:5107
```

The tested notebook was:

```text
Project:  133707E1-7C82-49A4-9395-57E25E22376A
Notebook: 704517DF-7CCA-4DDE-AA57-EDE87C1E588B
Page:     /projects/133707E1-7C82-49A4-9395-57E25E22376A/notebooks/704517DF-7CCA-4DDE-AA57-EDE87C1E588B
```

Screenshots were written to:

```text
output/playwright/
```

## Browser Flow

For each model, the browser flow was:

1. Update `ChatDefaults` through the application API from the active browser page.
2. Click the left-pane `Add new link` button in the Conversations section.
3. Fill the `Create Conversation` modal title with a unique model-specific title.
4. Click the modal `Create` button.
5. Wait until the new conversation appears in the conversation list.
6. Fill the visible compose textbox.
7. Click `Send`.
8. Wait until the exact expected token is visible in the page.
9. Capture a full-page screenshot.

The UI selectors used were role/label based:

```javascript
await page.getByRole('button', { name: 'Add new link' }).click();
await page.getByPlaceholder('Enter conversation title').fill(title);
await page.getByRole('button', { name: 'Create', exact: true }).click();
await page.getByRole('button', { name: title }).waitFor({ timeout: 30000 });
await page
  .getByRole('group', { name: 'Compose message' })
  .getByRole('textbox')
  .fill(prompt);
await page.getByRole('button', { name: 'Send' }).click();
await page.getByText(token, { exact: true }).waitFor({ timeout: 240000 });
```

## ChatDefaults Setup Per Test

Each test case set the model as the global default and enabled override mode:

```javascript
const current = await page.evaluate(async () =>
  await fetch('/api/settings/chat-defaults').then(r => r.json()));

const body = {
  defaultModelId: item.id,
  overrideAllChatModels: true,
  temperature: item.temperature,
  topP: item.topP,
  reasoningEffort: item.reasoningEffort,
  samplingParametersJson: item.samplingParametersJson,
  rowVersion: current.rowVersion
};

const result = await page.evaluate(async (body) => {
  const res = await fetch('/api/settings/chat-defaults', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });

  return { ok: res.ok, status: res.status, text: await res.text() };
}, body);

if (!result.ok) {
  throw new Error(`settings update failed ${item.id} ${result.status}: ${result.text}`);
}
```

The settings were intentionally changed through the running application API rather than by directly editing the DOM. The subsequent chat message was sent through the real browser UI.

## Prompt Design

Each prompt asked the model to return a unique exact token:

```text
Reply with exactly this token and no other text: <TOKEN>
```

Token format:

```text
GA_MATRIX_<MODEL_ID>_<RUN_ID>_OK
```

Examples:

```text
GA_MATRIX_GPT_4_1_MATRIX_1777332926572_OK
GA_MATRIX_GPT_5_CHAT_MATRIX3_1777333596710_OK
GA_MATRIX_CLAUDE_OPUS_4_5_MATRIX5_1777334594594_OK
```

The unique token was important because the notebook page contains a conversation history. Waiting for generic assistant output would be too weak; waiting for a fresh exact token prevents stale messages from satisfying the browser assertion.

## False Positive Avoidance

The first automation attempt created conversations through the API and navigated directly to them. That was rejected as insufficient because the database did not show the expected turn/message rows for those conversations. The final method used the UI to create the conversation and send the message.

The final method avoided false positives by requiring all of the following:

- unique conversation title per model/run
- unique exact token per model/run
- Playwright waits for the exact token in the browser
- SQL confirms the assistant message content exactly equals that token
- SQL confirms `ConversationTurns.ModelDeploymentId` equals the tested model
- SQL confirms assistant `NotebookConversationMessages.ModelDeploymentId` equals the tested model
- logs confirm `ReferenceKind=OverriddenToDefault`
- logs confirm the expected provider route

## Provider-Specific Test Settings

The Playwright matrix used provider-compatible `ChatDefaults` values.

Azure chat-completions models without reasoning:

```json
{
  "temperature": 0.7,
  "topP": 0.9,
  "reasoningEffort": null,
  "samplingParametersJson": "{\"temperature\":0.7,\"top_p\":0.9}"
}
```

Azure chat-completions models that rejected `reasoning_effort`:

```json
{
  "temperature": null,
  "topP": null,
  "reasoningEffort": null,
  "samplingParametersJson": null
}
```

Azure Responses models accepting `minimal`:

```json
{
  "temperature": null,
  "topP": null,
  "reasoningEffort": "minimal",
  "samplingParametersJson": null
}
```

Azure Responses models requiring `low`:

```json
{
  "temperature": null,
  "topP": null,
  "reasoningEffort": "low",
  "samplingParametersJson": null
}
```

Anthropic models:

```json
{
  "temperature": null,
  "topP": null,
  "reasoningEffort": "minimal",
  "samplingParametersJson": null
}
```

## Model Matrix Executed

| Model | Expected provider | Reasoning used in passing test | Screenshot |
|---|---|---:|---|
| `gpt-4.1` | `azure-openai-chat` | `NULL` | `output/playwright/gpt_4_1_MATRIX_1777332926572.png` |
| `gpt-4.1-mini` | `azure-openai-chat` | `NULL` | `output/playwright/gpt_4_1_mini_MATRIX2_1777333010513.png` |
| `gpt-4o` | `azure-openai-chat` | `NULL` | `output/playwright/gpt_4o_MATRIX2_1777333010513.png` |
| `gpt-4o-mini` | `azure-openai-chat` | `NULL` | `output/playwright/gpt_4o_mini_MATRIX2_1777333010513.png` |
| `gpt-5` | `azure-openai-responses` | `minimal` | `output/playwright/gpt_5_MATRIX2_1777333010513.png` |
| `gpt-5-chat` | `azure-openai-chat` | `NULL` | `output/playwright/gpt_5_chat_MATRIX3_1777333596710.png` |
| `gpt-5-mini` | `azure-openai-responses` | `minimal` | `output/playwright/gpt_5_mini_MATRIX3_1777333596710.png` |
| `gpt-5-nano` | `azure-openai-responses` | `minimal` | `output/playwright/gpt_5_nano_MATRIX3_1777333596710.png` |
| `gpt-5.1` | `azure-openai-chat` | `NULL` | `output/playwright/gpt_5_1_MATRIX3_1777333596710.png` |
| `gpt-5.2-codex` | `azure-openai-responses` | `low` | `output/playwright/gpt_5_2_codex_MATRIX4_1777334208441.png` |
| `o3` | `azure-openai-responses` | `low` | `output/playwright/o3_MATRIX5_1777334594594.png` |
| `o4-mini` | `azure-openai-responses` | `low` | `output/playwright/o4_mini_MATRIX5_1777334594594.png` |
| `claude-haiku-4-5` | `anthropic` | `minimal` | `output/playwright/claude_haiku_4_5_MATRIX5_1777334594594.png` |
| `claude-sonnet-4-5` | `anthropic` | `minimal` | `output/playwright/claude_sonnet_4_5_MATRIX5_1777334594594.png` |
| `claude-opus-4-5` | `anthropic` | `minimal` | `output/playwright/claude_opus_4_5_MATRIX5_1777334594594.png` |

## Supporting SQL Assertion

After Playwright saw the exact token in the browser, SQL was used to assert that the persisted turn and assistant message matched the tested model.

The final assertion checked:

- `ConversationTurns.ModelDeploymentId`
- `ConversationTurns.Status`
- assistant `NotebookConversationMessages.ModelDeploymentId`
- assistant message content equals the exact token

Shape of the assertion:

```sql
WITH Expected AS (
  SELECT * FROM (VALUES
    ('gpt-4.1','77AFC48F-5DF4-4AFA-88B9-9111E6B13122','GA_MATRIX_GPT_4_1_MATRIX_1777332926572_OK')
  ) AS v(ModelId, ConversationId, Token)
)
SELECT
  e.ModelId,
  e.ConversationId,
  ct.ModelDeploymentId AS TurnModel,
  ct.Status AS TurnStatus,
  am.ModelDeploymentId AS AssistantMessageModel,
  CASE WHEN CAST(am.Content AS nvarchar(max)) = e.Token THEN 'yes' ELSE 'no' END AS ExactTokenReturned
FROM Expected e
JOIN dbo.ConversationTurns ct
  ON ct.NotebookConversationId = CONVERT(uniqueidentifier, e.ConversationId)
JOIN dbo.NotebookConversationMessages am
  ON am.NotebookConversationId = CONVERT(uniqueidentifier, e.ConversationId)
 AND am.Role = 3;
```

The full matrix returned:

```text
TurnStatus=completed
TurnModel=<tested model>
AssistantMessageModel=<tested model>
ExactTokenReturned=yes
```

for every model in the matrix.

## Supporting Log Assertion

Application logs were checked after the browser run:

```powershell
docker logs --since 45m guideants-webapi-ui 2>&1 |
  Select-String -Pattern '<conversation ids>|Chat provider route resolved'
```

For each passing test, logs showed the global default override and provider route:

```text
Conversation chat model resolved.
ConversationId=<conversation id>
RequestedModelId=gpt-4.1
ResolvedModelId=<tested model>
ReferenceKind=OverriddenToDefault

Chat provider route resolved.
RequestedModelId=<tested model>
CatalogModelId=<tested model>
Provider=<expected provider>
```

This log assertion was used to prove there was no hidden fallback provider or hidden fallback model.

## Failures Caught By The Browser Matrix

The Playwright matrix exposed invalid default settings that would not have been obvious from catalog inspection alone.

`gpt-5-chat` failed when `ReasoningEffort=minimal` was used:

```text
Unrecognized request argument supplied: reasoning_effort
```

Passing setting:

```text
ReasoningEffort=NULL
```

`gpt-5.2-codex` failed when `ReasoningEffort=minimal` was used:

```text
Unsupported value: 'minimal' is not supported with the 'gpt-5.2-codex-2026-01-14' model.
Supported values are: 'low', 'medium', 'high', and 'xhigh'.
```

Passing setting:

```text
ReasoningEffort=low
```

`o3` failed when `ReasoningEffort=minimal` was used:

```text
Unsupported value: 'minimal' is not supported with the 'o3-2025-04-16' model.
Supported values are: 'low', 'medium', and 'high'.
```

Passing setting:

```text
ReasoningEffort=low
```

## Pass Criteria

A model was considered verified only when all of these were true:

- Playwright created a new conversation through the UI.
- Playwright sent the prompt through the visible compose textbox.
- Playwright observed the exact unique token in the rendered assistant response.
- A screenshot was captured after the response rendered.
- SQL showed the turn completed.
- SQL showed the turn and assistant message used the tested model.
- SQL showed the assistant message exactly matched the expected token.
- Logs showed `ReferenceKind=OverriddenToDefault`.
- Logs showed the expected provider route.

## Final Cleanup

After the matrix finished, `ChatDefaults` was restored to the value captured before testing. This kept the Playwright matrix from selecting a permanent product default.
