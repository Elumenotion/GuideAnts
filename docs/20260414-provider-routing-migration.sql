BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ApplicationSettingsBackup_20260414_ProviderRouting', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ApplicationSettingsBackup_20260414_ProviderRouting
    (
        BackupId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BackupCapturedUtc DATETIME2 NOT NULL,
        SectionName NVARCHAR(128) NOT NULL,
        ConfigMode NVARCHAR(32) NOT NULL,
        JsonValue NVARCHAR(MAX) NOT NULL,
        SchemaVersion INT NOT NULL,
        CreatedUtc DATETIME2 NOT NULL,
        UpdatedUtc DATETIME2 NOT NULL,
        RowVersion VARBINARY(8) NOT NULL
    );
END;

INSERT INTO dbo.ApplicationSettingsBackup_20260414_ProviderRouting
(
    BackupCapturedUtc,
    SectionName,
    ConfigMode,
    JsonValue,
    SchemaVersion,
    CreatedUtc,
    UpdatedUtc,
    RowVersion
)
SELECT
    SYSUTCDATETIME(),
    SectionName,
    ConfigMode,
    JsonValue,
    SchemaVersion,
    CreatedUtc,
    UpdatedUtc,
    RowVersion
FROM dbo.ApplicationSettings;

;WITH Ranked AS
(
    SELECT
        SectionName,
        ConfigMode,
        ROW_NUMBER() OVER
        (
            PARTITION BY SectionName
            ORDER BY
                CASE
                    WHEN ConfigMode = 'docker' THEN 0
                    WHEN ConfigMode = 'local' THEN 1
                    ELSE 2
                END,
                CASE WHEN NULLIF(LTRIM(RTRIM(JsonValue)), '') IS NULL THEN 1 ELSE 0 END,
                UpdatedUtc DESC,
                CreatedUtc DESC
        ) AS rn
    FROM dbo.ApplicationSettings
)
DELETE s
FROM dbo.ApplicationSettings s
INNER JOIN Ranked r
    ON r.SectionName = s.SectionName
    AND r.ConfigMode = s.ConfigMode
WHERE r.rn > 1;

UPDATE dbo.ApplicationSettings
SET
    JsonValue = JSON_MODIFY(
        JSON_MODIFY(
            JSON_MODIFY(JsonValue, '$.ActiveProviderId',
                COALESCE(
                    NULLIF(JSON_VALUE(JsonValue, '$.ActiveProviderId'), ''),
                    CASE LOWER(COALESCE(JSON_VALUE(JsonValue, '$.Provider'), ''))
                        WHEN 'local-asr' THEN 'SpeechTranscription.LocalAsr.Http'
                        WHEN 'speechtranscription.localasr.http' THEN 'SpeechTranscription.LocalAsr.Http'
                        WHEN 'speechtranscription.azurespeech.batch' THEN 'SpeechTranscription.AzureSpeech.Batch'
                        ELSE 'SpeechTranscription.AzureSpeech.Batch'
                    END
                )
            ),
            '$.Provider',
            NULL
        ),
        '$.LocalAsrBaseUrl',
        NULL
    ),
    UpdatedUtc = SYSUTCDATETIME()
WHERE SectionName = 'SpeechTranscription';

UPDATE dbo.ApplicationSettings
SET
    JsonValue = JSON_MODIFY(
        JSON_MODIFY(
            JSON_MODIFY(JsonValue, '$.ActiveProviderId',
                COALESCE(
                    NULLIF(JSON_VALUE(JsonValue, '$.ActiveProviderId'), ''),
                    CASE LOWER(COALESCE(JSON_VALUE(JsonValue, '$.Provider'), ''))
                        WHEN 'local-tts' THEN 'SpeechSynthesis.LocalTts.Http'
                        WHEN 'speechsynthesis.localtts.http' THEN 'SpeechSynthesis.LocalTts.Http'
                        WHEN 'speechsynthesis.azurespeech.ssml' THEN 'SpeechSynthesis.AzureSpeech.Ssml'
                        ELSE 'SpeechSynthesis.AzureSpeech.Ssml'
                    END
                )
            ),
            '$.Provider',
            NULL
        ),
        '$.LocalTtsBaseUrl',
        NULL
    ),
    UpdatedUtc = SYSUTCDATETIME()
WHERE SectionName = 'SpeechSynthesis';

UPDATE dbo.ApplicationSettings
SET
    JsonValue = JSON_MODIFY(
        JSON_MODIFY(
            JSON_MODIFY(JsonValue, '$.ActiveProviderId',
                COALESCE(
                    NULLIF(JSON_VALUE(JsonValue, '$.ActiveProviderId'), ''),
                    CASE LOWER(COALESCE(JSON_VALUE(JsonValue, '$.Provider'), ''))
                        WHEN 'local' THEN 'ImageGeneration.LocalSd.Http'
                        WHEN 'imagegeneration.localsd.http' THEN 'ImageGeneration.LocalSd.Http'
                        WHEN 'imagegeneration.azureopenai.images' THEN 'ImageGeneration.AzureOpenAI.Images'
                        ELSE 'ImageGeneration.AzureOpenAI.Images'
                    END
                )
            ),
            '$.Provider',
            NULL
        ),
        '$.LocalSdBaseUrl',
        NULL
    ),
    UpdatedUtc = SYSUTCDATETIME()
WHERE SectionName = 'ImageGeneration';

