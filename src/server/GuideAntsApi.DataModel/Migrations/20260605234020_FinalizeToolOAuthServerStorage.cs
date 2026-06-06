using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeToolOAuthServerStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalOAuthTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    AccessTokenEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RefreshTokenEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalOAuthTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalOAuthTokens_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExternalOAuthTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthAuthorizationStates",
                columns: table => new
                {
                    State = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CodeVerifier = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Tenant = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RedirectUri = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ReturnUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthAuthorizationStates", x => x.State);
                    table.ForeignKey(
                        name: "FK_OAuthAuthorizationStates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalOAuthTokens_ExpiresAt",
                table: "ExternalOAuthTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalOAuthTokens_ProjectId",
                table: "ExternalOAuthTokens",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalOAuthTokens_UserId_ProviderId_Unique",
                table: "ExternalOAuthTokens",
                columns: new[] { "UserId", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizationStates_ExpiresAt",
                table: "OAuthAuthorizationStates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizationStates_UserId_ProviderId",
                table: "OAuthAuthorizationStates",
                columns: new[] { "UserId", "ProviderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalOAuthTokens");

            migrationBuilder.DropTable(
                name: "OAuthAuthorizationStates");
        }
    }
}
