using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Settings;

public static class ImageGenerationBundleDefinitionContracts
{
    public const string SettingsSectionName = "ImageGenerationBundles";
    public const string BundlesPayloadProperty = "Bundles";
    public const string DefaultsRelativePath = "Settings/Defaults/ImageGeneration/Bundles";

    private static readonly IReadOnlyDictionary<string, string> LegacyBundleIdRenames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["flux2-klein-4b-q4ks"] = "flux2-klein-4b",
            ["flux2-klein-9b-q5"] = "flux2-klein-9b",
            ["FLUX.2-dev-GGUF-Q5_K_M"] = "FLUX.2-dev",
        };

    public static string NormalizeBundleId(string bundleId)
    {
        var trimmed = bundleId.Trim();
        return LegacyBundleIdRenames.TryGetValue(trimmed, out var renamed) ? renamed : trimmed;
    }

    public static IReadOnlyDictionary<string, string> LegacyRenames => LegacyBundleIdRenames;
}

public static class BundleDefinitionValidator
{
    public static IReadOnlyList<string> Validate(ImageGenerationBundleDefinitionDto? definition)
    {
        var errors = new List<string>();
        if (definition is null)
        {
            errors.Add("definition is required.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(definition.BundleId))
        {
            errors.Add("bundleId is required.");
        }

        ValidateRole(definition.Roles?.Diffusion, "roles.diffusion", errors);
        ValidateRole(definition.Roles?.Vae, "roles.vae", errors);
        ValidateRole(definition.Roles?.TextEncoder, "roles.textEncoder", errors);

        if (definition.Sampling is null)
        {
            errors.Add("sampling is required.");
        }
        else
        {
            if (definition.Sampling.Steps <= 0)
            {
                errors.Add("sampling.steps must be a positive integer.");
            }

            if (definition.Sampling.CfgScale <= 0)
            {
                errors.Add("sampling.cfgScale must be a positive number.");
            }

            if (string.IsNullOrWhiteSpace(definition.Sampling.SamplingMethod))
            {
                errors.Add("sampling.samplingMethod is required.");
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateJson(JsonElement payload)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<ImageGenerationBundleDefinitionDto>(
                payload.GetRawText(),
                BundleDefinitionJson.Options);
            return Validate(definition);
        }
        catch (JsonException ex)
        {
            return [$"Invalid bundle-definition JSON: {ex.Message}"];
        }
    }

    private static void ValidateRole(BundleDefinitionRoleDto? role, string label, List<string> errors)
    {
        if (role is null)
        {
            errors.Add($"{label} is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(role.Repo))
        {
            errors.Add($"{label}.repo is required.");
        }

        if (string.IsNullOrWhiteSpace(role.File))
        {
            errors.Add($"{label}.file is required.");
        }
        else if (role.File.Contains('*') || role.File.Contains('?'))
        {
            errors.Add($"{label}.file must be a single filename (no '*' or '?' glob metacharacters).");
        }
    }
}

public static class BundleDefinitionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ImageGenerationBundleDefinitionDto? Deserialize(JsonElement payload)
    {
        return JsonSerializer.Deserialize<ImageGenerationBundleDefinitionDto>(payload.GetRawText(), Options);
    }

    public static ImageGenerationBundleDefinitionDto? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<ImageGenerationBundleDefinitionDto>(json, Options);
    }

    public static JsonObject ToJsonObject(ImageGenerationBundleDefinitionDto definition)
    {
        var node = JsonSerializer.SerializeToNode(definition, Options);
        return node as JsonObject ?? new JsonObject();
    }
}
