using System.Globalization;
using System.Net.Http;

namespace GuideAntsApi.BackgroundJobs.Http;

public static class LocalServiceRequestHeaders
{
    public const string RequestTimeoutSeconds = "x-ga-request-timeout-seconds";

    public static void ApplyRequestTimeout(HttpRequestMessage request, int timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(
            RequestTimeoutSeconds,
            timeoutSeconds.ToString(CultureInfo.InvariantCulture));
    }
}
