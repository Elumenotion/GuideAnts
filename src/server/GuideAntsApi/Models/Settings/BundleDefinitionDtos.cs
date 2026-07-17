using System.Text.Json.Serialization;

namespace GuideAntsApi.Models.Settings;

public sealed record BundleDefinitionRoleDto(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("file")] string File);

public sealed record BundleDefinitionSamplingDto(
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("cfgScale")] double CfgScale,
    [property: JsonPropertyName("samplingMethod")] string SamplingMethod);

public sealed record BundleDefinitionRolesDto(
    [property: JsonPropertyName("diffusion")] BundleDefinitionRoleDto Diffusion,
    [property: JsonPropertyName("vae")] BundleDefinitionRoleDto Vae,
    [property: JsonPropertyName("textEncoder")] BundleDefinitionRoleDto TextEncoder);

public sealed record ImageGenerationBundleDefinitionDto(
    [property: JsonPropertyName("bundleId")] string BundleId,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("updatedAtUtc")] string? UpdatedAtUtc,
    [property: JsonPropertyName("roles")] BundleDefinitionRolesDto Roles,
    [property: JsonPropertyName("sampling")] BundleDefinitionSamplingDto Sampling);

public sealed record ImageGenerationBundleDefinitionListDto(
    IReadOnlyList<ImageGenerationBundleDefinitionDto> Items);

public sealed record ImageGenerationBundleDefinitionImportRequest(
    ImageGenerationBundleDefinitionDto Definition);
