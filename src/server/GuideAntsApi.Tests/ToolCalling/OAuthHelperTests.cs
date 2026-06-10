using System.Reflection;
using AntRunner.ToolCalling.Identity;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class OAuthHelperTests
{
    private static readonly Type HelperType = typeof(OAuthHelper);
    private static readonly FieldInfo CacheField = HelperType.GetField("CachedTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly Type CachedTokenType = HelperType.GetNestedType("CachedToken", BindingFlags.NonPublic)!;

    [TestInitialize]
    public void Initialize()
    {
        ClearCache();
    }

    [TestCleanup]
    public void Cleanup()
    {
        ClearCache();
    }

    [TestMethod]
    public async Task GetToken_WhenFreshTokenIsCached_ReturnsBearerTokenWithoutInteractiveFlow()
    {
        var token = CreateCachedToken("cached-access-token", DateTimeOffset.UtcNow.AddMinutes(15));
        SetCachedToken("client-a", token);

        var result = await OAuthHelper.GetToken(
            clientId: "client-a",
            tenantId: "tenant-a",
            scopes: ["scope.read"]);

        result.Should().Be("Bearer cached-access-token");
    }

    private static object GetTokenCache()
    {
        return CacheField.GetValue(null)!;
    }

    private static void SetCachedToken(string clientId, object token)
    {
        var cache = GetTokenCache();
        cache.GetType().GetProperty("Item")!.SetValue(cache, token, [clientId]);
    }

    private static void ClearCache()
    {
        var cache = GetTokenCache();
        cache.GetType().GetMethod("Clear")!.Invoke(cache, null);
    }

    private static object CreateCachedToken(string accessToken, DateTimeOffset expiresOn)
    {
        var token = Activator.CreateInstance(CachedTokenType)!;

        var accessTokenProperty = CachedTokenType.GetProperty("AccessToken", BindingFlags.Public | BindingFlags.Instance)!;
        var expiresOnProperty = CachedTokenType.GetProperty("ExpiresOn", BindingFlags.Public | BindingFlags.Instance)!;

        accessTokenProperty.SetValue(token, accessToken);
        expiresOnProperty.SetValue(token, expiresOn);

        return token;
    }
}
