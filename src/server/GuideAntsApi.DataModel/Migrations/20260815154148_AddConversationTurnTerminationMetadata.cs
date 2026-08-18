using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTurnTerminationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Turn_Status",
                table: "ConversationTurns");

            migrationBuilder.AddColumn<int>(
                name: "CheckpointVersion",
                table: "ConversationTurns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionId",
                table: "ConversationTurns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TerminalizedAt",
                table: "ConversationTurns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminationCode",
                table: "ConversationTurns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminationDetail",
                table: "ConversationTurns",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Turn_Status",
                table: "ConversationTurns",
                sql: "[Status] IN ('streaming', 'completed', 'cancelled', 'timed_out', 'failed', 'interrupted', 'pending_client_tool')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Turn_Status",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "CheckpointVersion",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "TerminalizedAt",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "TerminationCode",
                table: "ConversationTurns");

            migrationBuilder.DropColumn(
                name: "TerminationDetail",
                table: "ConversationTurns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Turn_Status",
                table: "ConversationTurns",
                sql: "[Status] IN ('streaming', 'completed', 'cancelled')");
        }
    }
}
