using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentConfigJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectAssistantEnvironments",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectAssistantEnvironments", x => new { x.ProjectId, x.AssistantId });
                    table.ForeignKey(
                        name: "FK_ProjectAssistantEnvironments_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectAssistantEnvironments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAssistantEnvironments_AssistantId",
                table: "ProjectAssistantEnvironments",
                column: "AssistantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectAssistantEnvironments");
        }
    }
}
