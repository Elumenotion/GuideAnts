using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOwnerUserIdFromProjectAndAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Assistants_Users_OwnerUserId",
                table: "Assistants");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Users_OwnerUserId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_OwnerUserId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Assistants_OwnerUserId_Name",
                table: "Assistants");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Assistants");

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_Name",
                table: "Assistants",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Assistants_Name",
                table: "Assistants");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Projects",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Assistants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OwnerUserId",
                table: "Projects",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_OwnerUserId_Name",
                table: "Assistants",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true,
                filter: "[OwnerUserId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Assistants_Users_OwnerUserId",
                table: "Assistants",
                column: "OwnerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Users_OwnerUserId",
                table: "Projects",
                column: "OwnerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
