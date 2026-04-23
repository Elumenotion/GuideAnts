using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class RenameWebSearchToolType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE [Tools]
                SET [ToolType] = 'WebSearch'
                WHERE [Id] = 'b0000000-0000-0000-0000-00000000000d';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE [Tools]
                SET [ToolType] = 'WebSearch'
                WHERE [Id] = 'b0000000-0000-0000-0000-00000000000d';
                """);
        }
    }
}
