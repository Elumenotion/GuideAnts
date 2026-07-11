using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddCuratedLocalLlamaPersistencePhase1B : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestFieldsWhenToolsPresentJson",
                table: "RuntimeProfiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "FleetLlamaRuntimeSettings",
                columns: table => new
                {
                    SingletonKey = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PresetJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DesiredRevision = table.Column<int>(type: "int", nullable: false),
                    AppliedRevision = table.Column<int>(type: "int", nullable: false),
                    ApplyStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ApplyError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    BootstrapSource = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetLlamaRuntimeSettings", x => x.SingletonKey);
                });

            migrationBuilder.CreateTable(
                name: "LocalModelInstallations",
                columns: table => new
                {
                    ModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ManagementMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CatalogId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CatalogVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Repository = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RequestedRevision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ResolvedRevision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    QuantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    QuantLabel = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RouterModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RuntimeProfileId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetDirectory = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ModelArtifactsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProjectorArtifactsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RouterPresetSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalModelInstallations", x => x.ModelId);
                    table.ForeignKey(
                        name: "FK_LocalModelInstallations_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "ModelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LocalModelMigrationIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IssueCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceValueSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredAction = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ResolutionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
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

            migrationBuilder.CreateTable(
                name: "LocalModelOperations",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RouterModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ImmutableInputJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CurrentStep = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CompletedSideEffectsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Remediation = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    DesiredRevision = table.Column<int>(type: "int", nullable: true),
                    AppliedRevision = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalModelOperations", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelMigrationIssues_ModelId_IssueCode_SourceHash",
                table: "LocalModelMigrationIssues",
                columns: new[] { "ModelId", "IssueCode", "SourceHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelOperations_ModelId",
                table: "LocalModelOperations",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelOperations_RouterModelId",
                table: "LocalModelOperations",
                column: "RouterModelId",
                filter: "[RouterModelId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LocalModelOperations_Status_UpdatedUtc",
                table: "LocalModelOperations",
                columns: new[] { "Status", "UpdatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetLlamaRuntimeSettings");

            migrationBuilder.DropTable(
                name: "LocalModelInstallations");

            migrationBuilder.DropTable(
                name: "LocalModelMigrationIssues");

            migrationBuilder.DropTable(
                name: "LocalModelOperations");

            migrationBuilder.DropColumn(
                name: "RequestFieldsWhenToolsPresentJson",
                table: "RuntimeProfiles");
        }
    }
}
