using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantSamplingParametersJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SamplingParametersJson",
                table: "Assistants",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE dbo.Assistants
                SET SamplingParametersJson =
                    CASE
                        WHEN Temperature IS NOT NULL AND TopP IS NOT NULL THEN
                            '{""temperature"":' + CAST(Temperature AS nvarchar(20)) + ',""top_p"":' + CAST(TopP AS nvarchar(20)) + '}'
                        WHEN Temperature IS NOT NULL THEN
                            '{""temperature"":' + CAST(Temperature AS nvarchar(20)) + '}'
                        WHEN TopP IS NOT NULL THEN
                            '{""top_p"":' + CAST(TopP AS nvarchar(20)) + '}'
                        ELSE NULL
                    END
                WHERE Temperature IS NOT NULL OR TopP IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SamplingParametersJson",
                table: "Assistants");
        }
    }
}
