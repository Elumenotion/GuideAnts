using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuidesServiceToolLimitsTests
{
    private static CreateAssistantDto EmptyCreateAssistant(string name) => new(
        Name: name,
        Description: "desc",
        Instructions: null,
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

    private static UpdateAssistantDto EmptyUpdateAssistant(string name) => new(
        Name: name,
        Description: "desc",
        Instructions: null,
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

    [TestMethod]
    public async Task CreateAssistantAsync_persists_tool_limit_fields()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-limits-create-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = EmptyCreateAssistant("Limited Search");
        dto = dto with { MaxToolCallsPerTurn = 12, MaxToolRoundsPerTurn = 4 };
        var created = await service.CreateAssistantAsync(dto);

        var details = await service.GetAssistantAsync(created.Id);
        details.Should().NotBeNull();
        details!.MaxToolCallsPerTurn.Should().Be(12);
        details.MaxToolRoundsPerTurn.Should().Be(4);

        var entity = await context.Assistants.AsNoTracking().SingleAsync(a => a.Id == created.Id);
        entity.MaxToolCallsPerTurn.Should().Be(12);
        entity.MaxToolRoundsPerTurn.Should().Be(4);
    }

    [TestMethod]
    public async Task UpdateAssistantAsync_rejects_negative_tool_limits()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-limits-negative-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var created = await service.CreateAssistantAsync(EmptyCreateAssistant("Assistant"));

        var update = EmptyUpdateAssistant("Assistant") with { MaxToolCallsPerTurn = 0 };
        var act = () => service.UpdateAssistantAsync(created.Id, update);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void AssistantDefinition_deserializes_tool_limit_fields_from_manifest_json()
    {
        const string json = """{"name":"Search","max_tool_calls_per_turn":12,"max_tool_rounds_per_turn":3}""";
        var definition = System.Text.Json.JsonSerializer.Deserialize<AssistantDefinition>(json);
        definition.Should().NotBeNull();
        definition!.MaxToolCallsPerTurn.Should().Be(12);
        definition.MaxToolRoundsPerTurn.Should().Be(3);
    }

    [TestMethod]
    public async Task UpdateGuideAsync_persists_crew_member_invocation_limit()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-limits-crew-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var memberDto = EmptyCreateAssistant("Crew Member") with { MaxToolCallsPerTurn = 20 };
        var member = await service.CreateAssistantAsync(memberDto);

        var guideDto = new CreateGuideDto(
            Name: "Guide",
            Description: "desc",
            Instructions: null,
            HomePageMarkdown: null,
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
            CrewMemberIds: [member.Id])
        {
            CrewMemberLimits = [new CrewMemberLimitDto(member.Id, 8)],
        };

        var guide = await service.CreateGuideAsync(guideDto);

        var details = await service.GetGuideAsync(guide.Id);
        details.Should().NotBeNull();
        details!.Crews.Should().ContainSingle();
        details.Crews[0].Members.Should().ContainSingle();
        details.Crews[0].Members[0].MaxToolCallsPerInvocation.Should().Be(8);
        details.Crews[0].Members[0].MaxToolCallsPerTurn.Should().Be(20);

        var guideMember = await context.GuideMembers.AsNoTracking().SingleAsync();
        guideMember.MaxToolCallsPerInvocation.Should().Be(8);
    }
}
