using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RemoveConversationLockUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationLocks_Users_LockedByUserId",
                table: "ConversationLocks");

            migrationBuilder.DropIndex(
                name: "IX_ConversationLocks_LockedByUserId",
                table: "ConversationLocks");

            migrationBuilder.DropColumn(
                name: "LockedByUserId",
                table: "ConversationLocks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LockedByUserId",
                table: "ConversationLocks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_ConversationLocks_LockedByUserId",
                table: "ConversationLocks",
                column: "LockedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationLocks_Users_LockedByUserId",
                table: "ConversationLocks",
                column: "LockedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
