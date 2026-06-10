using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuidesServiceTests
{
    [TestMethod]
    public async Task GetGuidesAsync_Returns_only_guide_kind_assistants()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-list-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        context.Assistants.AddRange(
            new Assistant { Name = "Guide One", Kind = AssistantKind.Guide, Created = DateTime.UtcNow },
            new Assistant { Name = "Assistant One", Kind = AssistantKind.Assistant, Created = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var guides = (await service.GetGuidesAsync()).ToList();

        guides.Should().ContainSingle();
        guides[0].Name.Should().Be("Guide One");
    }

    [TestMethod]
    public async Task GetGuideAsync_Returns_null_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var guide = await service.GetGuideAsync(Guid.NewGuid());

        guide.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateGuideAsync_UpdateGuideAsync_and_DeleteGuideAsync_round_trip()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-crud-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var created = await service.CreateGuideAsync(MinimalCreateGuideDto("Original Guide"));
        created.Name.Should().Be("Original Guide");

        var updated = await service.UpdateGuideAsync(created.Id, MinimalUpdateGuideDto("Updated Guide"));
        updated.Name.Should().Be("Updated Guide");

        var details = await service.GetGuideAsync(created.Id);
        details.Should().NotBeNull();
        details!.Guide.Name.Should().Be("Updated Guide");

        (await service.DeleteGuideAsync(created.Id)).Should().BeTrue();
        (await service.GetGuideAsync(created.Id)).Should().BeNull();
        (await service.DeleteGuideAsync(created.Id)).Should().BeFalse();
    }

    [TestMethod]
    public async Task Assistant_crud_and_listing_work_for_non_guide_assistants()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"assistants-crud-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var created = await service.CreateAssistantAsync(MinimalCreateAssistantDto("Crew Member"));
        var listed = (await service.GetAssistantsAsync()).ToList();
        listed.Should().ContainSingle(a => a.Id == created.Id);

        var updated = await service.UpdateAssistantAsync(created.Id, MinimalUpdateAssistantDto("Renamed Crew"));
        updated.Name.Should().Be("Renamed Crew");

        (await service.DeleteAssistantAsync(created.Id)).Should().BeTrue();
        (await service.GetAssistantAsync(created.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_Returns_valid_for_empty_members()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-validate-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var result = await service.ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest([]));

        result.IsValid.Should().BeTrue();
        result.Conflicts.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DuplicateGuideAsync_Throws_not_implemented()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-dup-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.DuplicateGuideAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    private static CreateGuideDto MinimalCreateGuideDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            HomePageMarkdown: "# Home",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            AuthProviders: null,
            Files: null,
            ConversationStarters: null,
            CrewMemberIds: null);

    private static UpdateGuideDto MinimalUpdateGuideDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            HomePageMarkdown: "# Home",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            AuthProviders: null,
            FileIdsToKeep: null,
            FilesToAdd: null,
            ConversationStarters: null,
            CrewMemberIds: null);

    private static CreateAssistantDto MinimalCreateAssistantDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            Files: null,
            ConversationStarters: null);

    private static UpdateAssistantDto MinimalUpdateAssistantDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            FileIdsToKeep: null,
            FilesToAdd: null,
            ConversationStarters: null);
}
