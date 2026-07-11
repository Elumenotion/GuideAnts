using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class DropFleetLlamaAndMigrationIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetLlamaRuntimeSettings");

            migrationBuilder.DropTable(
                name: "LocalModelMigrationIssues");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FleetLlamaRuntimeSettings",
                columns: table => new
                {
                    SingletonKey = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AppliedRevision = table.Column<int>(type: "int", nullable: false),
                    ApplyError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ApplyStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BootstrapSource = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    DesiredRevision = table.Column<int>(type: "int", nullable: false),
                    PresetJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetLlamaRuntimeSettings", x => x.SingletonKey);
                });

            migrationBuilder.CreateTable(
                name: "LocalModelMigrationIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    IssueCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequiredAction = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ResolutionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceValueSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalModelMigrationIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocalModelMigrationIssues_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "ModelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelMigrationIssues_ModelId_IssueCode_SourceHash",
                table: "LocalModelMigrationIssues",
                columns: new[] { "ModelId", "IssueCode", "SourceHash" },
                unique: true);
        }
    }
}
