using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyMediaTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM [AssistantTools]
                WHERE [ToolId] IN (
                    'b0000000-0000-0000-0000-000000000004',
                    'b0000000-0000-0000-0000-000000000006'
                );
                """);

            migrationBuilder.DeleteData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000006"));

            migrationBuilder.UpdateData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000005"),
                column: "DisplayOrder",
                value: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000005"),
                column: "DisplayOrder",
                value: 4);

            migrationBuilder.InsertData(
                table: "Tools",
                columns: new[] { "Id", "Category", "Created", "Description", "DisplayName", "DisplayOrder", "IsActive", "ToolType", "Updated" },
                values: new object[,]
                {
                    { new Guid("b0000000-0000-0000-0000-000000000004"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Deprecated tool.", "Removed Tool 004", 3, true, "removed_tool_004", null },
                    { new Guid("b0000000-0000-0000-0000-000000000006"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Deprecated tool.", "Removed Tool 006", 5, true, "removed_tool_006", null }
                });
        }
    }
}
