-- UpsertRuntimeProfiles
-- Idempotent seed script for RuntimeProfile rows.
-- Sources: Unsloth recommended settings for Qwen 3.5 and Gemma 4.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID('dbo.RuntimeProfiles', 'U') IS NULL
BEGIN
    PRINT 'RuntimeProfiles table not found. Apply AddRuntimeProfile migration first.';
    RETURN;
END;

DECLARE @now datetime2 = GETUTCDATE();

-- Qwen 3.5 family profile (https://unsloth.ai/docs/models/qwen3.5#recommended-settings)
-- Thinking mode general:  temperature=1.0, top_p=0.95, top_k=20, min_p=0.0, presence_penalty=1.5, repetition_penalty=1.0
-- Thinking mode coding:   temperature=0.6, top_p=0.95, top_k=20, min_p=0.0, presence_penalty=0.0, repetition_penalty=1.0
-- Non-thinking general:   temperature=0.7, top_p=0.8,  top_k=20, min_p=0.0, presence_penalty=1.5, repetition_penalty=1.0
-- Non-thinking reasoning: temperature=1.0, top_p=0.95, top_k=20, min_p=0.0, presence_penalty=1.5, repetition_penalty=1.0
-- We use the non-thinking general defaults as the profile defaults.
MERGE dbo.RuntimeProfiles AS target
USING (SELECT N'qwen3_5' AS ProfileId) AS source ON target.ProfileId = source.ProfileId
WHEN MATCHED THEN
    UPDATE SET
        target.DisplayName = N'Qwen 3.5',
        target.Description = N'Profile for Qwen 3.5 model family. Thinking via chat_template_kwargs.enable_thinking.',
        target.CombineSystemAndDeveloperMessages = 1,
        target.ThoughtBlockPattern = N'<think>[\s\S]*?</think>',
        target.SamplingParametersJson = N'{
  "temperature": { "key": "temperature", "label": "Temperature", "description": "Controls randomness: lower is focused, higher is creative", "min": 0.0, "max": 2.0, "step": 0.1, "default": 0.7, "displayOrder": 0, "exposedInGuideBuilder": true },
  "top_p": { "key": "top_p", "label": "Top P", "description": "Nucleus sampling: controls diversity of token selection", "min": 0.0, "max": 1.0, "step": 0.05, "default": 0.8, "displayOrder": 1, "exposedInGuideBuilder": true },
  "top_k": { "key": "top_k", "label": "Top K", "description": "Limits number of token candidates considered", "min": 1, "max": 100, "step": 1, "default": 20, "displayOrder": 2, "exposedInGuideBuilder": true },
  "min_p": { "key": "min_p", "label": "Min P", "description": "Minimum probability threshold for token selection", "min": 0.0, "max": 1.0, "step": 0.01, "default": 0.0, "displayOrder": 3, "exposedInGuideBuilder": false },
  "presence_penalty": { "key": "presence_penalty", "label": "Presence Penalty", "description": "Reduces repetition (0=off, 1.5 recommended for general use, may reduce quality at higher values)", "min": 0.0, "max": 2.0, "step": 0.1, "default": 1.5, "displayOrder": 4, "exposedInGuideBuilder": true },
  "repetition_penalty": { "key": "repetition_penalty", "label": "Repetition Penalty", "description": "Penalizes repeated tokens (1.0=disabled)", "min": 1.0, "max": 2.0, "step": 0.1, "default": 1.0, "displayOrder": 5, "exposedInGuideBuilder": false }
}',
        target.ThinkingControlJson = N'{
  "defaultChoice": "enabled",
  "choiceActions": {
    "none": [
      { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": false },
      { "target": "RequestField", "key": "reasoning_format", "value": "none" }
    ],
    "enabled": [
      { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": true }
    ]
  }
}',
        target.Updated = @now
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ProfileId, DisplayName, Description, CombineSystemAndDeveloperMessages, ThoughtBlockPattern,
            SamplingParametersJson, ThinkingControlJson, Created, Updated)
    VALUES (
        N'qwen3_5',
        N'Qwen 3.5',
        N'Profile for Qwen 3.5 model family. Thinking via chat_template_kwargs.enable_thinking.',
        1,
        N'<think>[\s\S]*?</think>',
        N'{
  "temperature": { "key": "temperature", "label": "Temperature", "description": "Controls randomness: lower is focused, higher is creative", "min": 0.0, "max": 2.0, "step": 0.1, "default": 0.7, "displayOrder": 0, "exposedInGuideBuilder": true },
  "top_p": { "key": "top_p", "label": "Top P", "description": "Nucleus sampling: controls diversity of token selection", "min": 0.0, "max": 1.0, "step": 0.05, "default": 0.8, "displayOrder": 1, "exposedInGuideBuilder": true },
  "top_k": { "key": "top_k", "label": "Top K", "description": "Limits number of token candidates considered", "min": 1, "max": 100, "step": 1, "default": 20, "displayOrder": 2, "exposedInGuideBuilder": true },
  "min_p": { "key": "min_p", "label": "Min P", "description": "Minimum probability threshold for token selection", "min": 0.0, "max": 1.0, "step": 0.01, "default": 0.0, "displayOrder": 3, "exposedInGuideBuilder": false },
  "presence_penalty": { "key": "presence_penalty", "label": "Presence Penalty", "description": "Reduces repetition (0=off, 1.5 recommended for general use, may reduce quality at higher values)", "min": 0.0, "max": 2.0, "step": 0.1, "default": 1.5, "displayOrder": 4, "exposedInGuideBuilder": true },
  "repetition_penalty": { "key": "repetition_penalty", "label": "Repetition Penalty", "description": "Penalizes repeated tokens (1.0=disabled)", "min": 1.0, "max": 2.0, "step": 0.1, "default": 1.0, "displayOrder": 5, "exposedInGuideBuilder": false }
}',
        N'{
  "defaultChoice": "enabled",
  "choiceActions": {
    "none": [
      { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": false },
      { "target": "RequestField", "key": "reasoning_format", "value": "none" }
    ],
    "enabled": [
      { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": true }
    ]
  }
}',
        @now,
        @now
    );

