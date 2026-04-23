using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAndNotebookSlugsAndNamedStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Projects",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Notebooks",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // Populate project slugs from titles and resolve collisions with numeric suffixes.
            migrationBuilder.Sql(
                """
                WITH ProjectBase AS (
                    SELECT
                        p.Id,
                        BaseSlug = LOWER(LTRIM(RTRIM(
                            REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                                COALESCE(NULLIF(p.Title, ''), 'project'),
                                ' ', '-'),
                                '/', '-'),
                                '\', '-'),
                                ':', '-'),
                                '*', '-'),
                                '?', '-'),
                                '"', '-'),
                                '<', '-'),
                                '>', '-'),
                                '|', '-'),
                                '--', '-')
                        )))
                    FROM Projects p
                ),
                ProjectRanked AS (
                    SELECT
                        pb.Id,
                        pb.BaseSlug,
                        rn = ROW_NUMBER() OVER (PARTITION BY pb.BaseSlug ORDER BY pb.Id)
                    FROM ProjectBase pb
                )
                UPDATE p
                SET Slug = CASE
                    WHEN pr.rn = 1 THEN LEFT(pr.BaseSlug, 100)
                    ELSE LEFT(pr.BaseSlug, 95) + '-' + CAST(pr.rn AS nvarchar(5))
                END
                FROM Projects p
                INNER JOIN ProjectRanked pr ON pr.Id = p.Id;
                """);

            // Populate notebook slugs from titles and resolve per-project collisions with numeric suffixes.
            migrationBuilder.Sql(
                """
                WITH NotebookBase AS (
                    SELECT
                        n.Id,
                        n.ProjectId,
                        BaseSlug = LOWER(LTRIM(RTRIM(
                            REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                                COALESCE(NULLIF(n.Title, ''), 'notebook'),
                                ' ', '-'),
                                '/', '-'),
                                '\', '-'),
                                ':', '-'),
                                '*', '-'),
                                '?', '-'),
                                '"', '-'),
                                '<', '-'),
                                '>', '-'),
                                '|', '-'),
                                '--', '-')
                        )))
                    FROM Notebooks n
                ),
                NotebookRanked AS (
                    SELECT
                        nb.Id,
                        nb.ProjectId,
                        nb.BaseSlug,
                        rn = ROW_NUMBER() OVER (PARTITION BY nb.ProjectId, nb.BaseSlug ORDER BY nb.Id)
                    FROM NotebookBase nb
                )
                UPDATE n
                SET Slug = CASE
                    WHEN nr.rn = 1 THEN LEFT(nr.BaseSlug, 100)
                    ELSE LEFT(nr.BaseSlug, 95) + '-' + CAST(nr.rn AS nvarchar(5))
                END
                FROM Notebooks n
                INNER JOIN NotebookRanked nr ON nr.Id = n.Id;
                """);

            // Regenerate ContentFile DocumentId using stable ProjectId + RelativePath input and
            // update existing DocumentChunks to preserve references.
            migrationBuilder.Sql(
                """
                ;WITH NewIds AS (
                    SELECT
                        cf.Id,
                        NewDocumentId = LOWER(CONVERT(varchar(64), HASHBYTES(
                            'SHA2_256',
                            LOWER(CONVERT(varchar(36), cf.ProjectId) + ':' + REPLACE(COALESCE(cf.RelativePath, ''), '\', '/'))
                        ), 2))
                    FROM ContentFiles cf
                )
                UPDATE cf
                SET cf.DocumentId = ni.NewDocumentId
                FROM ContentFiles cf
                INNER JOIN NewIds ni ON ni.Id = cf.Id;

                UPDATE dc
                SET dc.DocumentId = cf.DocumentId
                FROM DocumentChunks dc
                INNER JOIN ContentFiles cf ON cf.Id = dc.ContentFileId
                WHERE dc.ContentFileId IS NOT NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Projects",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Notebooks",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Slug_Unique",
                table: "Projects",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_ProjectId_Slug_Unique",
                table: "Notebooks",
                columns: new[] { "ProjectId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_Slug_Unique",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Notebooks_ProjectId_Slug_Unique",
                table: "Notebooks");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Notebooks");
        }
    }
}
