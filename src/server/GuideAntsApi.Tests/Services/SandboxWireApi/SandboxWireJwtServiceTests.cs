using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.Services.SandboxWireApi;

[TestClass]
public sealed class SandboxWireJwtServiceTests
{
    private static SandboxWireJwtService CreateSut() =>
        new(Microsoft.Extensions.Options.Options.Create(new SandboxWireApiOptions
        {
            SigningKey = "0123456789abcdef0123456789abcdef",
            Issuer = "GuideAnts.Tests",
            Audience = "GuideAnts.SandboxWire",
            InternalBaseUrl = "http://localhost/api/internal/sandbox/openai/v1",
            DefaultLifetimeMinutes = 30,
        }));

    [TestMethod]
    public void Mint_and_validate_round_trips_limit_claims()
    {
        var sut = CreateSut();
        var grant = new SandboxWireExecutionGrant(
            ExecutionId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            NotebookId: Guid.NewGuid(),
            OwnerAssistantId: Guid.NewGuid(),
            TargetAssistantId: Guid.NewGuid(),
            TargetAssistantName: "target",
            AllowedEndpoints: ["chat.completions"],
            AttributionConversationId: null,
            AncestorAssistantIds: [],
            Lifetime: TimeSpan.FromMinutes(10),
            DailyLimitUsd: 12.5m,
            MonthlyLimitUsd: 99m);

        var issued = sut.Mint(grant);
        var valid = sut.TryValidate(issued.Token, out var parsed, out var failureReason);

        valid.Should().BeTrue(failureReason);
        parsed.Should().NotBeNull();
        parsed!.DailyLimitUsd.Should().Be(12.5m);
        parsed.MonthlyLimitUsd.Should().Be(99m);
    }

    [TestMethod]
    public void ResolveAllowedEndpoints_respects_disabled_flags()
    {
        var endpoints = SandboxWireEnvironmentProvisioner.ResolveAllowedEndpoints(new Models.Guides.SandboxWireApiConfigDto
        {
            EndpointFlags = new Models.Guides.PublishedWireApiEndpointFlagsDto
            {
                Models = true,
                ChatCompletions = false,
                Responses = false,
                Messages = false,
                Embeddings = false,
                ImageGenerations = false,
                AudioTranscriptions = false,
                AudioSpeech = false,
            },
        });

        endpoints.Should().Equal("models");
    }
}
