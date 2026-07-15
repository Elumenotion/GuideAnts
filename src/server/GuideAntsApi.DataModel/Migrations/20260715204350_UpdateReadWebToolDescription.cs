using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class UpdateReadWebToolDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000001"),
                column: "Description",
                value: "Reads a web page URL and extracts requested content from it.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Tools",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000001"),
                column: "Description",
                value: "Reads a web page and returns markdown of the content.");
        }
    }
}
