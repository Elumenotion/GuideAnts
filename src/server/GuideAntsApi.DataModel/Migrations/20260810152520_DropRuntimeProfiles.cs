using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class DropRuntimeProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeProfiles",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CombineSystemAndDeveloperMessages = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ProvidersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestFieldsWhenToolsPresentJson = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "{}"),
                    SamplingParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ThinkingControlJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ThoughtBlockPattern = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeProfiles", x => x.ProfileId);
                });
        }
    }
}
