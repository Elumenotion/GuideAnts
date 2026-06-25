using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectScheduledJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectScheduledJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    JobType = table.Column<byte>(type: "tinyint", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConversationTitle = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Prompt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssistantName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ScriptNotebookFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunStatus = table.Column<byte>(type: "tinyint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectScheduledJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectScheduledJobs_NotebookFiles_ScriptNotebookFileId",
                        column: x => x.ScriptNotebookFileId,
                        principalTable: "NotebookFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectScheduledJobs_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectScheduledJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectScheduledJobs_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectScheduledJobRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduledJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TriggeredBy = table.Column<byte>(type: "tinyint", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    StandardOutput = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StandardError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExitCode = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectScheduledJobRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectScheduledJobRuns_ProjectScheduledJobs_ScheduledJobId",
                        column: x => x.ScheduledJobId,
                        principalTable: "ProjectScheduledJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectScheduledJobRuns_ScheduledJobId_StartedUtc",
                table: "ProjectScheduledJobRuns",
                columns: new[] { "ScheduledJobId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectScheduledJobs_CreatedByUserId",
                table: "ProjectScheduledJobs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectScheduledJobs_NotebookId",
                table: "ProjectScheduledJobs",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectScheduledJobs_ProjectId_IsEnabled_NextRunUtc",
                table: "ProjectScheduledJobs",
                columns: new[] { "ProjectId", "IsEnabled", "NextRunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectScheduledJobs_ProjectId_Name",
                table: "ProjectScheduledJobs",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectScheduledJobs_ScriptNotebookFileId",
                table: "ProjectScheduledJobs",
                column: "ScriptNotebookFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectScheduledJobRuns");

            migrationBuilder.DropTable(
                name: "ProjectScheduledJobs");
        }
    }
}
