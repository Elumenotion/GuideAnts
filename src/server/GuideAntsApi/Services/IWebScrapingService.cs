namespace GuideAntsApi.Services;

public interface IWebScrapingService
{
    Task<string> GetPageContentAsync(string url);
} 
