using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceLocalModelInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServiceLocalModelInstallations",
                columns: table => new
                {
                    ServiceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ModelRef = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ContentKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CatalogId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CatalogVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestedRevision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TargetDirectory = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    LastOperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ArtifactsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ObservedState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ObservedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ObservedError = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLocalModelInstallations", x => new { x.ServiceId, x.ModelRef });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLocalModelInstallations_ServiceId",
                table: "ServiceLocalModelInstallations",
                column: "ServiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceLocalModelInstallations");
        }
    }
}
