using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddExcludedHostsAndWebSearchTool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExcludedHosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcludedHosts", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Tools",
                columns: new[] { "Id", "Category", "Created", "Description", "DisplayName", "DisplayOrder", "IsActive", "ToolType", "Updated" },
                values: new object[] { new Guid("b0000000-0000-0000-0000-00000000000d"), "Search", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Search the web using searXNG.", "Web Search", 0, true, "WebSearch", null });

            migrationBuilder.UpdateData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000001"),
                column: "DisplayOrder",
                value: 1);

            migrationBuilder.UpdateData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-00000000000a"),
                column: "DisplayOrder",
                value: 2);

            migrationBuilder.UpdateData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-00000000000b"),
                column: "DisplayOrder",
                value: 3);

            migrationBuilder.CreateIndex(
                name: "IX_ExcludedHosts_Created",
                table: "ExcludedHosts",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_ExcludedHosts_Host",
                table: "ExcludedHosts",
                column: "Host",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExcludedHosts");

            migrationBuilder.DeleteData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-00000000000d"));
        }
    }
}
