using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RemoveNotebookConversationModelDeploymentOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID('ConversationCurrentState', 'V') IS NOT NULL
                    DROP VIEW ConversationCurrentState;
                """);

            migrationBuilder.DropColumn(
                name: "ModelDeploymentOverrideId",
                table: "NotebookConversations");

            migrationBuilder.Sql(
                """
                CREATE VIEW ConversationCurrentState AS
                SELECT
                    c.Id,
                    c.NotebookId,
                    c.Title,
                    c.Summary,
                    c.Created,
                    lastTurn.AssistantName AS CurrentAssistantName,
                    lastTurn.ModelDeploymentId AS CurrentModelDeploymentId,
                    lastTurn.Instructions AS LastInstructions,
                    lastTurn.Created AS LastActivity,
                    lastTurn.TurnIndex AS CurrentTurnIndex,
                    lastTurn.FilesCreated AS LastTurnFilesCreated,
                    lastTurn.FilesModified AS LastTurnFilesModified
                FROM NotebookConversations c
                LEFT JOIN (
                    SELECT
                        t1.NotebookConversationId,
                        t1.AssistantName,
                        t1.ModelDeploymentId,
                        t1.Instructions,
                        t1.Created,
                        t1.TurnIndex,
                        t1.FilesCreated,
                        t1.FilesModified
                    FROM ConversationTurns t1
                    WHERE t1.TurnIndex = (
                        SELECT MAX(t2.TurnIndex)
                        FROM ConversationTurns t2
                        WHERE t2.NotebookConversationId = t1.NotebookConversationId
                    )
                ) lastTurn ON c.Id = lastTurn.NotebookConversationId
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID('ConversationCurrentState', 'V') IS NOT NULL
                    DROP VIEW ConversationCurrentState;
                """);

            migrationBuilder.AddColumn<string>(
                name: "ModelDeploymentOverrideId",
                table: "NotebookConversations",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.Sql(
                """
                CREATE VIEW ConversationCurrentState AS
                SELECT
                    c.Id,
                    c.NotebookId,
                    c.Title,
                    c.Summary,
                    c.Created,
                    lastTurn.AssistantName AS CurrentAssistantName,
                    COALESCE(c.ModelDeploymentOverrideId, lastTurn.ModelDeploymentId) AS CurrentModelDeploymentId,
                    lastTurn.Instructions AS LastInstructions,
                    lastTurn.Created AS LastActivity,
                    lastTurn.TurnIndex AS CurrentTurnIndex,
                    lastTurn.FilesCreated AS LastTurnFilesCreated,
                    lastTurn.FilesModified AS LastTurnFilesModified
                FROM NotebookConversations c
                LEFT JOIN (
                    SELECT
                        t1.NotebookConversationId,
                        t1.AssistantName,
                        t1.ModelDeploymentId,
                        t1.Instructions,
                        t1.Created,
                        t1.TurnIndex,
                        t1.FilesCreated,
                        t1.FilesModified
                    FROM ConversationTurns t1
                    WHERE t1.TurnIndex = (
                        SELECT MAX(t2.TurnIndex)
                        FROM ConversationTurns t2
                        WHERE t2.NotebookConversationId = t1.NotebookConversationId
                    )
                ) lastTurn ON c.Id = lastTurn.NotebookConversationId
                """);
        }
    }
}
