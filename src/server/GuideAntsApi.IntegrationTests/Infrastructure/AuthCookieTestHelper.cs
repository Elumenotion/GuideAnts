using System.Net.Http.Headers;
using GuideAntsApi.Services.Auth;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

internal static class AuthCookieTestHelper
{
    public static string? ReadAuthToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return null;
        }

        foreach (var header in setCookieHeaders)
        {
            var token = ReadCookieValue(header, AuthCookieConstants.CookieName);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    public static void SetBearerToken(HttpClient client, string? token)
    {
        client.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    private static string? ReadCookieValue(string setCookieHeader, string cookieName)
    {
        var prefix = cookieName + "=";
        if (!setCookieHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var remainder = setCookieHeader[prefix.Length..];
        var separatorIndex = remainder.IndexOf(';');
        return separatorIndex >= 0 ? remainder[..separatorIndex] : remainder;
    }
}
