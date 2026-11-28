using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddVisionRetrievalTool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Tools",
                columns: new[] { "Id", "Category", "Created", "Description", "DisplayName", "DisplayOrder", "IsActive", "ToolType", "Updated" },
                values: new object[] { new Guid("b0000000-0000-0000-0000-00000000000f"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Retrieve an image from the notebook and present it to the model for visual inspection.", "Inspect Image", 6, true, "GetImageForInspection", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-00000000000f"));
        }
    }
}
