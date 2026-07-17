using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class BundleDefinitionSettingsTests
{
    [TestMethod]
    public void Validate_AcceptsCanonicalFluxKleinDefinition()
    {
        var definition = new ImageGenerationBundleDefinitionDto(
            "flux2-klein-4b",
            "main",
            UpdatedAtUtc: null,
            new BundleDefinitionRolesDto(
                new BundleDefinitionRoleDto("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q4_K_S.gguf"),
                new BundleDefinitionRoleDto("black-forest-labs/FLUX.2-small-decoder", "full_encoder_small_decoder.safetensors"),
                new BundleDefinitionRoleDto("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf")),
            new BundleDefinitionSamplingDto(4, 1.0, "euler"));

        BundleDefinitionValidator.Validate(definition).Should().BeEmpty();
    }

    [TestMethod]
    public void Validate_RejectsMissingSampling()
    {
        var definition = new ImageGenerationBundleDefinitionDto(
            "flux2-klein-4b",
            "main",
            UpdatedAtUtc: null,
            new BundleDefinitionRolesDto(
                new BundleDefinitionRoleDto("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q4_K_S.gguf"),
                new BundleDefinitionRoleDto("black-forest-labs/FLUX.2-small-decoder", "full_encoder_small_decoder.safetensors"),
                new BundleDefinitionRoleDto("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf")),
            null!);

        BundleDefinitionValidator.Validate(definition).Should().Contain("sampling is required.");
    }

    [TestMethod]
    public void TryMapDownloadPayloadToDefinition_MapsSnakeCaseDownloadBody()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """
            {
              "bundle_id": "flux2-klein-4b",
              "diffusion_repo": "unsloth/FLUX.2-klein-4B-GGUF",
              "diffusion_file": "flux-2-klein-4b-Q4_K_S.gguf",
              "vae_repo": "black-forest-labs/FLUX.2-small-decoder",
              "vae_file": "full_encoder_small_decoder.safetensors",
              "text_encoder_repo": "unsloth/Qwen3-4B-GGUF",
              "text_encoder_file": "Qwen3-4B-Q4_K_M.gguf",
              "sampling_steps": 4,
              "sampling_cfg_scale": 1.0,
              "sampling_method": "euler",
              "revision": "main"
            }
            """);

        var definition = GuideAntsApi.Endpoints.Settings.SettingsImageGenerationBundleDefinitionsEndpoints
            .TryMapDownloadPayloadToDefinition(doc.RootElement);

        definition.Should().NotBeNull();
        definition!.BundleId.Should().Be("flux2-klein-4b");
        definition.Sampling.Steps.Should().Be(4);
        definition.Roles.Diffusion.File.Should().Be("flux-2-klein-4b-Q4_K_S.gguf");
    }
}
