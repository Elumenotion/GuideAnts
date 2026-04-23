namespace GuideAntsApi.Models.Conversations;

public class UserConversationsQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
    public string SortBy { get; set; } = "date"; // "date", "project", "notebook"
    public string SortOrder { get; set; } = "desc"; // "asc", "desc"
}






