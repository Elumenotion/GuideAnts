namespace GuideAntsApi.Services.Auth;

public static class AuthCookieConstants
{
    public const string CookieName = "GuideAnts.Auth";
}

public interface IAuthCookieService
{
    void AppendAuthCookie(HttpResponse response, HttpRequest request, IssuedJwtToken issuedToken);

    void ClearAuthCookie(HttpResponse response, HttpRequest request);
}

public sealed class AuthCookieService : IAuthCookieService
{
    public void AppendAuthCookie(HttpResponse response, HttpRequest request, IssuedJwtToken issuedToken)
    {
        response.Cookies.Append(
            AuthCookieConstants.CookieName,
            issuedToken.Token,
            CreateCookieOptions(request, issuedToken.ExpiresAtUtc));
    }

    public void ClearAuthCookie(HttpResponse response, HttpRequest request)
    {
        response.Cookies.Delete(AuthCookieConstants.CookieName, CreateCookieOptions(request, expires: null));
    }

    private static CookieOptions CreateCookieOptions(HttpRequest request, DateTime? expires)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        };

        if (expires.HasValue)
        {
            options.Expires = new DateTimeOffset(DateTime.SpecifyKind(expires.Value, DateTimeKind.Utc));
        }

        return options;
    }
}
