-- UpsertLocalLlamaQwenModels
-- Idempotent onboarding script for local llama.cpp model rows (Qwen + Gemma).
-- Uses canonical RuntimeConfigJson shape:
-- { "routerModelId", "runtimeProfileId", "loadParams" }

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @provider nvarchar(32) = N'llama-cpp';
DECLARE @now datetime2 = GETUTCDATE();

IF COL_LENGTH('dbo.Models', 'ReasoningChoicesJson') IS NULL
   OR COL_LENGTH('dbo.Models', 'RuntimeConfigJson') IS NULL
BEGIN
    PRINT 'Required columns not found in dbo.Models. Apply latest migrations first.';
    RETURN;
END;

DECLARE @models TABLE
(
    ModelId nvarchar(128) NOT NULL PRIMARY KEY,
    DisplayName nvarchar(255) NOT NULL,
    Description nvarchar(1024) NULL,
    ReasoningChoicesJson nvarchar(max) NULL,
    DisplayOrder int NOT NULL,
    RuntimeConfigJson nvarchar(max) NOT NULL
);

INSERT INTO @models
(
    ModelId,
    DisplayName,
    Description,
    ReasoningChoicesJson,
    DisplayOrder,
    RuntimeConfigJson
)
VALUES
(
    N'qwen3.5-27b',
    N'Qwen 3.5 27B (Local)',
    N'Local llama.cpp-hosted Qwen 3.5 27B model.',
    N'["none","enabled"]',
    500,
    N'{"routerModelId":"Qwen3.5-27B-Q6_K","runtimeProfileId":"qwen3_5","loadParams":{"model":"Qwen3.5-27B-Q6_K"}}'
),
(
    N'qwen3.5-35b-a3b',
    N'Qwen 3.5 35B A3B (Local)',
    N'Local llama.cpp-hosted Qwen 3.5 35B A3B model.',
    N'["none","enabled"]',
    505,
    N'{"routerModelId":"Qwen3.5-35B-A3B-Q5_K_XL","runtimeProfileId":"qwen3_5","loadParams":{"model":"Qwen3.5-35B-A3B-Q5_K_XL"}}'
),
(
    N'gemma4-31b',
    N'Gemma 4 31B (Local)',
    N'Local llama.cpp-hosted Gemma 4 31B dense model.',
    N'["none","enabled"]',
    510,
    N'{"routerModelId":"Gemma-4-31B-Q6_K_XL","runtimeProfileId":"gemma4","loadParams":{"model":"Gemma-4-31B-Q6_K_XL"}}'
);

MERGE dbo.Models AS target
USING @models AS source
    ON target.ModelId = source.ModelId
WHEN MATCHED THEN
    UPDATE SET
        target.DisplayName = source.DisplayName,
        target.Provider = @provider,
        target.Description = source.Description,
        target.ReasoningChoicesJson = source.ReasoningChoicesJson,
        target.RuntimeConfigJson = source.RuntimeConfigJson,
        target.IsActive = 1,
        target.DisplayOrder = source.DisplayOrder,
        target.Updated = @now
WHEN NOT MATCHED BY TARGET THEN
    INSERT
    (
        ModelId,
        DisplayName,
        Provider,
        Description,
        ReasoningChoicesJson,
        RuntimeConfigJson,
        IsActive,
        DisplayOrder,
        Created,
        Updated
    )
    VALUES
    (
        source.ModelId,
        source.DisplayName,
        @provider,
        source.Description,
        source.ReasoningChoicesJson,
        source.RuntimeConfigJson,
        1,
        source.DisplayOrder,
        @now,
        @now
    );

-- Migrate assistants still pointing at legacy Qwen model ids.
UPDATE dbo.Assistants
SET ModelId = N'qwen3.5-27b',
    Updated = @now
WHERE ModelId IN (N'qwen3.5-27b-q6', N'qwen3.5-27b-q8');

-- Migrate assistants pointing at fabricated gemma4-27b id.
UPDATE dbo.Assistants
SET ModelId = N'gemma4-31b',
    Updated = @now
WHERE ModelId = N'gemma4-27b';

-- Remove legacy/incorrect rows if present.
DELETE FROM dbo.Models
WHERE ModelId IN (N'qwen3.5-27b-q6', N'qwen3.5-27b-q8', N'gemma4-27b');
