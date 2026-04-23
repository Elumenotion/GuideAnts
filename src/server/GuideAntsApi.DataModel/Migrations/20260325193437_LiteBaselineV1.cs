using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class LiteBaselineV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessCodeCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    MaxUsesPerCode = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessCodeCampaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantAuthProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    AuthType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Tenant = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HeaderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ValueTemplate = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UserConfigPolicy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAuthProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileLineageEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    FileKind = table.Column<int>(type: "int", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StoragePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileLineageEvents", x => x.Id);
                    table.CheckConstraint("CK_FileLineageEvent_NotebookId", "(FileKind = 1 AND NotebookId IS NOT NULL) OR (FileKind = 0 AND NotebookId IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "JobQueue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    AvailableAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobQueue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    ModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ReasoningChoicesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.ModelId);
                });

            migrationBuilder.CreateTable(
                name: "NotebookTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BillingIntervalUnit = table.Column<int>(type: "int", nullable: false),
                    BillingIntervalCount = table.Column<int>(type: "int", nullable: false),
                    UsageWindowDays = table.Column<int>(type: "int", nullable: false),
                    AllowanceAmount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    SubscriptionPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    StripeProductId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripePriceFlatId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripePriceMeteredId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
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
                name: "Tools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContentFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotebookFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotebookConversationMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvokingAssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgentInvocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Service = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModelDeploymentId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValueInput = table.Column<long>(type: "bigint", nullable: false),
                    ValueCachedInput = table.Column<long>(type: "bigint", nullable: false),
                    ValueReasoning = table.Column<long>(type: "bigint", nullable: false),
                    ValueOutput = table.Column<long>(type: "bigint", nullable: false),
                    ValueOther = table.Column<long>(type: "bigint", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CostUsd = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    MarkupPercent = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    ChargeUsd = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageReportCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageReportCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    IdentityIssuer = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    IdentitySubject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    RedeemedAccessCode = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessCodes_AccessCodeCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "AccessCodeCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AssistantAuthScopes",
                columns: table => new
                {
                    AssistantAuthProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAuthScopes", x => new { x.AssistantAuthProviderId, x.Scope });
                    table.ForeignKey(
                        name: "FK_AssistantAuthScopes_AssistantAuthProviders_AssistantAuthProviderId",
                        column: x => x.AssistantAuthProviderId,
                        principalTable: "AssistantAuthProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsageReportCategoryOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UsageReportCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageReportCategoryOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageReportCategoryOperations_UsageReportCategories_UsageReportCategoryId",
                        column: x => x.UsageReportCategoryId,
                        principalTable: "UsageReportCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                name: "Assistants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerTeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsGlobal = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Temperature = table.Column<float>(type: "real", nullable: true),
                    TopP = table.Column<double>(type: "float", nullable: true),
                    ReasoningEffort = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, comment: "Model-specific reasoning effort value."),
                    ToolResourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AuthConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DefaultAssistant = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    InvocationEvaluator = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false, comment: "AssistantKind discriminator"),
                    AvatarImageBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    AvatarContentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    HomePageMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assistants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assistants_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "ModelId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Assistants_Teams_OwnerTeamId",
                        column: x => x.OwnerTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamCreditTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
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
                    UtcDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    StripeInvoiceItemId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
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
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AutoRenew = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    StripeSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripeItemFlatId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StripeItemMeteredId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
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

            migrationBuilder.CreateTable(
                name: "AssistantContextOptions",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantContextOptions", x => new { x.AssistantId, x.Key });
                    table.ForeignKey(
                        name: "FK_AssistantContextOptions_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantConversationStarters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OrderIndex = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantConversationStarters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantConversationStarters_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VectorStoreName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RelativePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ContentBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantFiles_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantOpenApiSchemas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ApiHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SpecificationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantOpenApiSchemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantOpenApiSchemas_AssistantAuthProviders_AuthProviderId",
                        column: x => x.AuthProviderId,
                        principalTable: "AssistantAuthProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistantOpenApiSchemas_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantTools",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTools", x => new { x.AssistantId, x.ToolId });
                    table.ForeignKey(
                        name: "FK_AssistantTools_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantTools_Tools_ToolId",
                        column: x => x.ToolId,
                        principalTable: "Tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GuideMembers",
                columns: table => new
                {
                    GuideId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuideMembers", x => new { x.GuideId, x.AssistantId });
                    table.ForeignKey(
                        name: "FK_GuideMembers_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuideMembers_Assistants_GuideId",
                        column: x => x.GuideId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantFileMarkdownShadows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalAssistantFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsIndexed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantFileMarkdownShadows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantFileMarkdownShadows_AssistantFiles_OriginalAssistantFileId",
                        column: x => x.OriginalAssistantFileId,
                        principalTable: "AssistantFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantOpenApiOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ToolDefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SchemaFragmentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Updated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantOpenApiOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantOpenApiOperations_AssistantOpenApiSchemas_SchemaId",
                        column: x => x.SchemaId,
                        principalTable: "AssistantOpenApiSchemas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentInvocationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentInvocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ToolCallsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FunctionName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ToolCallId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInvocationMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentInvocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentTurnIndex = table.Column<int>(type: "int", nullable: false),
                    TriggeringToolCallId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ParentInvocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssistantName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ModelDeploymentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContextMessageJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Evaluator = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UsageJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LlmRoundTrips = table.Column<int>(type: "int", nullable: false),
                    ToolCallCount = table.Column<int>(type: "int", nullable: false),
                    Depth = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Completed = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInvocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentInvocations_AgentInvocations_ParentInvocationId",
                        column: x => x.ParentInvocationId,
                        principalTable: "AgentInvocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentInvocations_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContentFileMarkdownShadows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalContentFileVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsIndexed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentFileMarkdownShadows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContentFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DocumentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LatestVersion = table.Column<int>(type: "int", nullable: false),
                    IsSnapshot = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Deleted = table.Column<bool>(type: "bit", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HomePageContentFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_ContentFiles_HomePageContentFileId",
                        column: x => x.HomePageContentFileId,
                        principalTable: "ContentFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Projects_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Links_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectExternalAuths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    AuthType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Tenant = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HeaderName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    HeaderValue = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectExternalAuths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectExternalAuths_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectFolders_ProjectFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "ProjectFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectFolders_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectUserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectRoleId = table.Column<int>(type: "int", nullable: false),
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
                name: "SemiStructuredProjectDatas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Placeholder = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemiStructuredProjectDatas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SemiStructuredProjectDatas_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    TeamRoleId = table.Column<int>(type: "int", nullable: false),
                    ProjectRoleId = table.Column<int>(type: "int", nullable: true),
                    InvitedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccessCode = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
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
                name: "UserProjectContextOption",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProjectContextOption", x => new { x.UserId, x.ProjectId, x.Key });
                    table.ForeignKey(
                        name: "FK_UserProjectContextOption_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserProjectContextOption_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentFileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StoragePath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalRelativePath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalFolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Path = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Indexed = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    OriginVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromNotebook = table.Column<bool>(type: "bit", nullable: false),
                    OriginNotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginNotebookFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentFileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentFileVersions_ContentFileVersions_OriginVersionId",
                        column: x => x.OriginVersionId,
                        principalTable: "ContentFileVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentFileVersions_ContentFiles_ContentFileId",
                        column: x => x.ContentFileId,
                        principalTable: "ContentFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentFileVersions_ProjectFolders_OriginalFolderId",
                        column: x => x.OriginalFolderId,
                        principalTable: "ProjectFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationLocks",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LockedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LockedByUserName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    LockedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationLocks", x => x.ConversationId);
                    table.CheckConstraint("CK_ExpiresInFuture", "[ExpiresAt] > [LockedAt]");
                    table.ForeignKey(
                        name: "FK_ConversationLocks_Users_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnIndex = table.Column<int>(type: "int", nullable: false),
                    AssistantName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ModelDeploymentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChatRunOutputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UsageJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilesCreated = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilesModified = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorkingDirectory = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "completed"),
                    LastUpdated = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationTurns", x => x.Id);
                    table.UniqueConstraint("AK_ConversationTurns_NotebookConversationId_TurnIndex", x => new { x.NotebookConversationId, x.TurnIndex });
                    table.CheckConstraint("CK_Turn_Status", "[Status] IN ('streaming', 'completed', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "DocumentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IndexName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotebookFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssistantFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Embedding = table.Column<string>(type: "vector(1536)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentChunks_AssistantFiles_AssistantFileId",
                        column: x => x.AssistantFileId,
                        principalTable: "AssistantFiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DocumentChunks_ContentFiles_ContentFileId",
                        column: x => x.ContentFileId,
                        principalTable: "ContentFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    OrderIndex = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageEditHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OriginalToolCalls = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FirstEditedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstEditedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageEditHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageEditHistories_Users_FirstEditedByUserId",
                        column: x => x.FirstEditedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotebookConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalUserIdentity = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssistantName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModelDeploymentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ThinkingBlocksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolCalls = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FunctionName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolCallId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    TurnIndex = table.Column<int>(type: "int", nullable: false),
                    MessageSequence = table.Column<int>(type: "int", nullable: false),
                    MessageContentType = table.Column<int>(type: "int", nullable: false),
                    IsEdited = table.Column<bool>(type: "bit", nullable: false),
                    LastEditedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastEditedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsStreaming = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookConversationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotebookConversationMessages_Assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalTable: "Assistants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_NotebookConversationMessages_ConversationTurns_NotebookConversationId_TurnIndex",
                        columns: x => new { x.NotebookConversationId, x.TurnIndex },
                        principalTable: "ConversationTurns",
                        principalColumns: new[] { "NotebookConversationId", "TurnIndex" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotebookConversationMessages_Users_LastEditedByUserId",
                        column: x => x.LastEditedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotebookConversationMessages_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotebookConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotebookFileMarkdownShadows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalNotebookFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsIndexed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookFileMarkdownShadows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotebookFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    LastModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FileHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OriginContentFileVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    DocumentId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotebookFiles_ContentFileVersions_OriginContentFileVersionId",
                        column: x => x.OriginContentFileVersionId,
                        principalTable: "ContentFileVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Notebooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GuideId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HomePageFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HomePageConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    SourceNotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceConversationMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notebooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notebooks_Assistants_GuideId",
                        column: x => x.GuideId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notebooks_NotebookConversationMessages_SourceConversationMessageId",
                        column: x => x.SourceConversationMessageId,
                        principalTable: "NotebookConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notebooks_NotebookConversations_HomePageConversationId",
                        column: x => x.HomePageConversationId,
                        principalTable: "NotebookConversations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Notebooks_NotebookFiles_HomePageFileId",
                        column: x => x.HomePageFileId,
                        principalTable: "NotebookFiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Notebooks_Notebooks_SourceNotebookId",
                        column: x => x.SourceNotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notebooks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotebookLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotebookLinks_Links_LinkId",
                        column: x => x.LinkId,
                        principalTable: "Links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotebookLinks_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotebookSemiStructuredDatas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SemiStructuredProjectDataId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookSemiStructuredDatas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotebookSemiStructuredDatas_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotebookSemiStructuredDatas_SemiStructuredProjectDatas_SemiStructuredProjectDataId",
                        column: x => x.SemiStructuredProjectDataId,
                        principalTable: "SemiStructuredProjectDatas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishedGuides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuideId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    RetentionPeriod = table.Column<int>(type: "int", nullable: true),
                    MaxUserMessageLength = table.Column<int>(type: "int", nullable: true),
                    MaxTurns = table.Column<int>(type: "int", nullable: true),
                    AuthValidationWebhookUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    AuthWebhookTimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    FriendlyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DisplayMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CommandMode = table.Column<bool>(type: "bit", nullable: false),
                    ShowTurnNavigation = table.Column<bool>(type: "bit", nullable: false),
                    Collapsible = table.Column<bool>(type: "bit", nullable: false),
                    ShowConversationStarters = table.Column<bool>(type: "bit", nullable: false),
                    ShowAttachments = table.Column<bool>(type: "bit", nullable: false),
                    ApiKeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DailyChargeLimitUsd = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    BillingPeriodChargeLimitUsd = table.Column<decimal>(type: "decimal(19,4)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedGuides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishedGuides_Assistants_GuideId",
                        column: x => x.GuideId,
                        principalTable: "Assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublishedGuides_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
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

            migrationBuilder.InsertData(
                table: "Tools",
                columns: new[] { "Id", "Category", "Created", "Description", "DisplayName", "DisplayOrder", "IsActive", "ToolType", "Updated" },
                values: new object[,]
                {
                    { new Guid("b0000000-0000-0000-0000-000000000001"), "Search", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Reads a web page and returns markdown of the content.", "Read Web", 1, true, "ReadWeb", null },
                    { new Guid("b0000000-0000-0000-0000-000000000002"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Generate an image using AI.", "Generate Image", 1, true, "generate_image", null },
                    { new Guid("b0000000-0000-0000-0000-000000000003"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Generate a WAV podcast from SSML.", "Generate Podcast", 2, true, "generate_podcast", null },
                    { new Guid("b0000000-0000-0000-0000-000000000004"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Deprecated tool.", "Removed Tool 004", 3, true, "removed_tool_004", null },
                    { new Guid("b0000000-0000-0000-0000-000000000005"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Edit or modify an existing notebook image using AI.", "Edit Image", 4, true, "MakeImageFromImage", null },
                    { new Guid("b0000000-0000-0000-0000-000000000006"), "Media", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Deprecated tool.", "Removed Tool 006", 5, true, "removed_tool_006", null },
                    { new Guid("b0000000-0000-0000-0000-000000000007"), "Code", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Execute PlantUML to generate diagrams.", "Make Diagram", 1, true, "make_diagram", null },
                    { new Guid("b0000000-0000-0000-0000-000000000008"), "Code", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Execute bash in a container.", "Run Bash", 2, true, "run_bash", null },
                    { new Guid("b0000000-0000-0000-0000-000000000009"), "Code", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Execute Python in a container.", "Run Python", 3, true, "run_python", null },
                    { new Guid("b0000000-0000-0000-0000-00000000000a"), "Search", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Search inside the current notebook.", "Search Notebook", 2, true, "search_notebook", null },
                    { new Guid("b0000000-0000-0000-0000-00000000000b"), "Search", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Search inside the whole project.", "Search Project", 3, true, "search_project", null },
                    { new Guid("b0000000-0000-0000-0000-00000000000c"), "Integration", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Set user context options.", "Set Context Options", 1, true, "set_user_context_options", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessCodeCampaigns_Name",
                table: "AccessCodeCampaigns",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessCodes_CampaignId",
                table: "AccessCodes",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessCodes_Code",
                table: "AccessCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvocationMessages_AgentInvocationId_Sequence",
                table: "AgentInvocationMessages",
                columns: new[] { "AgentInvocationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvocations_AssistantId",
                table: "AgentInvocations",
                column: "AssistantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvocations_Created",
                table: "AgentInvocations",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvocations_ParentConversationId_ParentTurnIndex",
                table: "AgentInvocations",
                columns: new[] { "ParentConversationId", "ParentTurnIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInvocations_ParentInvocationId",
                table: "AgentInvocations",
                column: "ParentInvocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAuthProviders_ProviderId",
                table: "AssistantAuthProviders",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConversationStarters_AssistantId_OrderIndex",
                table: "AssistantConversationStarters",
                columns: new[] { "AssistantId", "OrderIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantFileMarkdownShadows_OriginalAssistantFileId",
                table: "AssistantFileMarkdownShadows",
                column: "OriginalAssistantFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantFileMarkdownShadows_Status",
                table: "AssistantFileMarkdownShadows",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantFiles_AssistantId_RelativePath",
                table: "AssistantFiles",
                columns: new[] { "AssistantId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantOpenApiOperations_SchemaId_OperationId",
                table: "AssistantOpenApiOperations",
                columns: new[] { "SchemaId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantOpenApiSchemas_ApiHost",
                table: "AssistantOpenApiSchemas",
                column: "ApiHost");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantOpenApiSchemas_AssistantId_Name",
                table: "AssistantOpenApiSchemas",
                columns: new[] { "AssistantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantOpenApiSchemas_AuthProviderId",
                table: "AssistantOpenApiSchemas",
                column: "AuthProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_IsGlobal_Kind_Name",
                table: "Assistants",
                columns: new[] { "IsGlobal", "Kind", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Assistants_ModelId",
                table: "Assistants",
                column: "ModelId");

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
                name: "IX_AssistantTools_ToolId",
                table: "AssistantTools",
                column: "ToolId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFileMarkdownShadows_OriginalContentFileVersionId",
                table: "ContentFileMarkdownShadows",
                column: "OriginalContentFileVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentFileMarkdownShadows_Status",
                table: "ContentFileMarkdownShadows",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFiles_FolderId",
                table: "ContentFiles",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFiles_ProjectId",
                table: "ContentFiles",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFileVersions_ContentFileId_VersionNumber",
                table: "ContentFileVersions",
                columns: new[] { "ContentFileId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentFileVersions_OriginalFolderId",
                table: "ContentFileVersions",
                column: "OriginalFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFileVersions_OriginNotebookFileId",
                table: "ContentFileVersions",
                column: "OriginNotebookFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFileVersions_OriginNotebookId",
                table: "ContentFileVersions",
                column: "OriginNotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFileVersions_OriginVersionId",
                table: "ContentFileVersions",
                column: "OriginVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationLocks_ExpiresAt",
                table: "ConversationLocks",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationLocks_LockedByUserId",
                table: "ConversationLocks",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurns_NotebookConversationId_LastUpdated",
                table: "ConversationTurns",
                columns: new[] { "NotebookConversationId", "LastUpdated" })
                .Annotation("SqlServer:Include", new[] { "Status", "TurnIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurns_NotebookConversationId_TurnIndex",
                table: "ConversationTurns",
                columns: new[] { "NotebookConversationId", "TurnIndex" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "AssistantName" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_AssistantFileId",
                table: "DocumentChunks",
                column: "AssistantFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_ContentFileId_ChunkIndex",
                table: "DocumentChunks",
                columns: new[] { "ContentFileId", "ChunkIndex" },
                unique: true,
                filter: "[ContentFileId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_IndexName",
                table: "DocumentChunks",
                column: "IndexName");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_NotebookFileId_ChunkIndex",
                table: "DocumentChunks",
                columns: new[] { "NotebookFileId", "ChunkIndex" },
                unique: true,
                filter: "[NotebookFileId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_ProjectId",
                table: "DocumentChunks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_ProjectId_NotebookId",
                table: "DocumentChunks",
                columns: new[] { "ProjectId", "NotebookId" });

            migrationBuilder.CreateIndex(
                name: "IX_FileLineageEvents_FileId_VersionNumber",
                table: "FileLineageEvents",
                columns: new[] { "FileId", "VersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_FileLineageEvents_ProjectId_Timestamp",
                table: "FileLineageEvents",
                columns: new[] { "ProjectId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_GuideMembers_AssistantId",
                table: "GuideMembers",
                column: "AssistantId");

            migrationBuilder.CreateIndex(
                name: "IX_GuideMembers_GuideId_DisplayOrder",
                table: "GuideMembers",
                columns: new[] { "GuideId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_JobQueue_Claiming",
                table: "JobQueue",
                columns: new[] { "Status", "AvailableAt", "Priority", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_JobQueue_CorrelationId",
                table: "JobQueue",
                column: "CorrelationId",
                filter: "[CorrelationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JobQueue_LeaseCleanup",
                table: "JobQueue",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_JobQueue_TypeClaiming",
                table: "JobQueue",
                columns: new[] { "JobType", "Status", "AvailableAt", "Priority", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_Links_ProjectId",
                table: "Links",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_MessageId_NotebookFileId",
                table: "MessageAttachments",
                columns: new[] { "MessageId", "NotebookFileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_MessageId_OrderIndex",
                table: "MessageAttachments",
                columns: new[] { "MessageId", "OrderIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_NotebookFileId",
                table: "MessageAttachments",
                column: "NotebookFileId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageEditHistories_FirstEditedByUserId",
                table: "MessageEditHistories",
                column: "FirstEditedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageEditHistories_MessageId",
                table: "MessageEditHistories",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_IsActive_DisplayOrder",
                table: "Models",
                columns: new[] { "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversationMessages_AssistantId",
                table: "NotebookConversationMessages",
                column: "AssistantId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversationMessages_LastEditedByUserId",
                table: "NotebookConversationMessages",
                column: "LastEditedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversationMessages_NotebookConversationId_Created",
                table: "NotebookConversationMessages",
                columns: new[] { "NotebookConversationId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversationMessages_NotebookConversationId_Role_Created",
                table: "NotebookConversationMessages",
                columns: new[] { "NotebookConversationId", "Role", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversationMessages_NotebookConversationId_TurnIndex_MessageSequence",
                table: "NotebookConversationMessages",
                columns: new[] { "NotebookConversationId", "TurnIndex", "MessageSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversationMessages_UserId_NotebookConversationId",
                table: "NotebookConversationMessages",
                columns: new[] { "UserId", "NotebookConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversations_NotebookId",
                table: "NotebookConversations",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookConversations_NotebookId_Created",
                table: "NotebookConversations",
                columns: new[] { "NotebookId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookFileMarkdownShadows_OriginalNotebookFileId",
                table: "NotebookFileMarkdownShadows",
                column: "OriginalNotebookFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotebookFileMarkdownShadows_Status",
                table: "NotebookFileMarkdownShadows",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookFiles_DocumentId",
                table: "NotebookFiles",
                column: "DocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotebookFiles_NotebookId",
                table: "NotebookFiles",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookFiles_OriginContentFileVersionId",
                table: "NotebookFiles",
                column: "OriginContentFileVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookFiles_RelativePath_NotebookId",
                table: "NotebookFiles",
                columns: new[] { "RelativePath", "NotebookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotebookLinks_LinkId",
                table: "NotebookLinks",
                column: "LinkId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookLinks_NotebookId",
                table: "NotebookLinks",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_GuideId",
                table: "Notebooks",
                column: "GuideId");

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_HomePageConversationId",
                table: "Notebooks",
                column: "HomePageConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_HomePageFileId",
                table: "Notebooks",
                column: "HomePageFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_ProjectId",
                table: "Notebooks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_SourceConversationMessageId",
                table: "Notebooks",
                column: "SourceConversationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_SourceNotebookId",
                table: "Notebooks",
                column: "SourceNotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookSemiStructuredDatas_NotebookId",
                table: "NotebookSemiStructuredDatas",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookSemiStructuredDatas_SemiStructuredProjectDataId",
                table: "NotebookSemiStructuredDatas",
                column: "SemiStructuredProjectDataId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Name",
                table: "Plans",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExternalAuths_ProjectId_ProviderId",
                table: "ProjectExternalAuths",
                columns: new[] { "ProjectId", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFolders_ParentFolderId",
                table: "ProjectFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFolders_ProjectId",
                table: "ProjectFolders",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFolders_RelativePath_ProjectId",
                table: "ProjectFolders",
                columns: new[] { "RelativePath", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_HomePageContentFileId",
                table: "Projects",
                column: "HomePageContentFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TeamId",
                table: "Projects",
                column: "TeamId");

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
                name: "IX_PublishedGuides_Active_Created",
                table: "PublishedGuides",
                columns: new[] { "Active", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishedGuides_AuthValidationWebhookUrl",
                table: "PublishedGuides",
                column: "AuthValidationWebhookUrl",
                filter: "[AuthValidationWebhookUrl] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PublishedGuides_FriendlyName",
                table: "PublishedGuides",
                column: "FriendlyName",
                unique: true,
                filter: "[FriendlyName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PublishedGuides_GuideId",
                table: "PublishedGuides",
                column: "GuideId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishedGuides_GuideId_Active",
                table: "PublishedGuides",
                columns: new[] { "GuideId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishedGuides_NotebookId",
                table: "PublishedGuides",
                column: "NotebookId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SemiStructuredProjectDatas_ProjectId",
                table: "SemiStructuredProjectDatas",
                column: "ProjectId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Tools_Category_IsActive_DisplayOrder",
                table: "Tools",
                columns: new[] { "Category", "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Tools_ToolType",
                table: "Tools",
                column: "ToolType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_AgentInvocationId_Category",
                table: "UsageEvents",
                columns: new[] { "AgentInvocationId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_AssistantId_Created",
                table: "UsageEvents",
                columns: new[] { "AssistantId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_Category_Created",
                table: "UsageEvents",
                columns: new[] { "Category", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_ConversationId_Created",
                table: "UsageEvents",
                columns: new[] { "ConversationId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_Created",
                table: "UsageEvents",
                column: "Created")
                .Annotation("SqlServer:Include", new[] { "ProjectId", "CostUsd", "MarkupPercent", "ChargeUsd" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_CrewUsage",
                table: "UsageEvents",
                column: "AgentInvocationId")
                .Annotation("SqlServer:Include", new[] { "Created", "Category", "ConversationId", "ValueInput", "ValueCachedInput", "ValueReasoning", "ValueOutput", "ChargeUsd" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_GuideInvocationUsage",
                table: "UsageEvents",
                columns: new[] { "InvokingAssistantId", "ProjectId", "Created" })
                .Annotation("SqlServer:Include", new[] { "Category", "Operation", "ConversationId", "ValueInput", "ValueCachedInput", "ValueReasoning", "ValueOutput", "ChargeUsd" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_GuideUsageReport",
                table: "UsageEvents",
                columns: new[] { "AssistantId", "ProjectId", "AgentInvocationId", "Created" })
                .Annotation("SqlServer:Include", new[] { "Category", "Operation", "ConversationId", "ValueInput", "ValueCachedInput", "ValueReasoning", "ValueOutput", "ChargeUsd" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_NotebookId_Created_ForCostLimits",
                table: "UsageEvents",
                columns: new[] { "NotebookId", "Created" })
                .Annotation("SqlServer:Include", new[] { "ChargeUsd" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_ProjectId_Created",
                table: "UsageEvents",
                columns: new[] { "ProjectId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_ProjectId_NotebookId_ForLastActivity",
                table: "UsageEvents",
                columns: new[] { "ProjectId", "NotebookId" })
                .Annotation("SqlServer:Include", new[] { "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_UserId_ProjectId_NotebookId_ConversationId",
                table: "UsageEvents",
                columns: new[] { "UserId", "ProjectId", "NotebookId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageReportCategories_Key",
                table: "UsageReportCategories",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageReportCategoryOperations_Operation",
                table: "UsageReportCategoryOperations",
                column: "Operation");

            migrationBuilder.CreateIndex(
                name: "IX_UsageReportCategoryOperations_UsageReportCategoryId",
                table: "UsageReportCategoryOperations",
                column: "UsageReportCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_UserProjectContextOption_ProjectId_Key",
                table: "UserProjectContextOption",
                columns: new[] { "ProjectId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email_Unique",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Identity_Unique",
                table: "Users",
                columns: new[] { "IdentityIssuer", "IdentitySubject" },
                unique: true,
                filter: "[IdentityIssuer] IS NOT NULL AND [IdentitySubject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RedeemedAccessCode",
                table: "Users",
                column: "RedeemedAccessCode");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentInvocationMessages_AgentInvocations_AgentInvocationId",
                table: "AgentInvocationMessages",
                column: "AgentInvocationId",
                principalTable: "AgentInvocations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentInvocations_NotebookConversations_ParentConversationId",
                table: "AgentInvocations",
                column: "ParentConversationId",
                principalTable: "NotebookConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContentFileMarkdownShadows_ContentFileVersions_OriginalContentFileVersionId",
                table: "ContentFileMarkdownShadows",
                column: "OriginalContentFileVersionId",
                principalTable: "ContentFileVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContentFiles_ProjectFolders_FolderId",
                table: "ContentFiles",
                column: "FolderId",
                principalTable: "ProjectFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ContentFiles_Projects_ProjectId",
                table: "ContentFiles",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContentFileVersions_NotebookFiles_OriginNotebookFileId",
                table: "ContentFileVersions",
                column: "OriginNotebookFileId",
                principalTable: "NotebookFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContentFileVersions_Notebooks_OriginNotebookId",
                table: "ContentFileVersions",
                column: "OriginNotebookId",
                principalTable: "Notebooks",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationLocks_NotebookConversations_ConversationId",
                table: "ConversationLocks",
                column: "ConversationId",
                principalTable: "NotebookConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationTurns_NotebookConversations_NotebookConversationId",
                table: "ConversationTurns",
                column: "NotebookConversationId",
                principalTable: "NotebookConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentChunks_NotebookFiles_NotebookFileId",
                table: "DocumentChunks",
                column: "NotebookFileId",
                principalTable: "NotebookFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageAttachments_NotebookConversationMessages_MessageId",
                table: "MessageAttachments",
                column: "MessageId",
                principalTable: "NotebookConversationMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageAttachments_NotebookFiles_NotebookFileId",
                table: "MessageAttachments",
                column: "NotebookFileId",
                principalTable: "NotebookFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageEditHistories_NotebookConversationMessages_MessageId",
                table: "MessageEditHistories",
                column: "MessageId",
                principalTable: "NotebookConversationMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotebookConversationMessages_NotebookConversations_NotebookConversationId",
                table: "NotebookConversationMessages",
                column: "NotebookConversationId",
                principalTable: "NotebookConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotebookConversations_Notebooks_NotebookId",
                table: "NotebookConversations",
                column: "NotebookId",
                principalTable: "Notebooks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotebookFileMarkdownShadows_NotebookFiles_OriginalNotebookFileId",
                table: "NotebookFileMarkdownShadows",
                column: "OriginalNotebookFileId",
                principalTable: "NotebookFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotebookFiles_Notebooks_NotebookId",
                table: "NotebookFiles",
                column: "NotebookId",
                principalTable: "Notebooks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql(
                @"
                CREATE VIEW ConversationCurrentState AS
                SELECT
                    c.Id,
                    c.NotebookId,
                    c.Title,
                    c.Summary,
                    c.Created,
                    lastTurn.AssistantName AS CurrentAssistantName,
                    lastTurn.ModelDeploymentId AS CurrentModelDeploymentId,
                    lastTurn.Instructions AS LastInstructions,
                    lastTurn.Created AS LastActivity,
                    lastTurn.TurnIndex AS CurrentTurnIndex,
                    lastTurn.FilesCreated AS LastTurnFilesCreated,
                    lastTurn.FilesModified AS LastTurnFilesModified
                FROM NotebookConversations c
                LEFT JOIN (
                    SELECT
                        t1.NotebookConversationId,
                        t1.AssistantName,
                        t1.ModelDeploymentId,
                        t1.Instructions,
                        t1.Created,
                        t1.TurnIndex,
                        t1.FilesCreated,
                        t1.FilesModified
                    FROM ConversationTurns t1
                    WHERE t1.TurnIndex = (
                        SELECT MAX(t2.TurnIndex)
                        FROM ConversationTurns t2
                        WHERE t2.NotebookConversationId = t1.NotebookConversationId
                    )
                ) lastTurn ON c.Id = lastTurn.NotebookConversationId;
                ");

            migrationBuilder.Sql(
                @"
                IF NOT EXISTS (SELECT * FROM sys.fulltext_catalogs WHERE name = 'DocumentChunks_Catalog')
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                BEGIN
                    CREATE FULLTEXT CATALOG DocumentChunks_Catalog;
                END
                ",
                suppressTransaction: true);

            migrationBuilder.Sql(
                @"
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1 AND NOT EXISTS (SELECT * FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('DocumentChunks'))
                BEGIN
                    CREATE FULLTEXT INDEX ON DocumentChunks(Content)
                    KEY INDEX PK_DocumentChunks
                    ON DocumentChunks_Catalog;
                END
                ",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1 AND EXISTS (SELECT * FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('DocumentChunks'))
                BEGIN
                    DROP FULLTEXT INDEX ON DocumentChunks;
                END
                ",
                suppressTransaction: true);

            migrationBuilder.Sql(
                @"
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1 AND EXISTS (SELECT * FROM sys.fulltext_catalogs WHERE name = 'DocumentChunks_Catalog')
                AND NOT EXISTS (
                    SELECT *
                    FROM sys.fulltext_indexes
                    WHERE fulltext_catalog_id = (
                        SELECT fulltext_catalog_id
                        FROM sys.fulltext_catalogs
                        WHERE name = 'DocumentChunks_Catalog'
                    )
                )
                    DROP FULLTEXT CATALOG DocumentChunks_Catalog;
                ",
                suppressTransaction: true);

            migrationBuilder.Sql("DROP VIEW IF EXISTS ConversationCurrentState;");

            migrationBuilder.DropForeignKey(
                name: "FK_NotebookConversationMessages_Assistants_AssistantId",
                table: "NotebookConversationMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_Notebooks_Assistants_GuideId",
                table: "Notebooks");

            migrationBuilder.DropForeignKey(
                name: "FK_ConversationTurns_NotebookConversations_NotebookConversationId",
                table: "ConversationTurns");

            migrationBuilder.DropForeignKey(
                name: "FK_NotebookConversationMessages_NotebookConversations_NotebookConversationId",
                table: "NotebookConversationMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_Notebooks_NotebookConversations_HomePageConversationId",
                table: "Notebooks");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Teams_TeamId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_NotebookFiles_ContentFileVersions_OriginContentFileVersionId",
                table: "NotebookFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_ContentFiles_ProjectFolders_FolderId",
                table: "ContentFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_ContentFiles_Projects_ProjectId",
                table: "ContentFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_Notebooks_Projects_ProjectId",
                table: "Notebooks");

            migrationBuilder.DropForeignKey(
                name: "FK_Notebooks_NotebookFiles_HomePageFileId",
                table: "Notebooks");

            migrationBuilder.DropTable(
                name: "AccessCodes");

            migrationBuilder.DropTable(
                name: "AgentInvocationMessages");

            migrationBuilder.DropTable(
                name: "AssistantAuthScopes");

            migrationBuilder.DropTable(
                name: "AssistantContextOptions");

            migrationBuilder.DropTable(
                name: "AssistantConversationStarters");

            migrationBuilder.DropTable(
                name: "AssistantFileMarkdownShadows");

            migrationBuilder.DropTable(
                name: "AssistantOpenApiOperations");

            migrationBuilder.DropTable(
                name: "AssistantTools");

            migrationBuilder.DropTable(
                name: "ContentFileMarkdownShadows");

            migrationBuilder.DropTable(
                name: "ConversationLocks");

            migrationBuilder.DropTable(
                name: "DocumentChunks");

            migrationBuilder.DropTable(
                name: "FileLineageEvents");

            migrationBuilder.DropTable(
                name: "GuideMembers");

            migrationBuilder.DropTable(
                name: "JobQueue");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropTable(
                name: "MessageEditHistories");

            migrationBuilder.DropTable(
                name: "NotebookFileMarkdownShadows");

            migrationBuilder.DropTable(
                name: "NotebookLinks");

            migrationBuilder.DropTable(
                name: "NotebookSemiStructuredDatas");

            migrationBuilder.DropTable(
                name: "NotebookTemplates");

            migrationBuilder.DropTable(
                name: "ProjectExternalAuths");

            migrationBuilder.DropTable(
                name: "ProjectUserRoles");

            migrationBuilder.DropTable(
                name: "PublishedGuides");

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
                name: "UsageEvents");

            migrationBuilder.DropTable(
                name: "UsageReportCategoryOperations");

            migrationBuilder.DropTable(
                name: "UserProjectContextOption");

            migrationBuilder.DropTable(
                name: "AccessCodeCampaigns");

            migrationBuilder.DropTable(
                name: "AgentInvocations");

            migrationBuilder.DropTable(
                name: "AssistantOpenApiSchemas");

            migrationBuilder.DropTable(
                name: "Tools");

            migrationBuilder.DropTable(
                name: "AssistantFiles");

            migrationBuilder.DropTable(
                name: "Links");

            migrationBuilder.DropTable(
                name: "SemiStructuredProjectDatas");

            migrationBuilder.DropTable(
                name: "ProjectRoles");

            migrationBuilder.DropTable(
                name: "TeamRoles");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "UsageReportCategories");

            migrationBuilder.DropTable(
                name: "AssistantAuthProviders");

            migrationBuilder.DropTable(
                name: "Assistants");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "NotebookConversations");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "ContentFileVersions");

            migrationBuilder.DropTable(
                name: "ProjectFolders");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "ContentFiles");

            migrationBuilder.DropTable(
                name: "NotebookFiles");

            migrationBuilder.DropTable(
                name: "Notebooks");

            migrationBuilder.DropTable(
                name: "NotebookConversationMessages");

            migrationBuilder.DropTable(
                name: "ConversationTurns");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
