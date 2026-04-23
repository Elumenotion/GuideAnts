using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Represents an Assistant or Guide in the system.
    /// 
    /// - A Guide IS an Assistant (Kind=Guide) that has a crew of other assistants
    /// - At runtime, assistants are created from guides
    /// - Can be global (available to all) or owned by a specific user
    /// 
    /// IsGlobal = true means available to all users
    /// </summary>
    public class Assistant
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// When true, this assistant/guide is available to all owners in the system.
        /// When false, only the owning user can use it.
        /// </summary>
        [Required]
        public bool IsGlobal { get; set; } = false;

        /// <summary>
        /// Whether this assistant/guide is currently active and available for use.
        /// </summary>
        [Required]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Optional display order for UI presentation.
        /// </summary>
        public int? DisplayOrder { get; set; }

        /// <summary>
        /// Logical assistant name (e.g., "Search", "Office 365 Guide").
        /// Unique per owner.
        /// </summary>
        [Required]
        [StringLength(255)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional description mirroring manifest.description.
        /// </summary>
        [StringLength(1024)]
        public string? Description { get; set; }

        /// <summary>
        /// Instructions from manifest or instructions.md.
        /// </summary>
        public string? Instructions { get; set; }

        /// <summary>
        /// Selected model for this assistant/guide.
        /// References the global model catalog.
        /// </summary>
        [StringLength(128)]
        public string? ModelId { get; set; }
        public Model? Model { get; set; }

        /// <summary>
        /// Temperature, TopP, and reasoning effort mirror assistant definition.
        /// Temperature and TopP are legacy typed columns; SamplingParametersJson is the new
        /// data-driven storage for all sampling parameters.
        /// </summary>
        public float? Temperature { get; set; }
        public double? TopP { get; set; }
        [StringLength(64)]
        public string? ReasoningEffort { get; set; }

        /// <summary>
        /// JSON dictionary of sampling parameter overrides, e.g., {"temperature": 0.6, "top_k": 30}.
        /// Parameters not in this dictionary use the profile default.
        /// </summary>
        public string? SamplingParametersJson { get; set; }

        /// <summary>
        /// Tools assigned to this assistant from the global tool catalog.
        /// </summary>
        public ICollection<AssistantTool> Tools { get; set; } = [];

        /// <summary>
        /// Raw JSON for ToolResources from manifest (e.g., file_search vectorStoreIds).
        /// </summary>
        public string? ToolResourcesJson { get; set; }

        /// <summary>
        /// Optional arbitrary metadata key/values from manifest.
        /// For guides, may include template family labels if needed for analytics/UI grouping.
        /// </summary>
        public string? MetadataJson { get; set; }

        /// <summary>
        /// Optional auth configuration JSON from guide manifest.
        /// For Kind=Guide, stores the auth section from manifest.json.
        /// </summary>
        public string? AuthConfigJson { get; set; }

        /// <summary>
        /// Optional default assistant name for guide workspace context.
        /// When Kind=Guide, specifies which crew member is the primary entry point.
        /// Null for regular assistants.
        /// </summary>
        [StringLength(255)]
        public string? DefaultAssistant { get; set; }

        /// <summary>
        /// Optional evaluator assistant to use when this assistant is invoked through Agent.Invoke.
        /// </summary>
        [StringLength(255)]
        public string? InvocationEvaluator { get; set; }

        /// <summary>
        /// Explicit discriminator of record behavior: Assistant or Guide.
        /// Guides enable crew and template-aware orchestration paths.
        /// </summary>
        [Required]
        public AssistantKind Kind { get; set; } = AssistantKind.Assistant;

        /// <summary>
        /// Optional avatar image bytes (png/jpg/svg) from HostExtensions/UI/avatar.*.
        /// Null if no avatar is provided.
        /// </summary>
        public byte[]? AvatarImageBytes { get; set; }

        /// <summary>
        /// Content type of the avatar image (e.g., image/png, image/jpeg, image/svg+xml).
        /// </summary>
        [StringLength(50)]
        public string? AvatarContentType { get; set; }

        /// <summary>
        /// Optional home page markdown content from HostExtensions/UI/home.md.
        /// For guides, this serves as the landing page when notebooks are created.
        /// </summary>
        public string? HomePageMarkdown { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Updated { get; set; }

        /// <summary>
        /// OpenAPI 3.0 schemas defining external API operations.
        /// Each schema links to auth providers via ApiHost.
        /// </summary>
        public ICollection<AssistantOpenApiSchema> OpenApiSchemas { get; set; } = [];

        /// <summary>
        /// One-to-many related binary files (code interpreter files, vector store files, etc.).
        /// NOTE: OpenAPI schemas are stored in OpenApiSchemas, not here.
        /// </summary>
        public ICollection<AssistantFile> Files { get; set; } = [];

        /// <summary>
        /// Context options key/value pairs from HostExtensions/UI/contextOptions.json.
        /// Stored as flattened records for queryability.
        /// </summary>
        public ICollection<AssistantContextOption> ContextOptions { get; set; } = [];

        /// <summary>
        /// For Kind=Guide: the assistants in this guide's crew (many-to-many via GuideMember).
        /// For Kind=Assistant: the guides this assistant is a member of (many-to-many via GuideMember).
        /// </summary>
        public ICollection<GuideMember> CrewMembers { get; set; } = [];
        public ICollection<GuideMember> GuideMemberships { get; set; } = [];

        /// <summary>
        /// Optional conversation starter prompts from HostExtensions/UI/conversationStarters.json.
        /// </summary>
        public ICollection<AssistantConversationStarter> ConversationStarters { get; set; } = [];

        /// <summary>
        /// Agent invocation executions for this assistant/guide.
        /// </summary>
        public ICollection<AgentInvocation> AgentInvocations { get; set; } = [];
    }
}


