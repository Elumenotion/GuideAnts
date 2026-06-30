using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260625193000_AddConversationTurnTraceCapture")]
    public partial class AddConversationTurnTraceCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationTurnTraces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationTurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnIndex = table.Column<int>(type: "int", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    CaptureState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TraceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationTurnTraces", x => x.Id);
                    table.CheckConstraint(
                        name: "CK_ConversationTurnTrace_State",
                        sql: "[CaptureState] IN ('partial', 'completed', 'cancelled', 'failed')");
                    table.ForeignKey(
                        name: "FK_ConversationTurnTraces_ConversationTurns_ConversationTurnId",
                        column: x => x.ConversationTurnId,
                        principalTable: "ConversationTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurnTraces_ConversationTurnId",
                table: "ConversationTurnTraces",
                column: "ConversationTurnId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurnTraces_NotebookConversationId_TurnIndex",
                table: "ConversationTurnTraces",
                columns: new[] { "NotebookConversationId", "TurnIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationTurnTraces");
        }
    }
}
