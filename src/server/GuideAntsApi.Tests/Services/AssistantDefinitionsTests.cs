using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GuideAntsApi.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class AssistantDefinitionsTests
{
    private static ApplicationDbContext InMem(string name) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(name).Options);

    [TestMethod]
    public async Task AssistantsListingReturnsCrewWithAvatarUrl()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var asstRoot = Path.Combine(tmp, "AssistantDefinitions");
        var pyDir = Path.Combine(asstRoot, "Python Ants", "HostExtensions", "UI");
        Directory.CreateDirectory(pyDir);
        File.WriteAllBytes(Path.Combine(pyDir, "avatar.png"), new byte[] { 0x89,0x50 });
        File.WriteAllText(Path.Combine(asstRoot, "Python Ants", "manifest.json"), "{}");

        var db = InMem("crew");
        var template = new NotebookTemplate { Id = Guid.NewGuid(), TemplateName = "Code Notebook" };
        db.NotebookTemplates.Add(template);

        var owner = new User { Id = Guid.NewGuid(), Email = "owner@example.com", Name = "Owner" };
        db.Users.Add(owner);

        var pythonAnts = new Assistant
        {
            Name = "Python Ants",
            Kind = AssistantKind.Assistant,
            IsActive = true,
            IsGlobal = true,
            AvatarImageBytes = new byte[] { 0x89, 0x50 }
        };
        var codeNotebookGuide = new Assistant
        {
            Name = "Code Notebook",
            Kind = AssistantKind.Guide,
            IsActive = true,
            IsGlobal = true
        };
        db.Assistants.AddRange(pythonAnts, codeNotebookGuide);
        db.SaveChanges();

        db.GuideMembers.Add(new GuideMember
        {
            GuideId = codeNotebookGuide.Id,
            AssistantId = pythonAnts.Id,
            DisplayOrder = 0
        });
        db.SaveChanges();

        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(db);
        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);
        var scopeFactoryMock2 = new Mock<IServiceScopeFactory>();
        scopeFactoryMock2.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var svc = new NotebookTemplateService(
            scopeFactoryMock2.Object,
            GuideAntsApi.Tests.TestUtils.EmptySystemGuideCatalogFilter.Instance,
            NullLogger<NotebookTemplateService>.Instance);
        var crew = await svc.GetAssistantsAsync(template.Id, userId: owner.Id);
        Assert.AreEqual(2, crew.Count);
        var pythonAntsDto = crew.Single(a => a.Name == "Python Ants");
        Assert.IsTrue(pythonAntsDto.AvatarUrl.Contains("/api/assistants/avatar/Python%20Ants"));
    }

    [TestMethod]
    public void DatabaseMaterialization_DoesNotCopyModelReasoningChoicesIntoAssistantMetadata()
    {
        var crewAssistant = new Assistant
        {
            Name = "Crew Helper",
            Kind = AssistantKind.Assistant,
            IsActive = true,
            IsGlobal = true
        };

        var guide = new Assistant
        {
            Name = "Reasoning Guide",
            Kind = AssistantKind.Guide,
            IsActive = true,
            IsGlobal = true,
            ModelId = "gpt-5-mini",
            Model = new Model
            {
                ModelId = "gpt-5-mini",
                DisplayName = "GPT-5 mini",
                Provider = "openai-chat",
                ReasoningChoicesJson = "[\"minimal\",\"low\",\"medium\",\"high\"]",
                IsActive = true
            }
        };

        guide.CrewMembers.Add(new GuideMember
        {
            Guide = guide,
            GuideId = guide.Id,
            Assistant = crewAssistant,
            AssistantId = crewAssistant.Id,
            DisplayOrder = 0
        });

        var method = typeof(DatabaseStorage).GetMethod(
            "MaterializeAssistant",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var storageMetadata = method!.Invoke(null, [guide]) as AssistantStorageMetadata;

        storageMetadata.Should().NotBeNull();
        storageMetadata!.AdditionalMetadata.Should().NotBeNull();
        storageMetadata.AdditionalMetadata.Should().ContainKey("__crew_names__");
        storageMetadata.AdditionalMetadata.Should().NotContainKey("__model_reasoning_choices__");
    }

    [TestMethod]
    public void DatabaseMaterialization_UsesSamplingBagOnly_ForCloudModel()
    {
        var assistant = new Assistant
        {
            Name = "Claude Helper",
            Kind = AssistantKind.Assistant,
            IsActive = true,
            ModelId = "claude-haiku-4-5-20251001",
            Temperature = 1.0f,
            TopP = 1.0,
            ReasoningEffort = "medium",
            SamplingParametersJson = """{"temperature":0.35,"top_p":0.75,"min_p":0.05}""",
            Model = new Model
            {
                ModelId = "claude-haiku-4-5-20251001",
                DisplayName = "Claude Haiku 4.5",
                Provider = "anthropic",
                ReasoningChoicesJson = "[\"minimal\",\"low\",\"medium\",\"high\"]",
                IsActive = true
            }
        };

        var storageMetadata = Materialize(assistant);

        using var manifest = JsonDocument.Parse(storageMetadata.ManifestJson);
        manifest.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
        manifest.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
        manifest.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        storageMetadata.SamplingParametersJson.Should().NotBeNullOrWhiteSpace();
        using var sampling = JsonDocument.Parse(storageMetadata.SamplingParametersJson!);
        sampling.RootElement.GetProperty("temperature").GetDouble().Should().BeApproximately(0.35, 0.001);
        sampling.RootElement.GetProperty("top_p").GetDouble().Should().BeApproximately(0.75, 0.001);
        sampling.RootElement.GetProperty("min_p").GetDouble().Should().BeApproximately(0.05, 0.001);
    }

    [TestMethod]
    public void DatabaseMaterialization_CloudRuntimeConfig_DoesNotBackfillTypedKnobsIntoSamplingBag()
    {
        var assistant = new Assistant
        {
            Name = "Cloud Runtime Profile Assistant",
            Kind = AssistantKind.Assistant,
            IsActive = true,
            ModelId = "gpt-5",
            Temperature = 0.25f,
            TopP = 0.85,
            ReasoningEffort = "high",
            SamplingParametersJson = """{"min_p":0.1}""",
            Model = new Model
            {
                ModelId = "gpt-5",
                DisplayName = "GPT-5",
                Provider = "openai-responses",
                RuntimeConfigJson = """{"runtimeProfileId":"openai_reasoning_v1"}""",
                IsActive = true
            }
        };

        var storageMetadata = Materialize(assistant);

        using var manifest = JsonDocument.Parse(storageMetadata.ManifestJson);
        manifest.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
        manifest.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
        manifest.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        storageMetadata.SamplingParametersJson.Should().NotBeNullOrWhiteSpace();
        using var sampling = JsonDocument.Parse(storageMetadata.SamplingParametersJson!);
        sampling.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
        sampling.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
        sampling.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        sampling.RootElement.GetProperty("min_p").GetDouble().Should().BeApproximately(0.1, 0.001);
    }

    [TestMethod]
    public void DatabaseMaterialization_TypedSamplingKnobsAreNotProjected_WhenSamplingBagMissing()
    {
        var assistant = new Assistant
        {
            Name = "Bag First Assistant",
            Kind = AssistantKind.Assistant,
            IsActive = true,
            ModelId = "openrouter-model",
            Temperature = 0.9f,
            TopP = 0.9,
            Model = new Model
            {
                ModelId = "openrouter-model",
                DisplayName = "OpenRouter Model",
                Provider = "openrouter",
                IsActive = true
            }
        };

        var storageMetadata = Materialize(assistant);
        storageMetadata.SamplingParametersJson.Should().BeNull();
    }

    [TestMethod]
    public void DatabaseMaterialization_PreservesSamplingBagPayloadVerbatim()
    {
        const string rawSamplingBag = """{"temperature":0.45,"custom_knob":"aggressive","nested":{"top_k":42}}""";
        var assistant = new Assistant
        {
            Name = "Raw Bag Assistant",
            Kind = AssistantKind.Assistant,
            IsActive = true,
            ModelId = "gpt-5-mini",
            Temperature = 0.9f,
            TopP = 0.2,
            ReasoningEffort = "high",
            SamplingParametersJson = rawSamplingBag,
            Model = new Model
            {
                ModelId = "gpt-5-mini",
                DisplayName = "GPT-5 mini",
                Provider = "openai-chat",
                IsActive = true
            }
        };

        var storageMetadata = Materialize(assistant);
        storageMetadata.SamplingParametersJson.Should().Be(rawSamplingBag);
    }

    private static AssistantStorageMetadata Materialize(Assistant assistant)
    {
        var method = typeof(DatabaseStorage).GetMethod(
            "MaterializeAssistant",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        return (method!.Invoke(null, [assistant]) as AssistantStorageMetadata)!;
    }
} 
