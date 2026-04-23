using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <summary>
    /// Renames catalog rows with Provider = "openai-chat" / "openai-responses"
    /// to "azure-openai-chat" / "azure-openai-responses". Before this fix the
    /// routing stack treated "openai-chat" as both "OpenAI platform" (for the
    /// readiness probe) and "Azure OpenAI" (for dispatch, because
    /// OpenAiChatClientFactory was DI-wired to GetAzureOpenAiConfig). Every
    /// shipped catalog row used those strings against an Azure-backed config,
    /// so this migration canonicalizes that state: the existing rows are Azure
    /// rows and should carry Azure-prefixed provider strings going forward.
    /// After this migration the platform rows can be introduced explicitly by
    /// operators without colliding with the previous meaning of "openai-chat".
    /// </summary>
    public partial class RenameOpenAiChatProvidersToAzure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE dbo.Models
                SET Provider = 'azure-openai-chat',
                    Updated = SYSUTCDATETIME()
                WHERE Provider IN ('openai', 'openai-chat');
                """);

            migrationBuilder.Sql("""
                UPDATE dbo.Models
                SET Provider = 'azure-openai-responses',
                    Updated = SYSUTCDATETIME()
                WHERE Provider = 'openai-responses';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE dbo.Models
                SET Provider = 'openai-chat',
                    Updated = SYSUTCDATETIME()
                WHERE Provider = 'azure-openai-chat';
                """);

            migrationBuilder.Sql("""
                UPDATE dbo.Models
                SET Provider = 'openai-responses',
                    Updated = SYSUTCDATETIME()
                WHERE Provider = 'azure-openai-responses';
                """);
        }
    }
}
