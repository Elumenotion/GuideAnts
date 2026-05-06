using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RenameLocalRuntimeJsonAndAddRuntimeProfileKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LocalRuntimeJson",
                table: "Models",
                newName: "RuntimeConfigJson");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "RuntimeProfiles",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "RuntimeProfiles");

            migrationBuilder.RenameColumn(
                name: "RuntimeConfigJson",
                table: "Models",
                newName: "LocalRuntimeJson");
        }
    }
}
