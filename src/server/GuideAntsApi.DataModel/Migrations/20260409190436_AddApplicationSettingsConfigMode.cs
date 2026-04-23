using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationSettingsConfigMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ApplicationSettings",
                table: "ApplicationSettings");

            migrationBuilder.AddColumn<string>(
                name: "ConfigMode",
                table: "ApplicationSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "docker");

            migrationBuilder.Sql("""
                UPDATE dbo.ApplicationSettings
                SET ConfigMode = 'docker'
                WHERE ConfigMode IS NULL OR LTRIM(RTRIM(ConfigMode)) = '';
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ApplicationSettings",
                table: "ApplicationSettings",
                columns: new[] { "SectionName", "ConfigMode" });

            migrationBuilder.Sql("""
                INSERT INTO dbo.ApplicationSettings (SectionName, ConfigMode, JsonValue, SchemaVersion, CreatedUtc, UpdatedUtc)
                SELECT s.SectionName, 'local', s.JsonValue, s.SchemaVersion, s.CreatedUtc, s.UpdatedUtc
                FROM dbo.ApplicationSettings s
                WHERE s.ConfigMode = 'docker'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM dbo.ApplicationSettings x
                      WHERE x.SectionName = s.SectionName
                        AND x.ConfigMode = 'local'
                  );
                """);

            migrationBuilder.Sql("""
                UPDATE dbo.ApplicationSettings
                SET JsonValue = JSON_MODIFY(JsonValue, '$.BaseUrl', 'http://localhost:8110/llama-cpp'),
                    UpdatedUtc = SYSUTCDATETIME()
                WHERE SectionName = 'LlamaCpp'
                  AND ConfigMode = 'local';
                """);

            migrationBuilder.Sql("""
                UPDATE dbo.ApplicationSettings
                SET JsonValue = JSON_MODIFY(
                        JSON_MODIFY(
                            JsonValue,
                            '$.Containers."guideants-ai".BaseUrl',
                            'http://localhost:8110/sandbox'
                        ),
                        '$.Containers.plantuml.BaseUrl',
                        'http://localhost:8111'
                    ),
                    UpdatedUtc = SYSUTCDATETIME()
                WHERE SectionName = 'ServiceRouting'
                  AND ConfigMode = 'local';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ApplicationSettings",
                table: "ApplicationSettings");

            migrationBuilder.Sql("""
                DELETE FROM dbo.ApplicationSettings
                WHERE ConfigMode <> 'docker';
                """);

            migrationBuilder.DropColumn(
                name: "ConfigMode",
                table: "ApplicationSettings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ApplicationSettings",
                table: "ApplicationSettings",
                column: "SectionName");
        }
    }
}
