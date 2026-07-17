using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class BundleDefinitionMigrationServiceTests
{
    [TestMethod]
    public async Task MigrateAsync_ImportsCheckedInDefaultsIntoSettingsStore()
    {
        var contentRoot = ResolveGuideAntsApiContentRoot();
        Directory.Exists(Path.Combine(contentRoot, ImageGenerationBundleDefinitionContracts.DefaultsRelativePath))
            .Should().BeTrue("checked-in bundle defaults must be present in the API project");

        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var stored = new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase);

        SetupSettingsMock(settings, stored);

        var service = CreateService(contentRoot, settings);

        var report = await service.MigrateAsync();

        report.DefaultsDiscovered.Should().Be(3);
        report.DefaultsImported.Should().Be(3);
        report.Failed.Should().Be(0);
        stored.Should().ContainKey("flux2-klein-4b");
        stored["flux2-klein-4b"].Sampling.Steps.Should().Be(4);
        stored.Should().ContainKey("FLUX.2-dev");
        stored["FLUX.2-dev"].Sampling.Steps.Should().Be(28);
    }

    [TestMethod]
    public async Task MigrateAsync_RenamesLegacyBundleIdAndPreservesRolesInSettingsStore()
    {
        var contentRoot = ResolveGuideAntsApiContentRoot();
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var stored = new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["flux2-klein-4b-q4ks"] = new ImageGenerationBundleDefinitionDto(
                BundleId: "flux2-klein-4b-q4ks",
                Revision: "main",
                UpdatedAtUtc: null,
                Roles: new BundleDefinitionRolesDto(
                    new BundleDefinitionRoleDto("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q8_0.gguf"),
                    new BundleDefinitionRoleDto("black-forest-labs/FLUX.2-small-decoder", "full_encoder_small_decoder.safetensors"),
                    new BundleDefinitionRoleDto("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf")),
                Sampling: new BundleDefinitionSamplingDto(4, 1.0, "euler")),
        };

        SetupSettingsMock(settings, stored);
        var service = CreateService(contentRoot, settings);

        await service.MigrateAsync();

        stored.Should().ContainKey("flux2-klein-4b");
        stored.Should().NotContainKey("flux2-klein-4b-q4ks");
        stored["flux2-klein-4b"].Roles.Diffusion.File.Should().Be("flux-2-klein-4b-Q8_0.gguf");
    }

    [TestMethod]
    public async Task MigrateAsync_MergesLegacyInstallConfigWhenCanonicalDefaultsAlreadyExist()
    {
        var contentRoot = ResolveGuideAntsApiContentRoot();
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var stored = new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["flux2-klein-4b"] = new ImageGenerationBundleDefinitionDto(
                BundleId: "flux2-klein-4b",
                Revision: "main",
                UpdatedAtUtc: null,
                Roles: new BundleDefinitionRolesDto(
                    new BundleDefinitionRoleDto("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q4_K_S.gguf"),
                    new BundleDefinitionRoleDto("black-forest-labs/FLUX.2-small-decoder", "full_encoder_small_decoder.safetensors"),
                    new BundleDefinitionRoleDto("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf")),
                Sampling: new BundleDefinitionSamplingDto(4, 1.0, "euler")),
            ["flux2-klein-4b-q4ks"] = new ImageGenerationBundleDefinitionDto(
                BundleId: "flux2-klein-4b-q4ks",
                Revision: "main",
                UpdatedAtUtc: null,
                Roles: new BundleDefinitionRolesDto(
                    new BundleDefinitionRoleDto("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q8_0.gguf"),
                    new BundleDefinitionRoleDto("black-forest-labs/FLUX.2-small-decoder", "full_encoder_small_decoder.safetensors"),
                    new BundleDefinitionRoleDto("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf")),
                Sampling: new BundleDefinitionSamplingDto(4, 1.0, "euler")),
        };

        SetupSettingsMock(settings, stored);
        var service = CreateService(contentRoot, settings);

        await service.MigrateAsync();

        stored["flux2-klein-4b"].Roles.Diffusion.File.Should().Be("flux-2-klein-4b-Q8_0.gguf");
        stored.Should().NotContainKey("flux2-klein-4b-q4ks");
    }

    [TestMethod]
    public async Task MigrateAsync_DoesNotOverwriteValidCustomConfigWithCheckedInDefaults()
    {
        var contentRoot = ResolveGuideAntsApiContentRoot();
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var stored = new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["flux2-klein-4b"] = new ImageGenerationBundleDefinitionDto(
                BundleId: "flux2-klein-4b",
                Revision: "main",
                UpdatedAtUtc: null,
                Roles: new BundleDefinitionRolesDto(
                    new BundleDefinitionRoleDto("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q8_0.gguf"),
                    new BundleDefinitionRoleDto("black-forest-labs/FLUX.2-small-decoder", "full_encoder_small_decoder.safetensors"),
                    new BundleDefinitionRoleDto("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf")),
                Sampling: new BundleDefinitionSamplingDto(4, 1.0, "euler")),
        };

        SetupSettingsMock(settings, stored);
        var service = CreateService(contentRoot, settings);

        await service.MigrateAsync();

        stored["flux2-klein-4b"].Roles.Diffusion.File.Should().Be("flux-2-klein-4b-Q8_0.gguf");
    }

    [TestMethod]
    public async Task MigrateAsync_DoesNotReplaceExistingBundleWithCheckedInDefaultsWhenValidationFails()
    {
        var contentRoot = ResolveGuideAntsApiContentRoot();
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        var stored = new Dictionary<string, ImageGenerationBundleDefinitionDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["flux2-klein-4b"] = new ImageGenerationBundleDefinitionDto(
                BundleId: "flux2-klein-4b",
                Revision: "main",
                UpdatedAtUtc: null,
                Roles: new BundleDefinitionRolesDto(
                    new BundleDefinitionRoleDto("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q8_0.gguf"),
                    new BundleDefinitionRoleDto("black-forest-labs/FLUX.2-small-decoder", "full_encoder_small_decoder.safetensors"),
                    new BundleDefinitionRoleDto("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf")),
                Sampling: new BundleDefinitionSamplingDto(0, 0, "")),
        };

        SetupSettingsMock(settings, stored);
        var service = CreateService(contentRoot, settings);

        await service.MigrateAsync();

        stored["flux2-klein-4b"].Roles.Diffusion.File.Should().Be("flux-2-klein-4b-Q8_0.gguf");
        stored["flux2-klein-4b"].Sampling.Steps.Should().Be(0);
    }

    private static void SetupSettingsMock(
        Mock<IApplicationSettingsService> settings,
        Dictionary<string, ImageGenerationBundleDefinitionDto> stored)
    {
        settings
            .Setup(s => s.GetImageGenerationBundleDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored.Values.ToList());
        settings
            .Setup(s => s.ReplaceImageGenerationBundleDefinitionsAsync(It.IsAny<IReadOnlyList<ImageGenerationBundleDefinitionDto>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<ImageGenerationBundleDefinitionDto> definitions, CancellationToken _) =>
            {
                stored.Clear();
                foreach (var definition in definitions)
                {
                    stored[definition.BundleId] = definition;
                }
                return Task.CompletedTask;
            });
        settings
            .Setup(s => s.GetServiceModesAsync(RoutedServiceNames.ImageGeneration, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ServiceModeDto>());
    }

    private static BundleDefinitionMigrationService CreateService(
        string contentRoot,
        Mock<IApplicationSettingsService> settings)
    {
        var environment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        environment.SetupGet(e => e.ContentRootPath).Returns(contentRoot);

        return new BundleDefinitionMigrationService(
            environment.Object,
            settings.Object,
            NullLogger<BundleDefinitionMigrationService>.Instance);
    }

    private static string ResolveGuideAntsApiContentRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GuideAntsApi")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "GuideAntsApi")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "GuideAntsApi")),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, ImageGenerationBundleDefinitionContracts.DefaultsRelativePath)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate GuideAntsApi content root containing bundle defaults.");
    }
}
