using FluentAssertions;
using GuideAntsApi.Configuration;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public sealed class DocumentServerUrlResolverTests
{
    [TestMethod]
    public void ResolvePublicUrl_WhenApiBaseUrlConfigured_UsesConfiguredHttpsBase()
    {
        var options = new DocumentServerOptions
        {
            ApiBaseUrl = "https://guideants-webapi-ui.example.azurecontainerapps.io"
        };

        var publicUrl = DocumentServerUrlResolver.ResolvePublicUrl(options);

        publicUrl.Should().Be("https://guideants-webapi-ui.example.azurecontainerapps.io/api/documentserver/ds");
    }

    [TestMethod]
    public void ResolvePublicUrl_WhenApiBaseUrlMissing_FallsBackToRequestHost()
    {
        var options = new DocumentServerOptions();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost:5107");

        var publicUrl = DocumentServerUrlResolver.ResolvePublicUrl(options, httpContext);

        publicUrl.Should().Be("http://localhost:5107/api/documentserver/ds");
    }

    [TestMethod]
    public void ResolveUpstreamHost_WhenDefaultPort_OmitsPort()
    {
        var destinationUri = new Uri("http://documentserver.internal.example.azurecontainerapps.io/web-apps/api.js");

        DocumentServerUrlResolver.ResolveUpstreamHost(destinationUri)
            .Should().Be("documentserver.internal.example.azurecontainerapps.io");
    }

    [TestMethod]
    public void ResolveUpstreamHost_WhenNonDefaultPort_IncludesPort()
    {
        var destinationUri = new Uri("http://documentserver.internal.example.azurecontainerapps.io:8000/web-apps/api.js");

        DocumentServerUrlResolver.ResolveUpstreamHost(destinationUri)
            .Should().Be("documentserver.internal.example.azurecontainerapps.io:8000");
    }
}
