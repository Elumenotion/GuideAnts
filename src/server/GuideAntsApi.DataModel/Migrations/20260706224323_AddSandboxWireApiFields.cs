using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddSandboxWireApiFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExposeSandboxWireApi",
                table: "ProjectScheduledJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WireAttributionConversationTitle",
                table: "ProjectScheduledJobs",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WireCreateAttributionConversationPerRun",
                table: "ProjectScheduledJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "WireDailyLimitUsd",
                table: "ProjectScheduledJobs",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WireMonthlyLimitUsd",
                table: "ProjectScheduledJobs",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WireTargetAssistantId",
                table: "ProjectScheduledJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SandboxWireApiConfigJson",
                table: "Assistants",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExposeSandboxWireApi",
                table: "ProjectScheduledJobs");

            migrationBuilder.DropColumn(
                name: "WireAttributionConversationTitle",
                table: "ProjectScheduledJobs");

            migrationBuilder.DropColumn(
                name: "WireCreateAttributionConversationPerRun",
                table: "ProjectScheduledJobs");

            migrationBuilder.DropColumn(
                name: "WireDailyLimitUsd",
                table: "ProjectScheduledJobs");

            migrationBuilder.DropColumn(
                name: "WireMonthlyLimitUsd",
                table: "ProjectScheduledJobs");

            migrationBuilder.DropColumn(
                name: "WireTargetAssistantId",
                table: "ProjectScheduledJobs");

            migrationBuilder.DropColumn(
                name: "SandboxWireApiConfigJson",
                table: "Assistants");
        }
    }
}