-- Gemma 4 family profile (https://unsloth.ai/docs/models/gemma-4#recommended-settings)
-- Recommended: temperature=1.0, top_p=0.95, top_k=64
-- Thinking: prepend <|think|> to system message
-- "Keep repetition/presence penalty disabled or 1.0 unless you see looping."
MERGE dbo.RuntimeProfiles AS target
USING (SELECT N'gemma4' AS ProfileId) AS source ON target.ProfileId = source.ProfileId
WHEN MATCHED THEN
    UPDATE SET
        target.DisplayName = N'Gemma 4',
        target.Description = N'Profile for Gemma 4 model family. Thinking via <|think|> system message prefix.',
        target.CombineSystemAndDeveloperMessages = 1,
        target.ThoughtBlockPattern = N'<\|channel>thought[\s\S]*?<channel\|>',
        target.SamplingParametersJson = N'{
  "temperature": { "key": "temperature", "label": "Temperature", "description": "Controls randomness: lower is focused, higher is creative", "min": 0.0, "max": 2.0, "step": 0.1, "default": 1.0, "displayOrder": 0, "exposedInGuideBuilder": true },
  "top_p": { "key": "top_p", "label": "Top P", "description": "Nucleus sampling: controls diversity of token selection", "min": 0.0, "max": 1.0, "step": 0.05, "default": 0.95, "displayOrder": 1, "exposedInGuideBuilder": true },
  "top_k": { "key": "top_k", "label": "Top K", "description": "Limits number of token candidates considered", "min": 1, "max": 100, "step": 1, "default": 64, "displayOrder": 2, "exposedInGuideBuilder": true },
  "presence_penalty": { "key": "presence_penalty", "label": "Presence Penalty", "description": "Reduces repetition (disabled by default, use only if you see looping)", "min": 0.0, "max": 2.0, "step": 0.1, "default": 0.0, "displayOrder": 3, "exposedInGuideBuilder": false },
  "repetition_penalty": { "key": "repetition_penalty", "label": "Repetition Penalty", "description": "Penalizes repeated tokens (1.0=disabled, use only if you see looping)", "min": 1.0, "max": 2.0, "step": 0.1, "default": 1.0, "displayOrder": 4, "exposedInGuideBuilder": false }
}',
        target.ThinkingControlJson = N'{
  "defaultChoice": "enabled",
  "choiceActions": {
    "none": [
      { "target": "RequestField", "key": "reasoning_format", "value": "none" }
    ],
    "enabled": [
      { "target": "SystemMessagePrefix", "key": "", "value": "<|think|>\n" }
    ]
  }
}',
        target.Updated = @now
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ProfileId, DisplayName, Description, CombineSystemAndDeveloperMessages, ThoughtBlockPattern,
            SamplingParametersJson, ThinkingControlJson, Created, Updated)
    VALUES (
        N'gemma4',
        N'Gemma 4',
        N'Profile for Gemma 4 model family. Thinking via <|think|> system message prefix.',
        1,
        N'<\|channel>thought[\s\S]*?<channel\|>',
        N'{
  "temperature": { "key": "temperature", "label": "Temperature", "description": "Controls randomness: lower is focused, higher is creative", "min": 0.0, "max": 2.0, "step": 0.1, "default": 1.0, "displayOrder": 0, "exposedInGuideBuilder": true },
  "top_p": { "key": "top_p", "label": "Top P", "description": "Nucleus sampling: controls diversity of token selection", "min": 0.0, "max": 1.0, "step": 0.05, "default": 0.95, "displayOrder": 1, "exposedInGuideBuilder": true },
  "top_k": { "key": "top_k", "label": "Top K", "description": "Limits number of token candidates considered", "min": 1, "max": 100, "step": 1, "default": 64, "displayOrder": 2, "exposedInGuideBuilder": true },
  "presence_penalty": { "key": "presence_penalty", "label": "Presence Penalty", "description": "Reduces repetition (disabled by default, use only if you see looping)", "min": 0.0, "max": 2.0, "step": 0.1, "default": 0.0, "displayOrder": 3, "exposedInGuideBuilder": false },
  "repetition_penalty": { "key": "repetition_penalty", "label": "Repetition Penalty", "description": "Penalizes repeated tokens (1.0=disabled, use only if you see looping)", "min": 1.0, "max": 2.0, "step": 0.1, "default": 1.0, "displayOrder": 4, "exposedInGuideBuilder": false }
}',
        N'{
  "defaultChoice": "enabled",
  "choiceActions": {
    "none": [
      { "target": "RequestField", "key": "reasoning_format", "value": "none" }
    ],
    "enabled": [
      { "target": "SystemMessagePrefix", "key": "", "value": "<|think|>\n" }
    ]
  }
}',
        @now,
        @now
    );

PRINT 'RuntimeProfiles upserted: qwen3_5, gemma4';
