using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAuthAndRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedByUserId",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SecurityStamp",
                table: "Users",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_ApprovedByUserId",
                table: "Users",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_AssignedByUserId",
                table: "UserRoles",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_Unique",
                table: "UserRoles",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_ApprovedByUserId",
                table: "Users",
                column: "ApprovedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // OssLiteSingleUserPrep retained this Id as the real single-user account.
            // Only remove the placeholder admin@localhost seed; never wipe a retained user
            // that still owns messages / edit history / context options.
            migrationBuilder.Sql(
                """
                DECLARE @SeedUserId uniqueidentifier = 'fd787545-ffae-4ea9-81fa-700db2fffccd';

                IF EXISTS (
                    SELECT 1
                    FROM dbo.Users
                    WHERE Id = @SeedUserId
                      AND Email = N'admin@localhost')
                BEGIN
                    IF COL_LENGTH(N'dbo.MessageEditHistories', N'FirstEditedByUserId') IS NOT NULL
                        UPDATE dbo.MessageEditHistories
                        SET FirstEditedByUserId = NULL
                        WHERE FirstEditedByUserId = @SeedUserId;

                    IF COL_LENGTH(N'dbo.NotebookConversationMessages', N'LastEditedByUserId') IS NOT NULL
                        UPDATE dbo.NotebookConversationMessages
                        SET LastEditedByUserId = NULL
                        WHERE LastEditedByUserId = @SeedUserId;

                    IF COL_LENGTH(N'dbo.NotebookConversationMessages', N'UserId') IS NOT NULL
                        UPDATE dbo.NotebookConversationMessages
                        SET UserId = NULL
                        WHERE UserId = @SeedUserId;

                    IF OBJECT_ID(N'dbo.UserProjectContextOption', N'U') IS NOT NULL
                        DELETE FROM dbo.UserProjectContextOption
                        WHERE UserId = @SeedUserId;

                    DELETE FROM dbo.Users WHERE Id = @SeedUserId;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_ApprovedByUserId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropIndex(
                name: "IX_Users_ApprovedByUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");
        }
    }
}
