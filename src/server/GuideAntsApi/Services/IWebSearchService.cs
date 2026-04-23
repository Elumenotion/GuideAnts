using GuideAntsApi.Models;

namespace GuideAntsApi.Services;

public interface IWebSearchService
{
    Task<WebSearchToolResponse> SearchAsync(
        string query,
        int count = 20,
        int skip = 0,
        CancellationToken cancellationToken = default);
}
