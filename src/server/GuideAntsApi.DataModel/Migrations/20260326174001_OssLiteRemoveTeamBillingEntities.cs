using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class OssLiteRemoveTeamBillingEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill OwnerUserId, clear team FK columns, empty soon-to-be-dropped tables.
            migrationBuilder.Sql(@"
SET NOCOUNT ON;

-- 1. Backfill OwnerUserId from team creator where still null
UPDATE p
SET    p.OwnerUserId = t.CreatedByUserId
FROM   dbo.Projects p
INNER JOIN dbo.Teams t ON t.Id = p.TeamId
WHERE  p.OwnerUserId IS NULL;

UPDATE a
SET    a.OwnerUserId = t.CreatedByUserId
FROM   dbo.Assistants a
INNER JOIN dbo.Teams t ON t.Id = a.OwnerTeamId
WHERE  a.OwnerUserId IS NULL;

-- 2. Clear FK columns that point to Teams (columns being dropped)
UPDATE dbo.Projects SET TeamId = NULL;
UPDATE dbo.Assistants SET OwnerTeamId = NULL;

-- 3. Empty all tables that will be dropped (child tables first)
DELETE FROM dbo.ProjectUserRoles;
DELETE FROM dbo.TeamCreditTransactions;
DELETE FROM dbo.TeamCreditWallets;
DELETE FROM dbo.TeamDailyInvoiceItems;
DELETE FROM dbo.TeamInvitations;
DELETE FROM dbo.TeamMemberships;
DELETE FROM dbo.TeamPlanSubscriptions;
DELETE FROM dbo.Teams;
");

            migrationBuilder.DropForeignKey(
                name: "FK_Assistants_Teams_OwnerTeamId",
                table: "Assistants");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Teams_TeamId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "ProjectUserRoles");

            migrationBuilder.DropTable(
                name: "TeamCreditTransactions");

            migrationBuilder.DropTable(
                name: "TeamCreditWallets");

            migrationBuilder.DropTable(
                name: "TeamDailyInvoiceItems");

            migrationBuilder.DropTable(
                name: "TeamInvitations");

            migrationBuilder.DropTable(
                name: "TeamMemberships");

            migrationBuilder.DropTable(
                name: "TeamPlanSubscriptions");

            migrationBuilder.DropTable(
                name: "ProjectRoles");

            migrationBuilder.DropTable(
                name: "TeamRoles");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_Projects_TeamId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Assistants_OwnerTeamId_Kind",
                table: "Assistants");

            migrationBuilder.DropIndex(
                name: "IX_Assistants_OwnerTeamId_Name",
                table: "Assistants");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OwnerTeamId",
                table: "Assistants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "Projects",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerTeamId",
                table: "Assistants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllowanceAmount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    BillingIntervalCount = table.Column<int>(type: "int", nullable: false),
                    BillingIntervalUnit = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StripePriceFlatId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripePriceMeteredId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripeProductId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SubscriptionPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    UsageWindowDays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StripeCustomerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Teams_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectUserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectRoleId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectUserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectUserRoles_ProjectRoles_ProjectRoleId",
                        column: x => x.ProjectRoleId,
                        principalTable: "ProjectRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectUserRoles_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectUserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamCreditTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamCreditTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamCreditTransactions_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamCreditWallets",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BalanceAmount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamCreditWallets", x => x.TeamId);
                    table.ForeignKey(
                        name: "FK_TeamCreditWallets_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamDailyInvoiceItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    StripeInvoiceItemId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtcDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamDailyInvoiceItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamDailyInvoiceItems_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProjectRoleId = table.Column<int>(type: "int", nullable: true),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamRoleId = table.Column<int>(type: "int", nullable: false),
                    AccessCode = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_ProjectRoles_ProjectRoleId",
                        column: x => x.ProjectRoleId,
                        principalTable: "ProjectRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_TeamRoles_TeamRoleId",
                        column: x => x.TeamRoleId,
                        principalTable: "TeamRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_Users_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamMemberships",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamRoleId = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMemberships", x => new { x.TeamId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TeamMemberships_TeamRoles_TeamRoleId",
                        column: x => x.TeamRoleId,
                        principalTable: "TeamRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamMemberships_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamPlanSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AutoRenew = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    EffectiveEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EffectiveStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StripeItemFlatId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripeItemMeteredId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripeSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamPlanSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamPlanSubscriptions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamPlanSubscriptions_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Plans",
                columns: new[] { "Id", "AllowanceAmount", "BillingIntervalCount", "BillingIntervalUnit", "Created", "CurrencyCode", "IsActive", "Name", "StripePriceFlatId", "StripePriceMeteredId", "StripeProductId", "SubscriptionPrice", "UsageWindowDays" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), 10.00m, 1, 0, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", true, "Free", null, null, null, 0.00m, 60 },
                    { new Guid("22222222-2222-2222-2222-222222222222"), 10.00m, 1, 1, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", true, "Monthly Pro", null, null, null, 10.00m, 30 },
                    { new Guid("33333333-3333-3333-3333-333333333333"), 10.00m, 1, 2, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", true, "Annual Pro (Save $20)", null, null, null, 100.00m, 365 }
                });

            migrationBuilder.InsertData(
                table: "ProjectRoles",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "Owner" },
                    { 2, "Contributor" },
                    { 3, "Reader" }
                });

            migrationBuilder.InsertData(
                table: "TeamRoles",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "Owner" },
                    { 2, "Admin" },
                    { 3, "Member" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TeamId",
                table: "Projects",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_OwnerTeamId_Kind",
                table: "Assistants",
                columns: new[] { "OwnerTeamId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_OwnerTeamId_Name",
                table: "Assistants",
                columns: new[] { "OwnerTeamId", "Name" },
                unique: true,
                filter: "[OwnerTeamId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Name",
                table: "Plans",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectUserRoles_ProjectId_UserId_ProjectRoleId",
                table: "ProjectUserRoles",
                columns: new[] { "ProjectId", "UserId", "ProjectRoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectUserRoles_ProjectRoleId",
                table: "ProjectUserRoles",
                column: "ProjectRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectUserRoles_UserId",
                table: "ProjectUserRoles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamCreditTransactions_TeamId_Created",
                table: "TeamCreditTransactions",
                columns: new[] { "TeamId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamCreditTransactions_TeamId_ReferenceId",
                table: "TeamCreditTransactions",
                columns: new[] { "TeamId", "ReferenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamDailyInvoiceItems_TeamId_UtcDate",
                table: "TeamDailyInvoiceItems",
                columns: new[] { "TeamId", "UtcDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_AccessCode",
                table: "TeamInvitations",
                column: "AccessCode");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_InvitedByUserId",
                table: "TeamInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_PendingUnique_Project",
                table: "TeamInvitations",
                columns: new[] { "TeamId", "ProjectId", "Email" },
                unique: true,
                filter: "[Status] = 0 AND [ProjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_PendingUnique_Team",
                table: "TeamInvitations",
                columns: new[] { "TeamId", "Email" },
                unique: true,
                filter: "[Status] = 0 AND [ProjectId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_ProjectId",
                table: "TeamInvitations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_ProjectRoleId",
                table: "TeamInvitations",
                column: "ProjectRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_TeamRoleId",
                table: "TeamInvitations",
                column: "TeamRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_TeamRoleId",
                table: "TeamMemberships",
                column: "TeamRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_UserId",
                table: "TeamMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamPlanSubscriptions_ActivePerTeam",
                table: "TeamPlanSubscriptions",
                column: "TeamId",
                unique: true,
                filter: "[Status] = 0 AND [EffectiveEnd] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamPlanSubscriptions_PlanId",
                table: "TeamPlanSubscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamPlanSubscriptions_TeamId_EffectiveStart",
                table: "TeamPlanSubscriptions",
                columns: new[] { "TeamId", "EffectiveStart" });

            migrationBuilder.CreateIndex(
                name: "IX_Teams_CreatedByUserId",
                table: "Teams",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Assistants_Teams_OwnerTeamId",
                table: "Assistants",
                column: "OwnerTeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Teams_TeamId",
                table: "Projects",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
