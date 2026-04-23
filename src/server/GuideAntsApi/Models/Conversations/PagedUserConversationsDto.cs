namespace GuideAntsApi.Models.Conversations;

public record PagedUserConversationsDto(
    IReadOnlyList<UserConversationDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages
);






