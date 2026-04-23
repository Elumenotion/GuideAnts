using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class OssLiteSingleUserPrep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Locale",
                table: "Users",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferencesJson",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "Users",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

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

            migrationBuilder.Sql(
                """
                DECLARE @RetainedUserId uniqueidentifier = 'fd787545-ffae-4ea9-81fa-700db2fffccd';

                IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Id = @RetainedUserId)
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM dbo.Users)
                    BEGIN
                        -- Empty database, just insert the user
                        INSERT INTO dbo.Users (Id, Email, Name, Created)
                        VALUES (@RetainedUserId, 'admin@localhost', 'Admin', GETUTCDATE());
                    END
                    ELSE
                    BEGIN
                        THROW 51021, 'OSS-lite migration failed: retained user not found.', 1;
                    END
                END;

                IF OBJECT_ID(N'tempdb..#RetainedProjects', N'U') IS NOT NULL DROP TABLE #RetainedProjects;
                CREATE TABLE #RetainedProjects
                (
                    ProjectId uniqueidentifier NOT NULL PRIMARY KEY
                );

                IF OBJECT_ID(N'dbo.ProjectUserRoles', N'U') IS NOT NULL
                BEGIN
                    INSERT INTO #RetainedProjects(ProjectId)
                    SELECT DISTINCT pur.ProjectId
                    FROM dbo.ProjectUserRoles pur
                    WHERE pur.UserId = @RetainedUserId;
                END;

                IF OBJECT_ID(N'dbo.Teams', N'U') IS NOT NULL
                BEGIN
                    INSERT INTO #RetainedProjects(ProjectId)
                    SELECT DISTINCT p.Id
                    FROM dbo.Projects p
                    INNER JOIN dbo.Teams t ON t.Id = p.TeamId
                    WHERE t.CreatedByUserId = @RetainedUserId
                      AND NOT EXISTS (SELECT 1 FROM #RetainedProjects rp WHERE rp.ProjectId = p.Id);
                END;

                UPDATE p
                SET
                    p.OwnerUserId = @RetainedUserId,
                    p.TeamId = NULL
                FROM dbo.Projects p
                INNER JOIN #RetainedProjects rp ON rp.ProjectId = p.Id;

                IF OBJECT_ID(N'dbo.ProjectUserRoles', N'U') IS NOT NULL
                BEGIN
                    DELETE pur
                    FROM dbo.ProjectUserRoles pur
                    WHERE pur.UserId <> @RetainedUserId
                       OR NOT EXISTS (SELECT 1 FROM #RetainedProjects rp WHERE rp.ProjectId = pur.ProjectId);
                END;

                IF OBJECT_ID(N'dbo.Assistants', N'U') IS NOT NULL
                BEGIN
                    UPDATE a
                    SET
                        a.OwnerUserId = @RetainedUserId,
                        a.OwnerTeamId = NULL
                    FROM dbo.Assistants a
                    WHERE a.OwnerUserId IS NULL
                      AND (
                          a.OwnerTeamId IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM dbo.Teams t
                              WHERE t.Id = a.OwnerTeamId
                                AND t.CreatedByUserId = @RetainedUserId
                          )
                      );
                END;

                IF OBJECT_ID(N'dbo.TeamInvitations', N'U') IS NOT NULL
                BEGIN
                    DELETE FROM dbo.TeamInvitations;
                END;

                IF OBJECT_ID(N'dbo.TeamMemberships', N'U') IS NOT NULL
                BEGIN
                    DELETE tm
                    FROM dbo.TeamMemberships tm
                    WHERE tm.UserId <> @RetainedUserId;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
                name: "Locale",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PreferencesJson",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Assistants");
        }
    }
}
