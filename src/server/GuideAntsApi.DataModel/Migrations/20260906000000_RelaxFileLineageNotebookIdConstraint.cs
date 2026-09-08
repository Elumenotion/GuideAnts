using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RelaxFileLineageNotebookIdConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the old overly restrictive constraint
            migrationBuilder.DropCheckConstraint(
                name: "CK_FileLineageEvent_NotebookId",
                table: "FileLineageEvents");

            // Add new constraint that only requires NotebookId for Notebook files.
            // Project files can optionally carry NotebookId when the action involves a
            // notebook (e.g. PublishedToProject / CopiedToNotebook track the source
            // notebook, which the history panel renders as a link).
            migrationBuilder.AddCheckConstraint(
                name: "CK_FileLineageEvent_NotebookId",
                table: "FileLineageEvents",
                sql: "FileKind = 0 OR (FileKind = 1 AND NotebookId IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FileLineageEvent_NotebookId",
                table: "FileLineageEvents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileLineageEvent_NotebookId",
                table: "FileLineageEvents",
                sql: "(FileKind = 1 AND NotebookId IS NOT NULL) OR (FileKind = 0 AND NotebookId IS NULL)");
        }
    }
}
