using FluentAssertions;
using GuideAntsApi.Configuration;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public sealed class DocumentServerUrlResolverTests
{
    [TestMethod]
    public void ResolvePublicUrl_UsesRequestOrigin_NotApiBaseUrl()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost:5107");

        var publicUrl = DocumentServerUrlResolver.ResolvePublicUrl(httpContext);

        publicUrl.Should().Be("http://localhost:5107/api/documentserver/ds");
    }

    [TestMethod]
    public void ResolvePublicUrl_WhenXForwardedProtoHttps_UsesHttpsWithRequestHost()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("guideants-webapi-ui.example.azurecontainerapps.io");
        httpContext.Request.Headers["X-Forwarded-Proto"] = "https";

        var publicUrl = DocumentServerUrlResolver.ResolvePublicUrl(httpContext);

        publicUrl.Should().Be("https://guideants-webapi-ui.example.azurecontainerapps.io/api/documentserver/ds");
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

    [TestMethod]
    public void IsVersionedRuntimePath_WhenDocumentServerBundle_ReturnsTrue()
    {
        DocumentServerUrlResolver.IsVersionedRuntimePath(new PathString("/9.3.1-771df13545a4e98e7b7e2471bad9b874/web-apps/apps/presentationeditor/main/index.html"))
            .Should().BeTrue();
    }

    [TestMethod]
    public void IsVersionedRuntimePath_WhenNormalAppRoute_ReturnsFalse()
    {
        DocumentServerUrlResolver.IsVersionedRuntimePath(new PathString("/projects/123/files"))
            .Should().BeFalse();
    }
}
