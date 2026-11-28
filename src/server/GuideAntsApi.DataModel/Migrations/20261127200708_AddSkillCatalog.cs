using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CatalogVersion",
                table: "AssistantSkillMetas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SkillId",
                table: "AssistantSkillMetas",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedFromAssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedFromSkillName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IsGlobal = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SkillVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SkillId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    RequiresToolsetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiresToolsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FallbackForToolsetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FallbackForToolsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PlatformsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkillVersions_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SkillFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SkillVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ContentBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkillFiles_SkillVersions_SkillVersionId",
                        column: x => x.SkillVersionId,
                        principalTable: "SkillVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSkillMetas_SkillId",
                table: "AssistantSkillMetas",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_SkillFiles_SkillVersionId_RelativePath_Unique",
                table: "SkillFiles",
                columns: new[] { "SkillVersionId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_Name_Unique",
                table: "Skills",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SkillVersions_SkillId_Version_Unique",
                table: "SkillVersions",
                columns: new[] { "SkillId", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantSkillMetas_Skills_SkillId",
                table: "AssistantSkillMetas",
                column: "SkillId",
                principalTable: "Skills",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantSkillMetas_Skills_SkillId",
                table: "AssistantSkillMetas");

            migrationBuilder.DropTable(
                name: "SkillFiles");

            migrationBuilder.DropTable(
                name: "SkillVersions");

            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_AssistantSkillMetas_SkillId",
                table: "AssistantSkillMetas");

            migrationBuilder.DropColumn(
                name: "CatalogVersion",
                table: "AssistantSkillMetas");

            migrationBuilder.DropColumn(
                name: "SkillId",
                table: "AssistantSkillMetas");
        }
    }
}
