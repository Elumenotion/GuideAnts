using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishedWireApiPhase1DataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalRequestId",
                table: "UsageEvents",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalUserIdentity",
                table: "UsageEvents",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedGuideId",
                table: "UsageEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceChannel",
                table: "UsageEvents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WireApiConfigJson",
                table: "PublishedGuides",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_ExternalRequestId",
                table: "UsageEvents",
                column: "ExternalRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_PublishedGuideId_Created",
                table: "UsageEvents",
                columns: new[] { "PublishedGuideId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_SourceChannel_Created",
                table: "UsageEvents",
                columns: new[] { "SourceChannel", "Created" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_ExternalRequestId",
                table: "UsageEvents");

            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_PublishedGuideId_Created",
                table: "UsageEvents");

            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_SourceChannel_Created",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "ExternalRequestId",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "ExternalUserIdentity",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "PublishedGuideId",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "SourceChannel",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "WireApiConfigJson",
                table: "PublishedGuides");
        }
    }
}
