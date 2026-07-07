using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Options;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.SandboxWireApi;

[TestClass]
public sealed class SandboxWireEnvironmentProvisionerTests
{
    [TestMethod]
    public async Task BuildEnvironmentAsync_returns_openai_env_when_job_forces_enabled()
    {
        var ownerGuideId = Guid.NewGuid();
        var targetAssistantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"sandbox-wire-provision-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(options);

        db.Assistants.AddRange(
            new Assistant
            {
                Id = ownerGuideId,
                Name = "Owner Guide",
                Kind = AssistantKind.Guide,
                IsActive = true,
                SandboxWireApiConfigJson = """{"enabled":false,"targetAssistantId":""" + $"\"{targetAssistantId}\"" + """}""",
            },
            new Assistant
            {
                Id = targetAssistantId,
                Name = "Target Assistant",
                Kind = AssistantKind.Assistant,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var cycleDetector = new Mock<ISandboxWireCycleDetector>();
        cycleDetector
            .Setup(d => d.WouldCreateCycleAsync(ownerGuideId, targetAssistantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        cycleDetector
            .Setup(d => d.BuildAncestorChainAsync(ownerGuideId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { ownerGuideId });

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
            cycleDetector.Object,
            Microsoft.Extensions.Options.Options.Create(new SandboxWireApiOptions
            {
                Issuer = "GuideAnts.Test",
                Audience = SandboxWireApiOptions.DefaultAudience,
                SigningKey = "guideants-integration-tests-sandbox-wire-signing-key-2026",
                InternalBaseUrl = "http://localhost/api/internal/sandbox/openai/v1",
                DefaultLifetimeMinutes = 35,
            }),
            NullLogger<SandboxWireEnvironmentProvisioner>.Instance);

        var environment = await provisioner.BuildEnvironmentAsync(new SandboxWireProvisionRequest(
            ExecutionId: Guid.NewGuid(),
            ProjectId: projectId,
            NotebookId: notebookId,
            OwnerAssistantId: ownerGuideId,
            AttributionConversationId: null,
            Lifetime: TimeSpan.FromMinutes(10),
            OverrideTargetAssistantId: targetAssistantId,
            ForceEnabled: true));

        environment.Should().NotBeNull();
        environment!["OPENAI_BASE_URL"].Should().Be("http://localhost/api/internal/sandbox/openai/v1");
        environment["OPENAI_API_KEY"].Should().NotBeNullOrWhiteSpace();
        jwtService.TryValidate(environment["OPENAI_API_KEY"], out var grant, out _).Should().BeTrue();
        grant!.TargetAssistantId.Should().Be(targetAssistantId);
    }
}
