-- BackfillModelReasoningChoices
-- Populates dbo.Models.ReasoningChoicesJson for models that support reasoning.
-- Safe to run multiple times.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @openAiChoices nvarchar(max) = N'["None","minimal","low","medium","high"]';
DECLARE @anthropicChoices nvarchar(max) = N'["None","minimal","low","medium","high"]';
DECLARE @now datetime2 = GETUTCDATE();

IF COL_LENGTH('dbo.Models', 'ReasoningChoicesJson') IS NULL
BEGIN
    PRINT 'ReasoningChoicesJson column not found in dbo.Models. Apply latest migrations first.';
    RETURN;
END;

-- OpenAI reasoning-capable model ids.
UPDATE m
SET m.ReasoningChoicesJson = @openAiChoices,
    m.Updated = @now
FROM dbo.Models m
WHERE m.ReasoningChoicesJson IS NULL
  AND m.Provider IN (N'openai', N'openai-chat', N'openai-responses')
  AND (
        m.ModelId LIKE N'o%'
        OR m.ModelId LIKE N'gpt-5%'
      );

-- Anthropic models can use reasoning effort when budgets are configured.
UPDATE m
SET m.ReasoningChoicesJson = @anthropicChoices,
    m.Updated = @now
FROM dbo.Models m
WHERE m.ReasoningChoicesJson IS NULL
  AND m.Provider = N'anthropic'
  AND m.ModelId LIKE N'claude%';