UPDATE dbo.ApplicationSettings
SET
    JsonValue = JSON_MODIFY(
        JSON_MODIFY(
            JSON_MODIFY(
                JSON_MODIFY(JsonValue, '$.ActiveProviderId',
                    COALESCE(
                        NULLIF(JSON_VALUE(JsonValue, '$.ActiveProviderId'), ''),
                        CASE LOWER(COALESCE(JSON_VALUE(JsonValue, '$.Provider'), ''))
                            WHEN 'local' THEN 'Embeddings.LocalEmb.Http'
                            WHEN 'embeddings.localemb.http' THEN 'Embeddings.LocalEmb.Http'
                            WHEN 'embeddings.azureopenai.embedding' THEN 'Embeddings.AzureOpenAI.Embedding'
                            ELSE 'Embeddings.AzureOpenAI.Embedding'
                        END
                    )
                ),
                '$.Provider',
                NULL
            ),
            '$.LocalBaseUrl',
            NULL
        ),
        '$.LocalMinIntervalMs',
        COALESCE(TRY_CONVERT(INT, JSON_VALUE(JsonValue, '$.LocalMinIntervalMs')), 5000)
    ),
    UpdatedUtc = SYSUTCDATETIME()
WHERE SectionName = 'Embeddings';

UPDATE dbo.ApplicationSettings
SET
    JsonValue = JSON_MODIFY(
        JSON_MODIFY(
            JSON_MODIFY(JsonValue, '$.ActiveProviderId',
                COALESCE(
                    NULLIF(JSON_VALUE(JsonValue, '$.ActiveProviderId'), ''),
                    CASE LOWER(COALESCE(JSON_VALUE(JsonValue, '$.Provider'), ''))
                        WHEN 'docling' THEN 'DocumentIntelligence.LocalDocling.Http'
                        WHEN 'documentintelligence.localdocling.http' THEN 'DocumentIntelligence.LocalDocling.Http'
                        WHEN 'documentintelligence.azure.documentintelligence' THEN 'DocumentIntelligence.Azure.DocumentIntelligence'
                        ELSE 'DocumentIntelligence.Azure.DocumentIntelligence'
                    END
                )
            ),
            '$.Provider',
            NULL
        ),
        '$.LocalDoclingBaseUrl',
        NULL
    ),
    UpdatedUtc = SYSUTCDATETIME()
WHERE SectionName = 'DocumentIntelligence';

DELETE FROM dbo.ApplicationSettings
WHERE SectionName = 'LocalServiceHosts';

DECLARE @Defaults TABLE
(
    SectionName NVARCHAR(128) NOT NULL,
    JsonValue NVARCHAR(MAX) NOT NULL
);

INSERT INTO @Defaults (SectionName, JsonValue)
VALUES
    ('SpeechTranscription', '{"ActiveProviderId":"SpeechTranscription.AzureSpeech.Batch","TimeoutSeconds":300}'),
    ('SpeechSynthesis', '{"ActiveProviderId":"SpeechSynthesis.AzureSpeech.Ssml","TimeoutSeconds":300}'),
    ('ImageGeneration', '{"ActiveProviderId":"ImageGeneration.AzureOpenAI.Images","TimeoutSeconds":600}'),
    ('Embeddings', '{"ActiveProviderId":"Embeddings.AzureOpenAI.Embedding","TimeoutSeconds":300,"LocalMinIntervalMs":5000}'),
    ('DocumentIntelligence', '{"ActiveProviderId":"DocumentIntelligence.Azure.DocumentIntelligence","TimeoutSeconds":300}'),
    ('AzureDocumentIntelligence', '{}'),
    ('AzureSpeechService', '{}'),
    ('AzureOpenAI', '{}'),
    ('AzureOpenAiImages', '{}'),
    ('AzureOpenAiEmbedding', '{}'),
    ('OpenAI', '{}'),
    ('Anthropic', '{}'),
    ('Search', '{}'),
    ('LlamaCpp', '{}'),
    ('ServiceRouting', '{}');

INSERT INTO dbo.ApplicationSettings
(
    SectionName,
    ConfigMode,
    JsonValue,
    SchemaVersion,
    CreatedUtc,
    UpdatedUtc
)
SELECT
    d.SectionName,
    'docker',
    d.JsonValue,
    1,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
FROM @Defaults d
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.ApplicationSettings s
    WHERE s.SectionName = d.SectionName
);

DELETE FROM dbo.ApplicationSettings
WHERE SectionName NOT IN
(
    'SpeechTranscription',
    'SpeechSynthesis',
    'ImageGeneration',
    'Embeddings',
    'DocumentIntelligence',
    'AzureDocumentIntelligence',
    'AzureSpeechService',
    'AzureOpenAI',
    'AzureOpenAiImages',
    'AzureOpenAiEmbedding',
    'OpenAI',
    'Anthropic',
    'Search',
    'LlamaCpp',
    'ServiceRouting'
);

ALTER TABLE [ApplicationSettings] DROP CONSTRAINT [PK_ApplicationSettings];

DECLARE @var sysname;
SELECT @var = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ApplicationSettings]') AND [c].[name] = N'ConfigMode');
IF @var IS NOT NULL EXEC(N'ALTER TABLE [ApplicationSettings] DROP CONSTRAINT [' + @var + '];');
ALTER TABLE [ApplicationSettings] DROP COLUMN [ConfigMode];

ALTER TABLE [ApplicationSettings] ADD CONSTRAINT [PK_ApplicationSettings] PRIMARY KEY ([SectionName]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260414183022_RemoveApplicationSettingsConfigModeAndProviderRouting', N'9.0.11');

COMMIT;
GO

