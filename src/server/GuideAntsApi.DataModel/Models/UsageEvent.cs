using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models
{
    public enum UsageCategory
    {
        ChatCompletion = 0,
        ImageGeneration = 1,
        DocumentExtraction = 2,
        SpeechTranscription = 3,
        SpeechSynthesis = 4,
        Search = 5,
        StorageUploaded = 6,
        StorageSystemGenerated = 7,
        ToolCall = 8,
        Embeddings = 9
    }

    [Index(nameof(UserId), nameof(ProjectId), nameof(NotebookId), nameof(ConversationId))]
    [Index(nameof(Category), nameof(Created))]
    [Index(nameof(ProjectId), nameof(Created))]
    [Index(nameof(AssistantId), nameof(Created))]
    // Guide usage report covering indexes are defined in ApplicationDbContext.OnModelCreating
    // with IncludeProperties for optimal query performance
    [Index(nameof(ConversationId), nameof(Created))]
    [Index(nameof(AgentInvocationId), nameof(Category))]
    [Index(nameof(PublishedGuideId), nameof(Created))]
    [Index(nameof(SourceChannel), nameof(Created))]
    [Index(nameof(ExternalRequestId))]
    public class UsageEvent
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public DateTime Created { get; set; } = DateTime.UtcNow;

        // Attribution
        public string? UserId { get; set; }
        public Guid? ProjectId { get; set; }
        public Guid? NotebookId { get; set; }
        public Guid? ConversationId { get; set; }
        public Guid? ContentFileId { get; set; }
        public Guid? NotebookFileId { get; set; }
        public Guid? PublishedGuideId { get; set; }
        [MaxLength(64)]
        public string? SourceChannel { get; set; }
        [MaxLength(128)]
        public string? ExternalRequestId { get; set; }
        [MaxLength(255)]
        public string? ExternalUserIdentity { get; set; }
        
        /// <summary>
        /// The specific notebook conversation message this usage event is associated with.
        /// Enables precise linkage of cost/metrics to individual assistant or tool messages.
        /// Intentionally NOT a FK — usage data must never be modified or blocked by message deletion.
        /// </summary>
        public Guid? NotebookConversationMessageId { get; set; }
        
        /// <summary>
        /// The assistant/guide that generated this usage event.
        /// Enables direct querying of usage by assistant without complex joins.
        /// </summary>
        public Guid? AssistantId { get; set; }

        /// <summary>
        /// The guide/assistant that invoked this usage (for crew attribution).
        /// Populated for usage recorded inside agent invocations.
        /// </summary>
        public Guid? InvokingAssistantId { get; set; }
        
        /// <summary>
        /// If this usage occurred during an agent invocation (crew member call),
        /// links to that invocation for drill-down reporting.
        /// </summary>
        public Guid? AgentInvocationId { get; set; }
        
        // Classification
        public UsageCategory Category { get; set; }
        public string Service { get; set; } = string.Empty;    // e.g., AzureOpenAI, AzureDocIntel, AzureSpeech, KernelMemory
        public string Operation { get; set; } = string.Empty;  // e.g., chat, prebuilt-layout, Ask, Search, TTS, STT
        public string? ModelDeploymentId { get; set; }         // for inference events

        // Values
        public long ValueInput { get; set; }
        public long ValueCachedInput { get; set; }
        public long ValueReasoning { get; set; }
        public long ValueOutput { get; set; }
        public long ValueOther { get; set; }

        // Free-form metadata for details like counts, sizes, file paths, etc.
        public string? MetadataJson { get; set; }

        /// <summary>
        /// Base provider cost in USD before any markup. Nullable so that zero-cost events
        /// do not allocate unnecessary storage.
        /// </summary>
        [Precision(19,4)]
        public decimal? CostUsd { get; set; }

        /// <summary>
        /// Markup multiplier applied to <see cref="CostUsd"/> when computing the customer charge.
        /// For a 20% markup this value is 1.20 (NOT 0.20). Default for all existing and new rows is 1.20.
        /// </summary>
        [Precision(5,4)]
        public decimal MarkupPercent { get; set; } = 1.20m;

        /// <summary>
        /// Final customer charge in USD after markup has been applied.
        /// This is the value all API read operations should expose instead of recomputing markup.
        /// </summary>
        [Precision(19,4)]
        public decimal? ChargeUsd { get; set; }
    }
}


