using Moq;
using System.Net;
using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaStackAdminClientProviderTests
{
    private sealed class CapturingFactory : IHttpClientFactory
    {
        public int Created { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Created++;
            return new HttpClient(new NullHandler());
        }

        private sealed class NullHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("not used in this test");
            }
        }
    }

    [TestMethod]
    public void GetClientForStack_ReturnsNull_ForGlobalRow()
    {
        var admin = new Mock<ILlamaRuntimeAdminClient>();
        var provider = new LlamaStackAdminClientProvider(
            admin.Object, new CapturingFactory(), NullLoggerFactory.Instance);

        provider.GetClientForStack("", null).Should().BeNull();
        provider.GetClientForStack(null, null).Should().BeNull();
        provider.GetClientForStack("   ", null).Should().BeNull();
    }

    [TestMethod]
    public void GetClientForStack_CachesPerStackAndKey()
    {
        var factory = new CapturingFactory();
        var provider = new LlamaStackAdminClientProvider(
            new Mock<ILlamaRuntimeAdminClient>().Object, factory, NullLoggerFactory.Instance);

        var first = provider.GetClientForStack("http://192.0.2.1:8112", null);
        var same = provider.GetClientForStack("http://192.0.2.1:8112/", null);
        var other = provider.GetClientForStack("http://192.0.2.1:8113", null);
        var rekeyed = provider.GetClientForStack("http://192.0.2.1:8112", "secret");

        first.Should().NotBeNull();
        same.Should().BeSameAs(first);
        other.Should().NotBeSameAs(first);
        rekeyed.Should().NotBeSameAs(first);
        factory.Created.Should().Be(3);
    }
}
