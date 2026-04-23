using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddRouterKnobsToLlamaCatalogDownloadIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RouterCacheRamMib",
                table: "LlamaCatalogDownloadIntents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouterContextSize",
                table: "LlamaCatalogDownloadIntents",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RouterCacheRamMib",
                table: "LlamaCatalogDownloadIntents");

            migrationBuilder.DropColumn(
                name: "RouterContextSize",
                table: "LlamaCatalogDownloadIntents");
        }
    }
}
