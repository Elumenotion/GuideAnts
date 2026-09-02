using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddJobQueueCorrelationDedupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_JobQueue_CorrelationDedup",
                table: "JobQueue",
                columns: new[] { "CorrelationId", "JobType", "Status" },
                unique: true,
                filter: "[CorrelationId] IS NOT NULL AND [Status] IN (0, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobQueue_CorrelationDedup",
                table: "JobQueue");
        }
    }
}
