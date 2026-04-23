using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260415112500_RemoveLegacySearchApplicationSetting")]
    public partial class RemoveLegacySearchApplicationSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM dbo.ApplicationSettings
                WHERE SectionName = 'Search';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM dbo.ApplicationSettings WHERE SectionName = 'Search')
                BEGIN
                    INSERT INTO dbo.ApplicationSettings
                    (
                        SectionName,
                        JsonValue,
                        SchemaVersion,
                        CreatedUtc,
                        UpdatedUtc
                    )
                    VALUES
                    (
                        'Search',
                        '{}',
                        1,
                        SYSUTCDATETIME(),
                        SYSUTCDATETIME()
                    );
                END;
                """);
        }
    }
}
