using FluentAssertions;
using System.Net;
using GuideAntsApi.IntegrationTests.Infrastructure;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class AssistantAvatarEndpointTests : BaseIntegrationTest
{
    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        await InitializeSharedFactoryAsync(context);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
    }

    [TestMethod]
    public async Task GetAvatar_ReturnsImage_WhenFileExists()
    {
        // Arrange
        // Use a real assistant that is known to have an avatar file.
        const string assistantName = "Diagrams";

        // Act
        var response = await Client.GetAsync($"/api/assistants/avatar/{assistantName}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.ToString().Should().Be("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task GetAvatar_ReturnsNotFound_WhenAssistantDoesNotExist()
    {
        // Act
        var response = await Client.GetAsync("/api/assistants/avatar/Non-Existent-Assistant-ABCXYZ");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task GetAvatar_ReturnsNotFound_WhenAvatarFileDoesNotExist()
    {
        // Arrange
        // Use a real assistant that is known to NOT have an avatar file.
        const string assistantName = "Read Web";

        // Act
        var response = await Client.GetAsync($"/api/assistants/avatar/{assistantName}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
} 