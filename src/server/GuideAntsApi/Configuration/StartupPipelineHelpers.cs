namespace GuideAntsApi.Configuration;

/// <summary>
/// Startup pipeline helpers extracted for unit testing.
/// </summary>
public static class StartupPipelineHelpers
{
    public static bool ShouldUseHttpsRedirection(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var urls = configuration["ASPNETCORE_URLS"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        return !string.IsNullOrWhiteSpace(urls)
            && urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldUseForwardedHeaders(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue<bool>("ForwardedHeaders:Enabled");
    }
}
