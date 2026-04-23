using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Models.Guides
{
    public record AssistantFileMarkdownShadowDto(
        Guid Id,
        Guid OriginalAssistantFileId,
        string ContentHash,
        long FileSize,
        MarkdownExtractionStatus Status,
        string? ErrorMessage,
        DateTime Created,
        DateTime? ProcessedAt
    );
}



