using System.Text.RegularExpressions;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public static class RouterPresetValidator
{
    private static readonly HashSet<string> InfrastructureKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "mmproj",
        "version",
    };

    private static readonly HashSet<string> FleetScopedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "models-preset",
        "models-max",
        "no-models-autoload",
        "no-autoload",
        "threads",
        "parallel",
        "n-gpu-layers",
        "gpu-layers",
        "kv-offload",
        "no-kv-offload",
        "kv-unified",
        "jinja",
        "cont-batching",
        "no-mmap",
        "flash-attn",
        "cache-type-k",
        "cache-type-v",
        "tensor-split",
        "cuda-visible-devices",
    };

    private static readonly Regex ControlCharRegex = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", RegexOptions.Compiled);
    private static readonly Regex ShellFragmentRegex = new(@"[;&|`$<>]|\$\(|\$\{", RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, string> ValidateAndNormalize(
        IReadOnlyDictionary<string, string>? preset)
    {
        if (preset is null || preset.Count == 0)
        {
            throw new AddModelException(
                CuratedInstallErrorCodes.PresetInvalid,
                step: "validation",
                message: "Resolved router preset is required.",
                remediation: "Choose a curated definition with a valid preset.");
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenLower = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, rawValue) in preset)
        {
            var key = rawKey.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: "Preset keys cannot be blank.",
                    remediation: "Fix the curated definition preset.");
            }

            if (ControlCharRegex.IsMatch(key))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset key '{key}' contains control characters.",
                    remediation: "Fix the curated definition preset.");
            }

            if (InfrastructureKeys.Contains(key))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset cannot include infrastructure key '{key}'.",
                    remediation: "Fix the curated definition preset.");
            }

            if (FleetScopedKeys.Contains(key))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset key '{key}' is fleet-scoped. Use the Fleet llama server editor.",
                    remediation: "Remove fleet-scoped keys from the alias preset.");
            }

            if (seenLower.TryGetValue(key, out var prior) && !string.Equals(prior, key, StringComparison.Ordinal))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset contains duplicate keys under case normalization: '{prior}' and '{key}'.",
                    remediation: "Fix the curated definition preset.");
            }

            seenLower[key] = key;

            if (rawValue is null)
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset value for '{key}' must be a string.",
                    remediation: "Fix the curated definition preset.");
            }

            var value = rawValue.Trim();
            if (ControlCharRegex.IsMatch(value) || value.Contains('\n') || value.Contains('\r'))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset value for '{key}' contains control characters or newlines.",
                    remediation: "Fix the curated definition preset.");
            }

            if (ShellFragmentRegex.IsMatch(value))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset value for '{key}' contains shell metacharacters.",
                    remediation: "Fix the curated definition preset.");
            }

            normalized[key] = value;
        }

        return normalized;
    }
}
