using System.Net.Http;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Http;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class LocalServiceRequestHeadersTests
{
    [TestMethod]
    public void ApplyRequestTimeout_AddsHeader_WhenTimeoutIsPositive()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/emb/embed");

        LocalServiceRequestHeaders.ApplyRequestTimeout(request, 450);

        request.Headers.TryGetValues(LocalServiceRequestHeaders.RequestTimeoutSeconds, out var values)
            .Should().BeTrue();
        values!.Single().Should().Be("450");
    }

    [TestMethod]
    public void ApplyRequestTimeout_DoesNotAddHeader_WhenTimeoutIsZeroOrNegative()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/emb/embed");

        LocalServiceRequestHeaders.ApplyRequestTimeout(request, 0);

        request.Headers.Contains(LocalServiceRequestHeaders.RequestTimeoutSeconds).Should().BeFalse();
    }
}
