using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddGuideAntsGuideSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthMode",
                table: "PublishedGuides",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemProject",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Backfill AuthMode from the legacy auth columns to preserve existing
            // behavior (D-GG-B). Precedence matches PublishedGuideAuthService, which
            // checks ApiKeyHash before AuthValidationWebhookUrl: ApiKey wins if both
            // are set. Rows with neither stay Anonymous (0, the column default).
            // Both statements are idempotent — they derive AuthMode purely from the
            // current legacy column values, so re-running produces the same result.
            // ApiKeyHash present -> ApiKey (2)
            migrationBuilder.Sql(@"
UPDATE [PublishedGuides]
SET [AuthMode] = 2
WHERE [ApiKeyHash] IS NOT NULL AND LTRIM(RTRIM([ApiKeyHash])) <> '';");

            // No ApiKey, but AuthValidationWebhookUrl present -> Webhook (1)
            migrationBuilder.Sql(@"
UPDATE [PublishedGuides]
SET [AuthMode] = 1
WHERE ([ApiKeyHash] IS NULL OR LTRIM(RTRIM([ApiKeyHash])) = '')
  AND [AuthValidationWebhookUrl] IS NOT NULL
  AND LTRIM(RTRIM([AuthValidationWebhookUrl])) <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthMode",
                table: "PublishedGuides");

            migrationBuilder.DropColumn(
                name: "IsSystemProject",
                table: "Projects");
        }
    }
}
