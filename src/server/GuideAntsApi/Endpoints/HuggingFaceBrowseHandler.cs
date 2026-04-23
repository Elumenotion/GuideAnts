using GuideAntsApi.Services.HuggingFace;

namespace GuideAntsApi.Endpoints;

/// <summary>
/// Shared handler for the Hugging Face repository browse endpoint. Both the
/// canonical <c>/api/settings/huggingface/...</c> path and the legacy
/// <c>/api/settings/llama/huggingface/...</c> alias route through this
/// method so there is no service-specific branching inside the proxy. The
/// server is service-agnostic; classification is done on the client.
/// </summary>
internal static class HuggingFaceBrowseHandler
{
    /// <summary>
    /// Optional request header callers can set so the server can emit a
    /// structured telemetry entry tagged with the calling service (e.g.
    /// <c>ImageGeneration</c>, <c>SpeechTranscription</c>, <c>Embeddings</c>).
    /// Missing header falls through silently.
    /// </summary>
    public const string ServiceOriginHeaderName = "X-Service-Origin";

    public static async Task<IResult> ExecuteAsync(
        string owner,
        string repo,
        HttpRequest request,
        IHuggingFaceRepositoryBrowser browser,
        ILoggerFactory loggerFactory,
        bool legacyLlamaPath,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("HuggingFaceBrowse");
        var serviceOrigin = ResolveServiceOrigin(request, legacyLlamaPath);

        try
        {
            var listing = await browser
                .ListFilesAsync(owner, repo, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "HfBrowseByService: serviceId={ServiceId} repository={Repository} gated={Gated} tokenUsed={TokenUsed} fileCount={FileCount} legacyPath={LegacyPath}",
                serviceOrigin,
                listing.Repository,
                listing.Gated,
                listing.TokenUsed,
                listing.Files.Count,
                legacyLlamaPath);

            return Results.Ok(listing);
        }
        catch (HuggingFaceBrowseException ex)
        {
            logger.LogInformation(
                "HfBrowseByService: serviceId={ServiceId} repository={Owner}/{Repo} failed code={Code} status={Status} legacyPath={LegacyPath}",
                serviceOrigin,
                owner,
                repo,
                ex.Code,
                (int)ex.StatusCode,
                legacyLlamaPath);

            return Results.Json(
                new
                {
                    code = ex.Code,
                    message = ex.Message,
                    detail = ex.Detail,
                },
                statusCode: (int)ex.StatusCode);
        }
    }

    private static string ResolveServiceOrigin(HttpRequest request, bool legacyLlamaPath)
    {
        // Header is optional. A missing / whitespace-only value falls through
        // as "unknown"; the legacy /llama/... path records a distinct tag so
        // telemetry can tell clients on the old alias apart from clients that
        // simply did not send the header on the neutral path.
        if (request.Headers.TryGetValue(ServiceOriginHeaderName, out var values))
        {
            var candidate = values.ToString()?.Trim();
            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }
        }

        return legacyLlamaPath ? "LegacyLlamaAlias" : "unknown";
    }
}
