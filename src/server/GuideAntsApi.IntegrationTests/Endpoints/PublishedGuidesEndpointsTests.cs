using System.Net;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class PublishedGuidesEndpointsTests
{
    private static WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        var baseFactory = new TestWebApplicationFactory();
        await baseFactory.InitializeAsync();
        _factory = baseFactory;
    }

    [TestInitialize]
    public void TestInitialize()
    {
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client.Dispose();
    }

    [TestMethod]
    public async Task GetPublishedGuide_Returns_not_found_for_unknown_id()
    {
        var response = await _client.GetAsync($"/api/published/guides/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
