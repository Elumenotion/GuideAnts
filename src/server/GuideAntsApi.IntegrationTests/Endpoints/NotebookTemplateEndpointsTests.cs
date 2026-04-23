using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class NotebookTemplateEndpointsTests : BaseEndpointTest
{
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        await InitializeSharedFactoryAsync(context);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
    }

    [TestMethod]
    public async Task GetNotebookTemplates_ReturnsTemplatesWithExpectedFields()
    {
        // Act
        var response = await Client.GetAsync("/api/notebook-templates");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var templates = await response.Content.ReadFromJsonAsync<List<NotebookTemplateSummaryDto>>();
        templates.Should().NotBeNull();
        templates!.Should().NotBeEmpty();

        // Check that required fields are populated for first template
        var first = templates.First();
        first.Id.Should().NotBe(Guid.Empty);
        first.TemplateName.Should().NotBeNullOrWhiteSpace();
        first.Description.Should().NotBeNull();
        first.AvatarUrl.Should().NotBeNull();
    }

    [TestMethod]
    public async Task GetNotebookTemplateAvatar_ReturnsImage()
    {
        // Arrange – get templates first
        var templates = await Client.GetFromJsonAsync<List<NotebookTemplateSummaryDto>>("/api/notebook-templates");
        Assert.IsNotNull(templates);
        var first = templates!.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.AvatarUrl));
        Assert.IsNotNull(first, "Could not find a template with an avatar");

        // Act
        var avatarResponse = await Client.GetAsync(first.AvatarUrl);

        // Assert
        avatarResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var mediaType = avatarResponse.Content.Headers.ContentType?.MediaType;
        mediaType.Should().NotBeNull();
        mediaType!.Should().StartWith("image");
        var bytes = await avatarResponse.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
    }
} 