using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RouteReadWebThroughGuideAntsTool : Migration
    {
        private static readonly Guid GetContentFromUrlToolId = new("b0000000-0000-0000-0000-00000000000e");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Tools",
                columns: new[] { "Id", "Category", "Created", "Description", "DisplayName", "DisplayOrder", "IsActive", "ToolType", "Updated" },
                values: new object[] { GetContentFromUrlToolId, "Search", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Fetch a web page and convert it to markdown.", "Get Content From URL", 4, true, "GetContentFromUrl", null });

            migrationBuilder.Sql($"""
                INSERT INTO [AssistantTools] ([AssistantId], [ToolId], [Created])
                SELECT a.[Id], '{GetContentFromUrlToolId}', GETUTCDATE()
                FROM [Assistants] a
                WHERE a.[Name] = N'Read Web'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM [AssistantTools] at
                      WHERE at.[AssistantId] = a.[Id]
                        AND at.[ToolId] = '{GetContentFromUrlToolId}'
                  );

                DELETE s
                FROM [AssistantOpenApiSchemas] s
                INNER JOIN [Assistants] a ON a.[Id] = s.[AssistantId]
                WHERE a.[Name] = N'Read Web'
                  AND (
                      s.[SpecificationJson] LIKE '%HtmlAgility.HtmlAgilityPackExtensions.ConvertUrlToMarkdown%'
                      OR s.[SpecificationJson] LIKE '%GuideAntsApi.Services.ReadWebTools.GetContentFromUrl%'
                  );

                UPDATE [Assistants]
                SET [Updated] = GETUTCDATE()
                WHERE [Name] = N'Read Web';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE at
                FROM [AssistantTools] at
                INNER JOIN [Assistants] a ON a.[Id] = at.[AssistantId]
                WHERE a.[Name] = N'Read Web'
                  AND at.[ToolId] = '{GetContentFromUrlToolId}';

                UPDATE [Assistants]
                SET [Updated] = GETUTCDATE()
                WHERE [Name] = N'Read Web';
                """);

            migrationBuilder.DeleteData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: GetContentFromUrlToolId);
        }
    }
}
