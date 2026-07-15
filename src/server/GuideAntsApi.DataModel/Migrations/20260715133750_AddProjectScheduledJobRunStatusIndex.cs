using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectScheduledJobRunStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ProjectScheduledJobRuns_ScheduledJobId_Status",
                table: "ProjectScheduledJobRuns",
                columns: new[] { "ScheduledJobId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProjectScheduledJobRuns_ScheduledJobId_Status",
                table: "ProjectScheduledJobRuns");
        }
    }
}
