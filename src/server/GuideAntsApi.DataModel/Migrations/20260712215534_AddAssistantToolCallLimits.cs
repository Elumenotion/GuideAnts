using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantToolCallLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxToolCallsPerInvocation",
                table: "GuideMembers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxToolCallsPerTurn",
                table: "Assistants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxToolRoundsPerTurn",
                table: "Assistants",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxToolCallsPerInvocation",
                table: "GuideMembers");

            migrationBuilder.DropColumn(
                name: "MaxToolCallsPerTurn",
                table: "Assistants");

            migrationBuilder.DropColumn(
                name: "MaxToolRoundsPerTurn",
                table: "Assistants");
        }
    }
}
