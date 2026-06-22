using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Represents a published guide configuration that can be used to create constrained notebook instances.
    /// Links a Guide (Assistant with Kind=Guide) to notebooks with specific usage constraints.
    /// </summary>
    public class PublishedGuide
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Reference to the Guide (Assistant with Kind=Guide) being published.
        /// </summary>
        [Required]
        public Guid GuideId { get; set; }
        public Assistant Guide { get; set; } = null!;

        /// <summary>
        /// Reference to the Notebook created from this published guide.
        /// </summary>
        [Required]
        public Guid NotebookId { get; set; }
        public Notebook Notebook { get; set; } = null!;

        /// <summary>
        /// Timestamp when this guide was published.
        /// </summary>
        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether this published guide is currently active and available for use.
        /// </summary>
        [Required]
        public bool Active { get; set; } = true;

        /// <summary>
        /// Explicit authentication mode for this published guide. Exactly one mode
        /// applies (mutually exclusive). Defaults to <see cref="PublishedGuideAuthMode.Anonymous"/>.
        /// Legacy columns (<see cref="ApiKeyHash"/>, <see cref="AuthValidationWebhookUrl"/>)
        /// still back the <see cref="PublishedGuideAuthMode.ApiKey"/> /
        /// <see cref="PublishedGuideAuthMode.Webhook"/> modes.
        /// </summary>
        [Required]
        public PublishedGuideAuthMode AuthMode { get; set; } = PublishedGuideAuthMode.Anonymous;

        /// <summary>
        /// Optional retention period in days for conversations created from this published guide.
        /// If specified, conversations older than this period may be automatically archived or deleted.
        /// </summary>
        public int? RetentionPeriod { get; set; }

        /// <summary>
        /// Optional maximum length for user messages in conversations using this published guide.
        /// If specified, user input will be limited to this character count.
        /// </summary>
        public int? MaxUserMessageLength { get; set; }

        /// <summary>
        /// Optional maximum number of conversation turns allowed when using this published guide.
        /// If specified, conversations will be limited to this many back-and-forth exchanges.
        /// </summary>
        public int? MaxTurns { get; set; }

        /// <summary>
        /// Optional webhook URL for custom authentication validation.
        /// When set, auth is REQUIRED. The minimal API will POST the bearer token to this URL.
        /// If null, the published guide allows anonymous access.
        /// Expected success response: 200 with { "valid": true, "userIdentity": "username" }
        /// Expected failure response: 401/403 with { "valid": false, "error": "message" }
        /// </summary>
        [StringLength(2048)]
        public string? AuthValidationWebhookUrl { get; set; }

        /// <summary>
        /// Timeout in seconds for authentication webhook calls.
        /// Defaults to 5 seconds if not specified.
        /// </summary>
        public int? AuthWebhookTimeoutSeconds { get; set; }

        /// <summary>
        /// Friendly name for public URL access (e.g., "my-awesome-guide").
        /// Globally unique across all published guides.
        /// Used in URL path: /public/{friendlyName}
        /// </summary>
        [StringLength(100)]
        public string? FriendlyName { get; set; }

        /// <summary>
        /// Display mode for the chat interface.
        /// "full" (default) or "last-turn".
        /// </summary>
        [StringLength(20)]
        public string? DisplayMode { get; set; } = "full";

        /// <summary>
        /// Whether to enable command mode input styling and behavior.
        /// </summary>
        public bool CommandMode { get; set; }

        /// <summary>
        /// Whether to show turn navigation controls (Previous/Next turn).
        /// </summary>
        public bool ShowTurnNavigation { get; set; }

        /// <summary>
        /// Whether the chat interface uses collapsible/floating behavior.
        /// </summary>
        public bool Collapsible { get; set; }

        /// <summary>
        /// Whether to show conversation starters if available.
        /// </summary>
        public bool ShowConversationStarters { get; set; }

        /// <summary>
        /// Whether to allow file attachments.
        /// </summary>
        public bool ShowAttachments { get; set; }

        /// <summary>
        /// Serialized wire API configuration for OpenAI-compatible published endpoints.
        /// </summary>
        public string? WireApiConfigJson { get; set; }

        /// <summary>
        /// SHA-256 hash of the API key for authentication.
        /// When set, clients must provide the API key via x-guideants-apikey header.
        /// This is an alternative to the AuthValidationWebhookUrl - only one should be used.
        /// The actual API key is only shown once at creation/regeneration time.
        /// </summary>
        [StringLength(64)]
        public string? ApiKeyHash { get; set; }

        /// <summary>
        /// Whether MCP (Model Context Protocol) access is enabled for this published guide.
        /// Requires an API key (ApiKeyHash must be set). When enabled, clients can connect
        /// via the /api/published/mcp?pubId={id} endpoint using a stateless HTTP transport.
        /// </summary>
        public bool McpEnabled { get; set; }

        /// <summary>
        /// Client-facing description exposed via MCP discovery (guide_info tool).
        /// Written for external AI agents and MCP clients to understand what this guide does,
        /// what it's good at, and how to interact with it effectively.
        /// Not the system prompt — this is specifically crafted for client consumption.
        /// </summary>
        [StringLength(2000)]
        public string? McpDescription { get; set; }

        /// <summary>
        /// Optional maximum customer charge (USD) allowed per UTC day for this published guide's notebook.
        /// When exceeded, the guide is automatically blocked for public access until the next UTC day.
        /// </summary>
        [Column(TypeName = "decimal(19,4)")]
        public decimal? DailyChargeLimitUsd { get; set; }

        /// <summary>
        /// Optional maximum customer charge (USD) allowed per UTC calendar month for this published guide's notebook.
        /// When exceeded, the guide is automatically blocked for public access until the next UTC month.
        /// </summary>
        [Column(TypeName = "decimal(19,4)")]
        public decimal? BillingPeriodChargeLimitUsd { get; set; }
    }
}
