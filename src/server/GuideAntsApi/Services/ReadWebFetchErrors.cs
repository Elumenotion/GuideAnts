using System.Net;

namespace GuideAntsApi.Services;

internal static class ReadWebFetchErrors
{
    private const string NoRetrySuffix =
        " Do not retry or issue another ReadWeb tool call for this invocation.";

    internal static string BuildMessage(
        int? directStatusCode,
        string? directError,
        int? renderStatusCode,
        string? renderError)
    {
        return PickDominantFailure(directStatusCode, directError, renderStatusCode, renderError) switch
        {
            FetchFailureKind.Unauthorized =>
                "401 Unauthorized. Credentials are required." + NoRetrySuffix,
            FetchFailureKind.Forbidden =>
                "403 Forbidden. This host blocks unauthenticated access." + NoRetrySuffix,
            FetchFailureKind.NotFound =>
                "404 Not Found. This URL does not exist." + NoRetrySuffix,
            FetchFailureKind.RateLimited =>
                "429 Rate limited." + NoRetrySuffix,
            FetchFailureKind.ServerError =>
                "Server error (5xx)." + NoRetrySuffix,
            FetchFailureKind.Timeout =>
                "Timed out." + NoRetrySuffix,
            FetchFailureKind.EmptyContent =>
                "Page returned no usable content." + NoRetrySuffix,
            _ =>
                "Request failed." + NoRetrySuffix
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
