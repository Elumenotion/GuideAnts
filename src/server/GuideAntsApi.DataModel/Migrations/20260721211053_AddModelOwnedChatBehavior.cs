using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddModelOwnedChatBehavior : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CombineSystemAndDeveloperMessages",
                table: "Models",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestFieldsWhenToolsPresentJson",
                table: "Models",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "SamplingParametersJson",
                table: "Models",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "ThinkingControlJson",
                table: "Models",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "ThoughtBlockPattern",
                table: "Models",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE m
                SET
                    CombineSystemAndDeveloperMessages = COALESCE(
                        TRY_CAST(JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfile.combineSystemAndDeveloperMessages') AS bit),
                        rp.CombineSystemAndDeveloperMessages,
                        1),
                    ThoughtBlockPattern = COALESCE(
                        JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfile.thoughtBlockPattern'),
                        rp.ThoughtBlockPattern),
                    SamplingParametersJson = COALESCE(
                        JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfile.samplingParametersJson'),
                        rp.SamplingParametersJson,
                        '{}'),
                    ThinkingControlJson = COALESCE(
                        JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfile.thinkingControlJson'),
                        rp.ThinkingControlJson,
                        '{}'),
                    RequestFieldsWhenToolsPresentJson = COALESCE(
                        JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfile.requestFieldsWhenToolsPresentJson'),
                        rp.RequestFieldsWhenToolsPresentJson,
                        '{}'),
                    ReasoningChoicesJson = COALESCE(
                        m.ReasoningChoicesJson,
                        (
                            SELECT JSON_QUERY('[' + STRING_AGG(QUOTENAME([key], '"'), ',') + ']')
                            FROM OPENJSON(JSON_QUERY(COALESCE(
                                JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfile.thinkingControlJson'),
                                rp.ThinkingControlJson,
                                '{}'), '$.choiceActions'))
                        )),
                    RuntimeConfigJson = CASE
                        WHEN m.RuntimeConfigJson IS NULL THEN NULL
                        ELSE JSON_MODIFY(
                            JSON_MODIFY(m.RuntimeConfigJson, '$.runtimeProfileId', NULL),
                            '$.runtimeProfile',
                            NULL)
                    END,
                    Updated = SYSUTCDATETIME()
                FROM Models m
                LEFT JOIN LocalModelInstallations lmi ON lmi.ModelId = m.ModelId
                LEFT JOIN RuntimeProfiles rp ON rp.ProfileId = COALESCE(
                    JSON_VALUE(m.RuntimeConfigJson, '$.runtimeProfileId'),
                    lmi.RuntimeProfileId)
                WHERE m.Provider = 'llama-cpp';

                UPDATE Models
                SET RuntimeConfigJson = JSON_OBJECT('routerModelId': JSON_VALUE(RuntimeConfigJson, '$.routerModelId'))
                WHERE Provider = 'llama-cpp'
                  AND RuntimeConfigJson IS NOT NULL
                  AND JSON_VALUE(RuntimeConfigJson, '$.routerModelId') IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "RuntimeProfileId",
                table: "LocalModelInstallations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RuntimeProfileId",
                table: "LocalModelInstallations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.DropColumn(
                name: "CombineSystemAndDeveloperMessages",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "RequestFieldsWhenToolsPresentJson",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SamplingParametersJson",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ThinkingControlJson",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ThoughtBlockPattern",
                table: "Models");
        }
    }
}
