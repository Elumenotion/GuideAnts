using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Options;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GuideAntsApi.Tests.Services.SandboxWireApi;

[TestClass]
public sealed class SandboxWireCycleDetectorTests
{
    [TestMethod]
    public async Task BuildAncestorChainAsync_returns_owner_only_for_guide_to_crew_assistant_wire()
    {
        var ownerGuideId = Guid.NewGuid();
        var targetAssistantId = Guid.NewGuid();
        await using var db = CreateDbContext();

        db.Assistants.AddRange(
            new Assistant
            {
                Id = ownerGuideId,
                Name = "Owner Guide",
                Kind = AssistantKind.Guide,
                IsActive = true,
                SandboxWireApiConfigJson = JsonSerializer.Serialize(new SandboxWireApiConfigDto
                {
                    Enabled = true,
                    TargetAssistantId = targetAssistantId,
                }),
            },
            new Assistant
            {
                Id = targetAssistantId,
                Name = "Wire Target",
                Kind = AssistantKind.Assistant,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var sut = new SandboxWireCycleDetector(db);
        var ancestors = await sut.BuildAncestorChainAsync(ownerGuideId);

        ancestors.Should().Equal(ownerGuideId);
    }

    [TestMethod]
    public void Mint_succeeds_when_ancestors_contain_only_owner_and_target_is_crew_assistant()
    {
        var ownerGuideId = Guid.NewGuid();
        var targetAssistantId = Guid.NewGuid();
        var jwtService = new SandboxWireJwtService(Microsoft.Extensions.Options.Options.Create(new SandboxWireApiOptions
        {
            Issuer = "GuideAnts.Test",
            Audience = SandboxWireApiOptions.DefaultAudience,
            SigningKey = "guideants-integration-tests-sandbox-wire-signing-key-2026",
            InternalBaseUrl = "http://localhost/api/internal/sandbox/openai/v1",
            DefaultLifetimeMinutes = 35,
        }));

        var issued = jwtService.Mint(new SandboxWireExecutionGrant(
            ExecutionId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            NotebookId: Guid.NewGuid(),
            OwnerAssistantId: ownerGuideId,
            TargetAssistantId: targetAssistantId,
            TargetAssistantName: "Wire Target",
            AllowedEndpoints: ["chat.completions"],
            AttributionConversationId: null,
            AncestorAssistantIds: [ownerGuideId],
            Lifetime: TimeSpan.FromMinutes(10)));

        jwtService.TryValidate(issued.Token, out var grant, out var failureReason).Should().BeTrue(failureReason);
        grant!.TargetAssistantId.Should().Be(targetAssistantId);
    }

    [TestMethod]
    public async Task BuildEnvironmentAsync_mints_jwt_for_guide_to_crew_assistant_without_cycle_error()
    {
        var ownerGuideId = Guid.NewGuid();
        var targetAssistantId = Guid.NewGuid();
        await using var db = CreateDbContext();

        db.Assistants.AddRange(
            new Assistant
            {
                Id = ownerGuideId,
                Name = "Owner Guide",
                Kind = AssistantKind.Guide,
                IsActive = true,
                SandboxWireApiConfigJson = JsonSerializer.Serialize(new SandboxWireApiConfigDto
                {
                    Enabled = true,
                    TargetAssistantId = targetAssistantId,
                }),
            },
            new Assistant
            {
                Id = targetAssistantId,
                Name = "Wire Target",
                Kind = AssistantKind.Assistant,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var jwtService = new SandboxWireJwtService(Microsoft.Extensions.Options.Options.Create(new SandboxWireApiOptions
        {
            Issuer = "GuideAnts.Test",
            Audience = SandboxWireApiOptions.DefaultAudience,
            SigningKey = "guideants-integration-tests-sandbox-wire-signing-key-2026",
            InternalBaseUrl = "http://localhost/api/internal/sandbox/openai/v1",
            DefaultLifetimeMinutes = 35,
        }));

        var provisioner = new SandboxWireEnvironmentProvisioner(
            db,
            jwtService,
            new SandboxWireCycleDetector(db),
            Microsoft.Extensions.Options.Options.Create(new SandboxWireApiOptions
            {
                Issuer = "GuideAnts.Test",
                Audience = SandboxWireApiOptions.DefaultAudience,
                SigningKey = "guideants-integration-tests-sandbox-wire-signing-key-2026",
                InternalBaseUrl = "http://localhost/api/internal/sandbox/openai/v1",
                DefaultLifetimeMinutes = 35,
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SandboxWireEnvironmentProvisioner>.Instance);

        var environment = await provisioner.BuildEnvironmentAsync(new SandboxWireProvisionRequest(
            ExecutionId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            NotebookId: Guid.NewGuid(),
            OwnerAssistantId: ownerGuideId,
            AttributionConversationId: null,
            Lifetime: TimeSpan.FromMinutes(10)));

        environment.Should().NotBeNull();
        jwtService.TryValidate(environment!["OPENAI_API_KEY"], out var grant, out var failureReason).Should().BeTrue(failureReason);
        grant!.TargetAssistantId.Should().Be(targetAssistantId);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"sandbox-wire-cycle-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }
}
