using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class SeedUsageReportCategoriesAndOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET NOCOUNT ON;

                DECLARE @NowUtc datetime2(7) = GETUTCDATE();

                DECLARE @Categories TABLE
                (
                    [Key] nvarchar(128) NOT NULL,
                    [Title] nvarchar(256) NOT NULL,
                    [Description] nvarchar(1024) NULL
                );

                INSERT INTO @Categories ([Key], [Title], [Description])
                VALUES
                    (N'chat-completion', N'Chat Completion', N'LLM chat completion usage.'),
                    (N'search', N'Search', N'Search and retrieval operations.'),
                    (N'image-generation', N'Image Generation', N'Image generation and image edit operations.'),
                    (N'document-extraction', N'Document Extraction', N'Document and OCR extraction operations.'),
                    (N'speech-transcription', N'Speech Transcription', N'Speech-to-text operations.'),
                    (N'speech-synthesis', N'Speech Synthesis', N'Text-to-speech operations.'),
                    (N'storage-uploaded', N'Storage Uploaded', N'User-uploaded storage operations.'),
                    (N'storage-system-generated', N'Storage System Generated', N'System-generated storage operations.'),
                    (N'tool-call', N'Tool Call', N'Tool invocation operations.');

                UPDATE c
                SET
                    c.Title = s.[Title],
                    c.[Description] = s.[Description]
                FROM dbo.UsageReportCategories c
                INNER JOIN @Categories s ON s.[Key] = c.[Key];

                INSERT INTO dbo.UsageReportCategories (Id, [Key], Title, [Description], Created)
                SELECT NEWID(), s.[Key], s.[Title], s.[Description], @NowUtc
                FROM @Categories s
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.UsageReportCategories c
                    WHERE c.[Key] = s.[Key]
                );

                DECLARE @Operations TABLE
                (
                    [Operation] nvarchar(128) NOT NULL,
                    [CategoryKey] nvarchar(128) NOT NULL
                );

                INSERT INTO @Operations ([Operation], [CategoryKey])
                VALUES
                    (N'chat', N'chat-completion'),
                    (N'agent_invoke', N'chat-completion'),
                    (N'completions', N'chat-completion'),

                    (N'SearchProject', N'search'),
                    (N'SearchNotebook', N'search'),
                    (N'SearchAssistant', N'search'),
                    (N'WebSearch', N'search'),
                    (N'Ask', N'search'),
                    (N'Search', N'search'),

                    (N'image-generation', N'image-generation'),
                    (N'image-edit', N'image-generation'),

                    (N'prebuilt-read', N'document-extraction'),
                    (N'prebuilt-layout', N'document-extraction'),

                    (N'STT', N'speech-transcription'),
                    (N'stt', N'speech-transcription'),
                    (N'transcribe', N'speech-transcription'),

                    (N'TTS', N'speech-synthesis'),
                    (N'tts', N'speech-synthesis'),

                    (N'upload', N'storage-uploaded'),
                    (N'create-text-file', N'storage-uploaded'),

                    (N'system', N'storage-system-generated'),

                    (N'ReadWeb', N'tool-call'),
                    (N'GetContentFromUrl', N'tool-call'),
                    (N'search_project', N'tool-call'),
                    (N'search_notebook', N'tool-call'),
                    (N'SearchAssistantFiles', N'tool-call'),
                    (N'run_python', N'tool-call'),
                    (N'run_bash', N'tool-call'),
                    (N'make_diagram', N'tool-call'),
                    (N'generate_image', N'tool-call'),
                    (N'MakeImageFromImage', N'tool-call'),
                    (N'generate_podcast', N'tool-call'),
                    (N'set_user_context_options', N'tool-call'),
                    (N'InvokeAgent', N'tool-call'),
                    (N'GetParentConversation', N'tool-call'),
                    (N'crawl', N'tool-call');

                ;WITH CanonicalOperations AS
                (
                    SELECT DISTINCT
                        o.[Operation],
                        c.Id AS UsageReportCategoryId
                    FROM @Operations o
                    INNER JOIN dbo.UsageReportCategories c
                        ON c.[Key] = o.[CategoryKey]
                )
                UPDATE existing
                SET existing.UsageReportCategoryId = canonical.UsageReportCategoryId
                FROM dbo.UsageReportCategoryOperations existing
                INNER JOIN CanonicalOperations canonical
                    ON canonical.[Operation] = existing.[Operation]
                WHERE existing.UsageReportCategoryId <> canonical.UsageReportCategoryId;

                ;WITH CanonicalOperations AS
                (
                    SELECT DISTINCT
                        o.[Operation],
                        c.Id AS UsageReportCategoryId
                    FROM @Operations o
                    INNER JOIN dbo.UsageReportCategories c
                        ON c.[Key] = o.[CategoryKey]
                )
                INSERT INTO dbo.UsageReportCategoryOperations (Id, [Operation], UsageReportCategoryId)
                SELECT NEWID(), canonical.[Operation], canonical.UsageReportCategoryId
                FROM CanonicalOperations canonical
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.UsageReportCategoryOperations existing
                    WHERE existing.[Operation] = canonical.[Operation]
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally no-op: this migration seeds reference data and should not
            // remove user-curated category mappings on downgrade.
        }
    }
}
