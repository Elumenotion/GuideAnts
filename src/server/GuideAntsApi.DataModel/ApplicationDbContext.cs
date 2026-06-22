using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.DataModel
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
    {
        public static ApplicationDbContext Factory()
        {
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings:DefaultConnection")
                ?? throw new Exception("Connection string 'ConnectionStrings:DefaultConnection' not found in environment.");
            builder.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                sql.CommandTimeout(60);
            });
            return new ApplicationDbContext(builder.Options);
        }

        public DbSet<User> Users { get; set; } = null!;
        public DbSet<UserRole> UserRoles { get; set; } = null!;
        public DbSet<Project> Projects { get; set; } = null!;
        public DbSet<ProjectAssistantEnvironment> ProjectAssistantEnvironments { get; set; } = null!;
        
        public DbSet<NotebookTemplate> NotebookTemplates { get; set; } = null!;
        public DbSet<Notebook> Notebooks { get; set; } = null!;
        public DbSet<ContentFile> ContentFiles { get; set; } = null!;
        public DbSet<ProjectFolder> ProjectFolders { get; set; } = null!;
        public DbSet<Link> Links { get; set; } = null!;
        public DbSet<ProjectExternalAuth> ProjectExternalAuths { get; set; } = null!;
        public DbSet<ExternalOAuthToken> ExternalOAuthTokens { get; set; } = null!;
        public DbSet<OAuthAuthorizationState> OAuthAuthorizationStates { get; set; } = null!;
        public DbSet<NotebookLink> NotebookLinks { get; set; } = null!;
        public DbSet<SemiStructuredProjectData> SemiStructuredProjectDatas { get; set; } = null!;
        public DbSet<NotebookSemiStructuredData> NotebookSemiStructuredDatas { get; set; } = null!;
        public DbSet<NotebookConversation> NotebookConversations { get; set; } = null!;
        public DbSet<NotebookConversationMessage> NotebookConversationMessages { get; set; } = null!;
        public DbSet<ConversationTurn> ConversationTurns { get; set; } = null!;
        public DbSet<ConversationLock> ConversationLocks { get; set; } = null!;
        public DbSet<AgentInvocation> AgentInvocations { get; set; } = null!;
        public DbSet<AgentInvocationMessage> AgentInvocationMessages { get; set; } = null!;
        public DbSet<MessageEditHistory> MessageEditHistories { get; set; } = null!;
        public DbSet<MessageAttachment> MessageAttachments { get; set; } = null!;
        
        // Keyless entity for conversation current state view
        public DbSet<ConversationCurrentState> ConversationCurrentState { get; set; } = null!;
        public DbSet<ContentFileVersion> ContentFileVersions { get; set; } = null!;
        public DbSet<ContentFileMarkdownShadow> ContentFileMarkdownShadows { get; set; } = null!;
        public DbSet<NotebookFile> NotebookFiles { get; set; } = null!;
        public DbSet<NotebookFileMarkdownShadow> NotebookFileMarkdownShadows { get; set; } = null!;
        public DbSet<DocumentChunk> DocumentChunks { get; set; } = null!;
        public DbSet<FileLineageEvent> FileLineageEvents { get; set; } = null!;
        public DbSet<UsageEvent> UsageEvents { get; set; } = null!;
        public DbSet<UsageReportCategory> UsageReportCategories { get; set; } = null!;
        public DbSet<UsageReportCategoryOperation> UsageReportCategoryOperations { get; set; } = null!;
        public DbSet<JobQueue> JobQueue { get; set; } = null!;
        public DbSet<UserProjectContextOption> UserProjectContextOptions { get; set; } = null!;
        public DbSet<Model> Models { get; set; } = null!;
        public DbSet<Tool> Tools { get; set; } = null!;
        public DbSet<ExcludedHost> ExcludedHosts { get; set; } = null!;
        public DbSet<Assistant> Assistants { get; set; } = null!;
        public DbSet<GuideMember> GuideMembers { get; set; } = null!;
        public DbSet<AssistantTool> AssistantTools { get; set; } = null!;
        public DbSet<AssistantOpenApiSchema> AssistantOpenApiSchemas { get; set; } = null!;
        public DbSet<AssistantOpenApiOperation> AssistantOpenApiOperations { get; set; } = null!;
        public DbSet<AssistantFile> AssistantFiles { get; set; } = null!;
        public DbSet<AssistantFileMarkdownShadow> AssistantFileMarkdownShadows { get; set; } = null!;
        public DbSet<AssistantContextOption> AssistantContextOptions { get; set; } = null!;
        public DbSet<AssistantAuthProvider> AssistantAuthProviders { get; set; } = null!;
        public DbSet<AssistantAuthScope> AssistantAuthScopes { get; set; } = null!;
        public DbSet<AssistantConversationStarter> AssistantConversationStarters { get; set; } = null!;
        public DbSet<PublishedGuide> PublishedGuides { get; set; } = null!;
        public DbSet<ApplicationSetting> ApplicationSettings { get; set; } = null!;
        public DbSet<RuntimeProfile> RuntimeProfiles { get; set; } = null!;
        public DbSet<HostFolderMount> HostFolderMounts { get; set; } = null!;
        public DbSet<HostFolderMountLink> HostFolderMountLinks { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ------------------------------------------------------------
            // Configure Created column for all entities
            // ------------------------------------------------------------
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (entityType.FindProperty("Created") != null)
                {
                    modelBuilder.Entity(entityType.Name)
                        .Property("Created")
                        .HasDefaultValueSql("GETUTCDATE()");
                }
            }


            // ------------------------------------------------------------
            // Configure unique indexes
            // ------------------------------------------------------------
            // User email uniqueness (case-insensitive)
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique()
                .HasDatabaseName("IX_Users_Email_Unique");

            // User identity provider uniqueness when both issuer and subject are provided
            modelBuilder.Entity<User>()
                .HasIndex(u => new { u.IdentityIssuer, u.IdentitySubject })
                .IsUnique()
                .HasFilter("[IdentityIssuer] IS NOT NULL AND [IdentitySubject] IS NOT NULL")
                .HasDatabaseName("IX_Users_Identity_Unique");

            modelBuilder.Entity<UserRole>()
                .HasIndex(ur => ur.UserId)
                .IsUnique()
                .HasDatabaseName("IX_UserRoles_UserId_Unique");

            modelBuilder.Entity<User>()
                .Property(u => u.SecurityStamp)
                .HasDefaultValueSql("NEWID()");

            modelBuilder.Entity<User>()
                .Property(u => u.MustChangePassword)
                .HasDefaultValue(false);

            modelBuilder.Entity<ProjectFolder>()
                .HasIndex(pf => new { pf.RelativePath, pf.ProjectId })
                .IsUnique();

            modelBuilder.Entity<Project>()
                .HasIndex(p => p.Slug)
                .IsUnique()
                .HasDatabaseName("IX_Projects_Slug_Unique");

            modelBuilder.Entity<Project>()
                .Property(p => p.IsSystemProject)
                .HasDefaultValue(false);

            modelBuilder.Entity<Notebook>()
                .HasIndex(n => new { n.ProjectId, n.Slug })
                .IsUnique()
                .HasDatabaseName("IX_Notebooks_ProjectId_Slug_Unique");


            // ------------------------------------------------------------
            // Configure JobQueue entity and indexes
            // ------------------------------------------------------------
            modelBuilder.Entity<JobQueue>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.JobType).HasMaxLength(64).IsRequired();
                b.Property(x => x.Status).HasConversion<byte>();
                
                // No explicit timestamp configuration needed - the existing convention-based
                // loop above will automatically configure the "Created" property
                // with GETUTCDATE() since it follows the established naming convention
                
                // Optimized indexes for job claiming performance
                b.HasIndex(x => new { x.Status, x.AvailableAt, x.Priority, x.Created })
                 .HasDatabaseName("IX_JobQueue_Claiming");
                
                b.HasIndex(x => new { x.JobType, x.Status, x.AvailableAt, x.Priority, x.Created })
                 .HasDatabaseName("IX_JobQueue_TypeClaiming");
                 
                // For lease cleanup operations
                b.HasIndex(x => new { x.Status, x.LeaseUntil })
                 .HasDatabaseName("IX_JobQueue_LeaseCleanup");
                 
                // For correlation tracking (filtered for non-null)
                b.HasIndex(x => x.CorrelationId)
                 .HasFilter("[CorrelationId] IS NOT NULL");
            });
            
            modelBuilder.Entity<ContentFileVersion>()
                .HasIndex(cfv => new { cfv.ContentFileId, cfv.VersionNumber })
                .IsUnique();

            modelBuilder.Entity<ContentFileMarkdownShadow>()
                .HasIndex(cfs => cfs.OriginalContentFileVersionId)
                .IsUnique();

            modelBuilder.Entity<ContentFileMarkdownShadow>()
                .HasIndex(cfs => cfs.Status);

            modelBuilder.Entity<NotebookFileMarkdownShadow>()
                .HasIndex(nfs => nfs.OriginalNotebookFileId)
                .IsUnique();

            modelBuilder.Entity<NotebookFileMarkdownShadow>()
                .HasIndex(nfs => nfs.Status);

            modelBuilder.Entity<AssistantFileMarkdownShadow>()
                .HasIndex(afs => afs.OriginalAssistantFileId)
                .IsUnique();

            modelBuilder.Entity<AssistantFileMarkdownShadow>()
                .HasIndex(afs => afs.Status);

            // ------------------------------------------------------------
            // DocumentChunk configuration
            // ------------------------------------------------------------
            modelBuilder.Entity<DocumentChunk>(b =>
            {
                b.HasKey(c => c.Id);

                // Relationships
                b.HasOne(c => c.ContentFile)
                 .WithMany()
                 .HasForeignKey(c => c.ContentFileId)
                 .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(c => c.NotebookFile)
                 .WithMany()
                 .HasForeignKey(c => c.NotebookFileId)
                 .OnDelete(DeleteBehavior.Cascade);

                // Properties
                b.Property(c => c.IndexName)
                 .IsRequired()
                 .HasMaxLength(255);

                b.Property(c => c.Embedding)
                 .HasColumnType("vector(1536)");

                b.Property(c => c.Content).IsRequired();

                // Indexes
                b.HasIndex(c => c.IndexName);
                b.HasIndex(c => c.ProjectId);
                b.HasIndex(c => new { c.ProjectId, c.NotebookId });

                b.HasIndex(c => new { c.ContentFileId, c.ChunkIndex })
                 .IsUnique()
                 .HasFilter("[ContentFileId] IS NOT NULL");

                b.HasIndex(c => new { c.NotebookFileId, c.ChunkIndex })
                 .IsUnique()
                 .HasFilter("[NotebookFileId] IS NOT NULL");
            });

            modelBuilder.Entity<NotebookFile>()
                .HasIndex(nf => new { nf.RelativePath, nf.NotebookId })
                .IsUnique();

            modelBuilder.Entity<NotebookFile>()
                .HasIndex(nf => nf.DocumentId)
                .IsUnique();

            // Map Project → HomePageContentFile (optional FK)
            modelBuilder.Entity<Project>()
                .HasOne(p => p.HomePageContentFile)
                .WithMany()
                .HasForeignKey(p => p.HomePageContentFileId)
                .OnDelete(DeleteBehavior.SetNull);

            // ConversationTurn unique index on (ConversationId, TurnIndex)
            modelBuilder.Entity<ConversationTurn>()
                .HasIndex(t => new { t.NotebookConversationId, t.TurnIndex })
                .IsUnique();

            // NotebookConversationMessage indexes for turn-based queries
            modelBuilder.Entity<NotebookConversationMessage>()
                .HasIndex(m => new { m.NotebookConversationId, m.TurnIndex, m.MessageSequence });

            // MessageAttachment unique index on (MessageId, NotebookFileId, OrderIndex)
            modelBuilder.Entity<MessageAttachment>()
                .HasIndex(ma => new { ma.MessageId, ma.NotebookFileId })
                .IsUnique();

            modelBuilder.Entity<MessageAttachment>()
                .HasIndex(ma => new { ma.MessageId, ma.OrderIndex })
                .IsUnique();

            // Additional indexes for performance optimization
            modelBuilder.Entity<NotebookConversationMessage>()
                .HasIndex(m => new { m.NotebookConversationId, m.Created });

            modelBuilder.Entity<NotebookConversationMessage>()
                .HasIndex(m => new { m.NotebookConversationId, m.Role, m.Created });

            modelBuilder.Entity<ConversationTurn>()
                .HasIndex(t => new { t.NotebookConversationId, t.TurnIndex })
                .IncludeProperties(t => t.AssistantName);

            // Configure default values for ConversationTurn columns
            modelBuilder.Entity<ConversationTurn>()
                .Property(t => t.Status)
                .HasDefaultValue("completed");

            modelBuilder.Entity<ConversationTurn>()
                .Property(t => t.LastUpdated)
                .HasDefaultValueSql("GETUTCDATE()");

            // Index for efficient polling by observers
            modelBuilder.Entity<ConversationTurn>()
                .HasIndex(t => new { t.NotebookConversationId, t.LastUpdated })
                .IncludeProperties(t => new { t.Status, t.TurnIndex });

            // Check constraint for valid status values
            modelBuilder.Entity<ConversationTurn>()
                .ToTable(t => t.HasCheckConstraint("CK_Turn_Status", "[Status] IN ('streaming', 'completed', 'cancelled')"));

            modelBuilder.Entity<MessageEditHistory>()
                .HasIndex(h => h.MessageId);

            // ------------------------------------------------------------
            // Configure FileLineageEvent indexes & constraints
            // ------------------------------------------------------------
            modelBuilder.Entity<FileLineageEvent>()
                .HasIndex(fle => new { fle.FileId, fle.VersionNumber });

            modelBuilder.Entity<FileLineageEvent>()
                .HasIndex(fle => new { fle.ProjectId, fle.Timestamp });

            // Ensure NotebookId presence aligns with FileKind
            // Notebook = 1, Project = 0 per enum
            modelBuilder.Entity<FileLineageEvent>()
                .ToTable(t => t.HasCheckConstraint("CK_FileLineageEvent_NotebookId",
                    "(FileKind = 1 AND NotebookId IS NOT NULL) OR (FileKind = 0 AND NotebookId IS NULL)"));

            // ------------------------------------------------------------
            // Configure relationships with restricted delete behavior
            // ------------------------------------------------------------

            modelBuilder.Entity<User>()
                .HasOne(u => u.ApprovedByUser)
                .WithMany()
                .HasForeignKey(u => u.ApprovedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<UserRole>()
                .HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserRole>()
                .HasOne(ur => ur.AssignedByUser)
                .WithMany()
                .HasForeignKey(ur => ur.AssignedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            
            // ------------------------------------------------------------
            // UsageEvents - covering indexes for guide usage queries
            // ------------------------------------------------------------
            modelBuilder.Entity<UsageEvent>(b =>
            {
                // Covering index recommended by Azure for queries filtering by Created and selecting ProjectId and cost/charge columns
                b.HasIndex(x => x.Created)
                    .IncludeProperties(x => new { x.ProjectId, x.CostUsd, x.MarkupPercent, x.ChargeUsd });

                // For published guide cost limit checks: filter by NotebookId + Created range and sum ChargeUsd.
                // UsageEvent.NotebookId is nullable, but limit checks use NotebookId != null.
                b.HasIndex(x => new { x.NotebookId, x.Created })
                    .IncludeProperties(x => new { x.ChargeUsd })
                    .HasDatabaseName("IX_UsageEvents_NotebookId_Created_ForCostLimits");

                // Covering index for guide usage report consolidated query:
                // Filters: AssistantId, ProjectId, AgentInvocationId, Created
                // Selects: Category, Operation, ConversationId, ValueInput, ValueCachedInput, ValueReasoning, ValueOutput, ChargeUsd
                b.HasIndex(x => new { x.AssistantId, x.ProjectId, x.AgentInvocationId, x.Created })
                    .IncludeProperties(x => new { 
                        x.Category, x.Operation, x.ConversationId, 
                        x.ValueInput, x.ValueCachedInput, x.ValueReasoning, x.ValueOutput, x.ChargeUsd 
                    })
                    .HasDatabaseName("IX_UsageEvents_GuideUsageReport");

                // Covering index for crew totals by invoking guide (no joins)
                b.HasIndex(x => new { x.InvokingAssistantId, x.ProjectId, x.Created })
                    .IncludeProperties(x => new {
                        x.Category, x.Operation, x.ConversationId,
                        x.ValueInput, x.ValueCachedInput, x.ValueReasoning, x.ValueOutput, x.ChargeUsd
                    })
                    .HasDatabaseName("IX_UsageEvents_GuideInvocationUsage");

                // Covering index for crew usage queries (by AgentInvocationId)
                b.HasIndex(x => x.AgentInvocationId)
                    .IncludeProperties(x => new {
                        x.Created, x.Category, x.ConversationId,
                        x.ValueInput, x.ValueCachedInput, x.ValueReasoning, x.ValueOutput, x.ChargeUsd
                    })
                    .HasDatabaseName("IX_UsageEvents_CrewUsage");

                // Covering index for project details "last activity per notebook" aggregation:
                // WHERE ProjectId = X AND NotebookId IS NOT NULL GROUP BY NotebookId SELECT MAX(Created)
                b.HasIndex(x => new { x.ProjectId, x.NotebookId })
                    .IncludeProperties(x => new { x.Created })
                    .HasDatabaseName("IX_UsageEvents_ProjectId_NotebookId_ForLastActivity");

                // NotebookConversationMessageId is intentionally NOT a FK constraint.
                // Usage events are immutable historical records that must never be modified
                // or blocked by conversation/message deletion (e.g., retention cleanup).
                // The column is a plain nullable Guid — matching ConversationId and AgentInvocationId.
            });

            // ------------------------------------------------------------
            // Model and Tool catalogs
            // ------------------------------------------------------------
            modelBuilder.Entity<ApplicationSetting>(b =>
            {
                b.HasKey(x => x.SectionName);
                b.Property(x => x.SectionName).HasMaxLength(128).IsRequired();
                b.Property(x => x.JsonValue).HasColumnType("nvarchar(max)").IsRequired();
                b.Property(x => x.SchemaVersion).HasDefaultValue(1);
                b.Property(x => x.CreatedUtc).HasDefaultValueSql("GETUTCDATE()");
                b.Property(x => x.UpdatedUtc).HasDefaultValueSql("GETUTCDATE()");
                b.Property(x => x.RowVersion).IsRowVersion();
            });

            modelBuilder.Entity<RuntimeProfile>(b =>
            {
                b.Property(x => x.SamplingParametersJson).HasColumnType("nvarchar(max)").IsRequired();
                b.Property(x => x.ThinkingControlJson).HasColumnType("nvarchar(max)").IsRequired();
            });

            modelBuilder.Entity<Model>(b =>
            {
                // Note: ModelId is already the primary key, no separate unique index needed
                b.HasIndex(x => new { x.IsActive, x.DisplayOrder });
                b.Property(x => x.ReasoningChoicesJson).HasColumnType("nvarchar(max)");
            });

            modelBuilder.Entity<Tool>(b =>
            {
                b.HasIndex(x => x.ToolType).IsUnique();
                b.HasIndex(x => new { x.Category, x.IsActive, x.DisplayOrder });
            });

            modelBuilder.Entity<ExcludedHost>(b =>
            {
                b.HasIndex(x => x.Host).IsUnique();
                b.HasIndex(x => x.Created);
                b.Property(x => x.Host).HasMaxLength(255).IsRequired();
                b.Property(x => x.Reason).HasMaxLength(1024);
            });

            // Seed tools
            modelBuilder.Entity<Tool>().HasData(
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000d"),
                    ToolType = "WebSearch",
                    DisplayName = "Web Search",
                    Description = "Search the web using searXNG.",
                    Category = "Search",
                    IsActive = true,
                    DisplayOrder = 0,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000001"),
                    ToolType = "ReadWeb",
                    DisplayName = "Read Web",
                    Description = "Reads a web page and returns markdown of the content.",
                    Category = "Search",
                    IsActive = true,
                    DisplayOrder = 1,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000e"),
                    ToolType = "GetContentFromUrl",
                    DisplayName = "Get Content From URL",
                    Description = "Fetch a web page and convert it to markdown.",
                    Category = "Search",
                    IsActive = true,
                    DisplayOrder = 4,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000002"),
                    ToolType = "generate_image",
                    DisplayName = "Generate Image",
                    Description = "Generate an image using AI.",
                    Category = "Media",
                    IsActive = true,
                    DisplayOrder = 1,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000003"),
                    ToolType = "generate_podcast",
                    DisplayName = "Generate Podcast",
                    Description = "Generate a WAV podcast from SSML.",
                    Category = "Media",
                    IsActive = true,
                    DisplayOrder = 2,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000005"),
                    ToolType = "MakeImageFromImage",
                    DisplayName = "Edit Image",
                    Description = "Edit or modify an existing notebook image using AI.",
                    Category = "Media",
                    IsActive = true,
                    DisplayOrder = 3,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000007"),
                    ToolType = "make_diagram",
                    DisplayName = "Make Diagram",
                    Description = "Execute PlantUML to generate diagrams.",
                    Category = "Code",
                    IsActive = true,
                    DisplayOrder = 1,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000008"),
                    ToolType = "run_bash",
                    DisplayName = "Run Bash",
                    Description = "Execute bash in a container.",
                    Category = "Code",
                    IsActive = true,
                    DisplayOrder = 2,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000009"),
                    ToolType = "run_python",
                    DisplayName = "Run Python",
                    Description = "Execute Python in a container.",
                    Category = "Code",
                    IsActive = true,
                    DisplayOrder = 3,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000a"),
                    ToolType = "search_notebook",
                    DisplayName = "Search Notebook",
                    Description = "Search inside the current notebook.",
                    Category = "Search",
                    IsActive = true,
                    DisplayOrder = 2,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000b"),
                    ToolType = "search_project",
                    DisplayName = "Search Project",
                    Description = "Search inside the whole project.",
                    Category = "Search",
                    IsActive = true,
                    DisplayOrder = 3,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000c"),
                    ToolType = "set_user_context_options",
                    DisplayName = "Set Context Options",
                    Description = "Set user context options.",
                    Category = "Integration",
                    IsActive = true,
                    DisplayOrder = 1,
                    Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            );
            
            // ------------------------------------------------------------
            // Assistant/Guide entities
            // ------------------------------------------------------------
            modelBuilder.Entity<Assistant>(b =>
            {
                b.HasIndex(x => x.Name)
                 .IsUnique();
                 
                b.Property(x => x.ReasoningEffort).HasMaxLength(64).HasComment("Model-specific reasoning effort value.");
                b.Property(x => x.ToolResourcesJson).HasColumnType("nvarchar(max)");
                b.Property(x => x.MetadataJson).HasColumnType("nvarchar(max)");
                b.Property(x => x.Kind).HasConversion<byte>().HasComment("AssistantKind discriminator");
                b.Property(x => x.AvatarImageBytes).HasColumnType("varbinary(max)");
                b.Property(x => x.HomePageMarkdown).HasColumnType("nvarchar(max)");
                
                b.HasIndex(x => new { x.IsGlobal, x.Kind, x.Name });

                b.HasOne(x => x.Model)
                 .WithMany(m => m.Assistants)
                 .HasForeignKey(x => x.ModelId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<AssistantTool>(b =>
            {
                b.HasKey(x => new { x.AssistantId, x.ToolId });

                b.HasOne(x => x.Assistant)
                 .WithMany(a => a.Tools)
                 .HasForeignKey(x => x.AssistantId)
                 .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.Tool)
                 .WithMany(t => t.AssistantTools)
                 .HasForeignKey(x => x.ToolId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<AssistantOpenApiSchema>(b =>
            {
                b.HasIndex(x => new { x.AssistantId, x.Name }).IsUnique();
                b.HasIndex(x => x.ApiHost);
                b.Property(x => x.SpecificationJson).HasColumnType("nvarchar(max)");

                b.HasOne(x => x.Assistant)
                 .WithMany(a => a.OpenApiSchemas)
                 .HasForeignKey(x => x.AssistantId)
                 .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.AuthProvider)
                 .WithMany()
                 .HasForeignKey(x => x.AuthProviderId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<AssistantOpenApiOperation>(b =>
            {
                b.HasIndex(x => new { x.SchemaId, x.OperationId }).IsUnique();
                b.Property(x => x.ToolDefinitionJson).HasColumnType("nvarchar(max)");
                b.Property(x => x.SchemaFragmentJson).HasColumnType("nvarchar(max)");

                b.HasOne(x => x.Schema)
                 .WithMany(s => s.Operations)
                 .HasForeignKey(x => x.SchemaId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<AssistantFile>(b =>
            {
                b.HasIndex(x => new { x.AssistantId, x.RelativePath }).IsUnique();
                b.Property(x => x.ContentBytes).HasColumnType("varbinary(max)");

                b.HasOne(x => x.Assistant)
                 .WithMany(a => a.Files)
                 .HasForeignKey(x => x.AssistantId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<AssistantContextOption>(b =>
            {
                b.HasKey(x => new { x.AssistantId, x.Key });
                b.Property(x => x.Key).IsRequired().HasMaxLength(128);
                b.Property(x => x.Value).HasMaxLength(1024);

                b.HasOne(x => x.Assistant)
                 .WithMany(a => a.ContextOptions)
                 .HasForeignKey(x => x.AssistantId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<AssistantAuthProvider>(b =>
            {
                b.HasIndex(x => x.ProviderId);
                b.Property(x => x.AuthType).HasMaxLength(32);
                b.Property(x => x.ValueTemplate).HasMaxLength(512);
                b.Property(x => x.HeaderName).HasMaxLength(100);
                b.Property(x => x.ClientId).HasMaxLength(200);
                b.Property(x => x.Tenant).HasMaxLength(100);
                b.Property(x => x.UserConfigPolicy).HasMaxLength(50);
            });

            modelBuilder.Entity<AssistantAuthScope>(b =>
            {
                b.HasKey(x => new { x.AssistantAuthProviderId, x.Scope });

                b.HasOne(x => x.AssistantAuthProvider)
                 .WithMany(p => p.Scopes)
                 .HasForeignKey(x => x.AssistantAuthProviderId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ProjectAssistantEnvironment>(b =>
            {
                b.HasKey(x => new { x.ProjectId, x.AssistantId });
                b.HasIndex(x => x.AssistantId);
                b.Property(x => x.EnvironmentConfigJson).HasColumnType("nvarchar(max)");

                b.HasOne(x => x.Project)
                 .WithMany(p => p.AssistantEnvironments)
                 .HasForeignKey(x => x.ProjectId)
                 .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.Assistant)
                 .WithMany()
                 .HasForeignKey(x => x.AssistantId)
                 .OnDelete(DeleteBehavior.Cascade);
            });


            modelBuilder.Entity<AssistantConversationStarter>(b =>
            {
                b.HasIndex(x => new { x.AssistantId, x.OrderIndex }).IsUnique();

                b.HasOne(x => x.Assistant)
                 .WithMany(a => a.ConversationStarters)
                 .HasForeignKey(x => x.AssistantId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ------------------------------------------------------------
            // GuideMember (many-to-many: Guides ↔ Assistants for crew)
            // ------------------------------------------------------------
            modelBuilder.Entity<GuideMember>(b =>
            {
                b.HasKey(x => new { x.GuideId, x.AssistantId });
                
                b.HasIndex(x => x.AssistantId);
                b.HasIndex(x => new { x.GuideId, x.DisplayOrder });

                b.HasOne(x => x.Guide)
                 .WithMany(a => a.CrewMembers)
                 .HasForeignKey(x => x.GuideId)
                 .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.Assistant)
                 .WithMany(a => a.GuideMemberships)
                 .HasForeignKey(x => x.AssistantId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ------------------------------------------------------------
            // PublishedGuide configuration
            // ------------------------------------------------------------
            modelBuilder.Entity<PublishedGuide>(b =>
            {
                // Explicit auth mode (int column, not null, default Anonymous = 0)
                b.Property(x => x.AuthMode)
                 .HasConversion<int>()
                 .IsRequired()
                 .HasDefaultValue(PublishedGuideAuthMode.Anonymous);

                // Index for looking up all published guides for a specific guide
                b.HasIndex(x => x.GuideId);
                
                // Unique index on NotebookId - each notebook can only be associated with one published guide
                b.HasIndex(x => x.NotebookId).IsUnique();
                
                // Index for filtering active published guides
                b.HasIndex(x => new { x.Active, x.Created });
                
                // Index for querying published guides by guide and active status
                b.HasIndex(x => new { x.GuideId, x.Active });
                
                // Index for filtering published guides by auth requirement (webhook URL presence)
                b.HasIndex(x => x.AuthValidationWebhookUrl)
                 .HasFilter("[AuthValidationWebhookUrl] IS NOT NULL");

                // Unique index on FriendlyName (case-insensitive uniqueness, filtered to exclude NULLs)
                b.HasIndex(x => x.FriendlyName)
                 .IsUnique()
                 .HasFilter("[FriendlyName] IS NOT NULL");

                b.HasOne(x => x.Guide)
                 .WithMany()
                 .HasForeignKey(x => x.GuideId)
                 .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(x => x.Notebook)
                 .WithMany()
                 .HasForeignKey(x => x.NotebookId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // Project
            modelBuilder.Entity<Notebook>()
                .HasOne(n => n.Project)
                .WithMany(p => p.Notebooks)
                .HasForeignKey(n => n.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ContentFile>()
                .HasOne(cf => cf.Project)
                .WithMany(p => p.ContentFiles)
                .HasForeignKey(cf => cf.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            




            // GuideId references Assistants table (guides) - nullable for legacy data
            modelBuilder.Entity<Notebook>()
                .HasOne(n => n.Guide)
                .WithMany()
                .HasForeignKey(n => n.GuideId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            modelBuilder.Entity<ProjectFolder>()
                .HasOne(pf => pf.Project)
                .WithMany(p => p.Folders)
                .HasForeignKey(pf => pf.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ProjectFolder>()
                .HasOne(pf => pf.ParentFolder)
                .WithMany(pf => pf.SubFolders)
                .HasForeignKey(pf => pf.ParentFolderId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ContentFile>()
                .HasOne(cf => cf.Folder)
                .WithMany(pf => pf.ContentFiles)
                .HasForeignKey(cf => cf.FolderId)
                .OnDelete(DeleteBehavior.SetNull);
            
            modelBuilder.Entity<ContentFileVersion>()
                .HasOne(cfv => cfv.OriginVersion)
                .WithMany()
                .HasForeignKey(cfv => cfv.OriginVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Link>()
                .HasOne(l => l.Project)
                .WithMany(p => p.Links)
                .HasForeignKey(l => l.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ProjectExternalAuth>()
                .HasIndex(e => new { e.ProjectId, e.ProviderId })
                .IsUnique();
            modelBuilder.Entity<ProjectExternalAuth>()
                .Property(e => e.AuthType)
                .HasMaxLength(32);

            modelBuilder.Entity<ExternalOAuthToken>()
                .HasIndex(e => new { e.UserId, e.ProviderId })
                .IsUnique()
                .HasDatabaseName("IX_ExternalOAuthTokens_UserId_ProviderId_Unique");

            modelBuilder.Entity<ExternalOAuthToken>()
                .HasIndex(e => e.ExpiresAt);

            modelBuilder.Entity<ExternalOAuthToken>()
                .Property(e => e.Updated)
                .HasDefaultValueSql("GETUTCDATE()");

            modelBuilder.Entity<ExternalOAuthToken>()
                .HasOne(e => e.User)
                .WithMany(u => u.ExternalOAuthTokens)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ExternalOAuthToken>()
                .HasOne(e => e.Project)
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<OAuthAuthorizationState>()
                .HasIndex(e => new { e.UserId, e.ProviderId })
                .HasDatabaseName("IX_OAuthAuthorizationStates_UserId_ProviderId");

            modelBuilder.Entity<OAuthAuthorizationState>()
                .HasIndex(e => e.ExpiresAt);

            modelBuilder.Entity<OAuthAuthorizationState>()
                .HasOne(e => e.User)
                .WithMany(u => u.OAuthAuthorizationStates)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SemiStructuredProjectData>()
                .HasOne(sd => sd.Project)
                .WithMany(p => p.SemiStructuredDatas)
                .HasForeignKey(sd => sd.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            // ------------------------------------------------------------
            // Configure NotebookConversation one-to-many with Notebook
            // ------------------------------------------------------------
            modelBuilder.Entity<Notebook>()
                .HasMany(n => n.Conversations)
                .WithOne(c => c.Notebook)
                .HasForeignKey(c => c.NotebookId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<NotebookConversationMessage>()
                .HasOne(m => m.NotebookConversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.NotebookConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<NotebookConversationMessage>()
                .HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<NotebookConversationMessage>()
                .HasOne(m => m.LastEditedByUser)
                .WithMany()
                .HasForeignKey(m => m.LastEditedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // ------------------------------------------------------------
            // Configure ConversationTurn relationships
            // ------------------------------------------------------------
            modelBuilder.Entity<ConversationTurn>()
                .HasOne(t => t.NotebookConversation)
                .WithMany(c => c.Turns)
                .HasForeignKey(t => t.NotebookConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<NotebookConversationMessage>()
                .HasOne(m => m.ConversationTurn)
                .WithMany(t => t.Messages)
                .HasForeignKey(m => new { m.NotebookConversationId, m.TurnIndex })
                .HasPrincipalKey(t => new { t.NotebookConversationId, t.TurnIndex })
                .OnDelete(DeleteBehavior.Restrict);

            // ------------------------------------------------------------
            // Configure ConversationLock relationships
            // ------------------------------------------------------------
            modelBuilder.Entity<ConversationLock>()
                .HasOne(l => l.NotebookConversation)
                .WithMany()
                .HasForeignKey(l => l.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Check constraint for lock expiration
            modelBuilder.Entity<ConversationLock>()
                .ToTable(t => t.HasCheckConstraint("CK_ExpiresInFuture", "[ExpiresAt] > [LockedAt]"));

            // ------------------------------------------------------------
            // Configure MessageAttachment relationships
            // ------------------------------------------------------------
            modelBuilder.Entity<MessageAttachment>()
                .HasOne(ma => ma.Message)
                .WithMany(m => m.Attachments)
                .HasForeignKey(ma => ma.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MessageAttachment>()
                .HasOne(ma => ma.NotebookFile)
                .WithMany()
                .HasForeignKey(ma => ma.NotebookFileId)
                .OnDelete(DeleteBehavior.Restrict);

            // ------------------------------------------------------------
            // Configure MessageEditHistory one-to-one and user FKs
            // ------------------------------------------------------------
            modelBuilder.Entity<MessageEditHistory>()
                .HasOne(h => h.Message)
                .WithOne(m => m.EditHistory)
                .HasForeignKey<MessageEditHistory>(h => h.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MessageEditHistory>()
                .HasOne(h => h.FirstEditedByUser)
                .WithMany()
                .HasForeignKey(h => h.FirstEditedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Self-reference for source notebook traceability (no cascade delete)
            modelBuilder.Entity<Notebook>()
                .HasOne(n => n.SourceNotebook)
                .WithMany()
                .HasForeignKey(n => n.SourceNotebookId)
                .OnDelete(DeleteBehavior.Restrict);

            // Reference to source conversation message (restrict delete)
            modelBuilder.Entity<Notebook>()
                .HasOne(n => n.SourceConversationMessage)
                .WithMany()
                .HasForeignKey(n => n.SourceConversationMessageId)
                .OnDelete(DeleteBehavior.Restrict);

            // Home page file reference (nullable FK, no special delete behavior needed)
            modelBuilder.Entity<Notebook>()
                .HasOne(n => n.HomePageFile)
                .WithMany()
                .HasForeignKey(n => n.HomePageFileId);

            // Home page conversation reference (nullable FK, no special delete behavior needed)
            modelBuilder.Entity<Notebook>()
                .HasOne(n => n.HomePageConversation)
                .WithMany()
                .HasForeignKey(n => n.HomePageConversationId);

            modelBuilder.Entity<NotebookFile>()
                .HasOne(nf => nf.Notebook)
                .WithMany(n => n.NotebookFiles)
                .HasForeignKey(nf => nf.NotebookId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ContentFileVersion>()
                .HasOne(cfv => cfv.OriginNotebookFile)
                .WithMany()
                .HasForeignKey(cfv => cfv.OriginNotebookFileId)
                .OnDelete(DeleteBehavior.Restrict);

            // Configure relationship for OriginalFolderId (lineage tracking)
            modelBuilder.Entity<ContentFileVersion>()
                .HasOne(cfv => cfv.OriginalFolder)
                .WithMany()
                .HasForeignKey(cfv => cfv.OriginalFolderId)
                .OnDelete(DeleteBehavior.Restrict);

            // ------------------------------------------------------------
            // Configure UserProjectContextOption
            // ------------------------------------------------------------
            modelBuilder.Entity<UserProjectContextOption>(cfg =>
            {
                cfg.ToTable("UserProjectContextOption");
                cfg.HasKey(x => new { x.UserId, x.ProjectId, x.Key });

                // Lookup optimisation for "all users options in a project"
                cfg.HasIndex(x => new { x.ProjectId, x.Key });

                cfg.HasOne(x => x.User)
                   .WithMany()
                   .HasForeignKey(x => x.UserId)
                   .OnDelete(DeleteBehavior.Cascade);

                cfg.HasOne(x => x.Project)
                   .WithMany()
                   .HasForeignKey(x => x.ProjectId)
                   .OnDelete(DeleteBehavior.Cascade);
            });

            // ------------------------------------------------------------
            // Configure AgentInvocation relationships
            // ------------------------------------------------------------
            modelBuilder.Entity<AgentInvocation>()
                .HasOne(ai => ai.ParentConversation)
                .WithMany()
                .HasForeignKey(ai => ai.ParentConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentInvocation>()
                .HasOne(ai => ai.ParentInvocation)
                .WithMany(ai => ai.ChildInvocations)
                .HasForeignKey(ai => ai.ParentInvocationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentInvocation>()
                .HasIndex(ai => ai.AssistantId);

            modelBuilder.Entity<AgentInvocation>()
                .HasOne(ai => ai.Assistant)
                .WithMany(a => a.AgentInvocations)
                .HasForeignKey(ai => ai.AssistantId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentInvocationMessage>()
                .HasOne(m => m.AgentInvocation)
                .WithMany(ai => ai.Messages)
                .HasForeignKey(m => m.AgentInvocationId)
                .OnDelete(DeleteBehavior.Cascade);

            // ------------------------------------------------------------
            // Configure ConversationCurrentState view
            // ------------------------------------------------------------
            modelBuilder.Entity<ConversationCurrentState>()
                .ToView("ConversationCurrentState")
                .HasNoKey();

            // ------------------------------------------------------------
            // Host folder mounts (metadata only — never cascade host content)
            // ------------------------------------------------------------
            modelBuilder.Entity<HostFolderMount>(b =>
            {
                b.Property(m => m.SourceSpec).HasColumnType("nvarchar(max)").IsRequired();
                b.Property(m => m.Scope).HasConversion<int>();
                b.Property(m => m.SourceKind).HasConversion<int>();
                b.Property(m => m.Status).HasConversion<int>();
                b.Property(m => m.CreatedUtc).HasDefaultValueSql("GETUTCDATE()");
                b.Property(m => m.UpdatedUtc).HasDefaultValueSql("GETUTCDATE()");

                b.HasOne(m => m.Project)
                    .WithMany()
                    .HasForeignKey(m => m.ProjectId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(m => m.Notebook)
                    .WithMany()
                    .HasForeignKey(m => m.NotebookId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(m => m.CreatedByUser)
                    .WithMany()
                    .HasForeignKey(m => m.CreatedByUserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<HostFolderMountLink>(b =>
            {
                b.Property(l => l.Status).HasConversion<int>();

                b.HasOne(l => l.HostFolderMount)
                    .WithMany(m => m.Links)
                    .HasForeignKey(l => l.HostFolderMountId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(l => l.Notebook)
                    .WithMany()
                    .HasForeignKey(l => l.NotebookId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Plan §9: at most one active mapping per leaf per notebook.
                b.HasIndex(l => new { l.NotebookId, l.LinkRelativePath })
                    .IsUnique()
                    .HasFilter("[Status] IN (0, 1, 2)")
                    .HasDatabaseName("IX_HostFolderMountLinks_NotebookId_LinkRelativePath_ActiveUnique");
            });
        }

        /// <summary>
        /// Validates message timestamp ordering before saving changes.
        /// Prevents creating messages with newer timestamps when higher sequence numbers already exist in the same turn.
        /// </summary>
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ValidateMessageTimestampOrdering();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        /// <summary>
        /// Validates message timestamp ordering before saving changes.
        /// Prevents creating messages with newer timestamps when higher sequence numbers already exist in the same turn.
        /// </summary>
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            ValidateMessageTimestampOrdering();
            return await base.SaveChangesAsync(cancellationToken);
        }

        private void ValidateMessageTimestampOrdering()
        {
            var newMessages = ChangeTracker.Entries<NotebookConversationMessage>()
                .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                .Select(e => e.Entity)
                .ToList();

            if (newMessages.Count == 0)
                return;

            // Group new messages by conversation and turn for efficient checking
            var newMessagesByTurn = newMessages
                .GroupBy(m => new { m.NotebookConversationId, m.TurnIndex })
                .ToList();

            foreach (var turnGroup in newMessagesByTurn)
            {
                var conversationId = turnGroup.Key.NotebookConversationId;
                var turnIndex = turnGroup.Key.TurnIndex;
                var newMessagesInTurn = turnGroup.ToList();

                // Get existing messages in the database for this turn
                var existingMessages = NotebookConversationMessages
                    .AsNoTracking()
                    .Where(m => m.NotebookConversationId == conversationId
                             && m.TurnIndex == turnIndex)
                    .ToList();

                // Check each new message
                foreach (var newMsg in newMessagesInTurn)
                {
                    // Check against existing messages with higher sequence numbers
                    var existingHigherSequence = existingMessages
                        .Where(m => m.MessageSequence > newMsg.MessageSequence)
                        .ToList();

                    // Also check against other new messages in the same batch with higher sequence numbers
                    var newHigherSequence = newMessagesInTurn
                        .Where(m => m != newMsg && m.MessageSequence > newMsg.MessageSequence)
                        .ToList();

                    if (existingHigherSequence.Count > 0 || newHigherSequence.Count > 0)
                    {
                        // Find the minimum Created timestamp of all messages with higher sequence numbers
                        var allHigherSequence = existingHigherSequence.Concat(newHigherSequence).ToList();
                        var minHigherSequenceTimestamp = allHigherSequence.Min(m => m.Created);

                        // If the new message has a newer timestamp than the minimum of higher-sequence messages, it's invalid
                        if (newMsg.Created > minHigherSequenceTimestamp)
                        {
                            throw new InvalidOperationException(
                                $"Cannot create message with TurnIndex={turnIndex}, MessageSequence={newMsg.MessageSequence}, " +
                                $"Created={newMsg.Created:O} because there are existing messages with higher sequence numbers " +
                                $"(min Created={minHigherSequenceTimestamp:O}) in the same turn. " +
                                $"Message timestamps must respect sequence order within a turn.");
                        }
                    }
                }
            }
        }
    }
} 
