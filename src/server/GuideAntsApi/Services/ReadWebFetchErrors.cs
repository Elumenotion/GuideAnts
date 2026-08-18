using System.Net;

namespace GuideAntsApi.Services;

internal static class ReadWebFetchErrors
{
    internal static string BuildMessage(
        int? directStatusCode,
        string? directError,
        int? renderStatusCode,
        string? renderError)
    {
        return PickDominantFailure(directStatusCode, directError, renderStatusCode, renderError) switch
        {
            FetchFailureKind.Unauthorized =>
                "401 Unauthorized. Credentials required. Do not retry without an authenticated tool.",
            FetchFailureKind.Forbidden =>
                "403 Forbidden. This host blocks unauthenticated access. Do not retry — use a different source or local files.",
            FetchFailureKind.NotFound =>
                "404 Not Found. This URL does not exist. Do not retry — fix the path or use local file search.",
            FetchFailureKind.RateLimited =>
                "429 Rate limited. Do not retry now — use a different source.",
            FetchFailureKind.ServerError =>
                "Server error (5xx). You may retry once; if it fails again, use a different source.",
            FetchFailureKind.Timeout =>
                "Timed out. Do not retry this URL — try a lighter page or a different tool.",
            FetchFailureKind.EmptyContent =>
                "Page returned no usable content. This may be an API endpoint or JS-only page — use a different tool.",
            _ =>
                "Request failed. Do not retry — use a different source or tool."
        };
    }

    private static FetchFailureKind PickDominantFailure(
        int? directStatusCode,
        string? directError,
        int? renderStatusCode,
        string? renderError)
    {
        var direct = Classify(directStatusCode, directError);
        var render = Classify(renderStatusCode, renderError);
        return (FetchFailureKind)Math.Max((int)direct, (int)render);
    }

    private static FetchFailureKind Classify(int? statusCode, string? error)
    {
        if (statusCode is (int)HttpStatusCode.Unauthorized)
            return FetchFailureKind.Unauthorized;

        if (statusCode is (int)HttpStatusCode.Forbidden)
            return FetchFailureKind.Forbidden;

        if (statusCode is (int)HttpStatusCode.NotFound or (int)HttpStatusCode.Gone)
            return FetchFailureKind.NotFound;

        if (statusCode is (int)HttpStatusCode.TooManyRequests)
            return FetchFailureKind.RateLimited;

        if (statusCode is >= 500 and <= 599)
            return FetchFailureKind.ServerError;

        if (IsTimeout(error))
            return FetchFailureKind.Timeout;

        if (IsEmptyContent(error))
            return FetchFailureKind.EmptyContent;

        if (!string.IsNullOrWhiteSpace(error) &&
            (error.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("access denied", StringComparison.OrdinalIgnoreCase)))
        {
            return error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                ? FetchFailureKind.Unauthorized
                : FetchFailureKind.Forbidden;
        }

        return FetchFailureKind.Unknown;
    }

    private static bool IsTimeout(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyContent(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("exceeded max size", StringComparison.OrdinalIgnoreCase));

    private enum FetchFailureKind
    {
        Unknown = 0,
        EmptyContent = 1,
        Timeout = 2,
        ServerError = 3,
        RateLimited = 4,
        NotFound = 5,
        Forbidden = 6,
        Unauthorized = 7
    }
}
