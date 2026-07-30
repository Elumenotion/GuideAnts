using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class BackfillNonLocalModelRowAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE m
                SET
                    SamplingParametersJson = CASE
                        WHEN m.SamplingParametersJson IS NULL
                            OR LTRIM(RTRIM(m.SamplingParametersJson)) IN ('', '{}')
                            THEN COALESCE(rp.SamplingParametersJson, '{}')
                        ELSE m.SamplingParametersJson
                    END,
                    ReasoningChoicesJson = COALESCE(
                        m.ReasoningChoicesJson,
                        (
                            SELECT JSON_QUERY('[' + STRING_AGG(QUOTENAME([key], '"'), ',') + ']')
                            FROM OPENJSON(JSON_QUERY(COALESCE(rp.ThinkingControlJson, '{}'), '$.choiceActions'))
                        )),
                    RuntimeConfigJson = NULL,
                    Updated = SYSUTCDATETIME()
                FROM Models m
                LEFT JOIN RuntimeProfiles rp ON rp.ProfileId = JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfileId')
                WHERE m.Provider <> 'llama-cpp'
                  AND JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfileId') IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
