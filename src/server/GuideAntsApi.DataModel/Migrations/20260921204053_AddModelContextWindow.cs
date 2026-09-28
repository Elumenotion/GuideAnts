using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddModelContextWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContextWindowTokens",
                table: "Models",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxOutputTokens",
                table: "Models",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextWindowTokens",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "MaxOutputTokens",
                table: "Models");
        }
    }
}
