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

    /// <summary>
    /// Router shell bootstrap keys belong on the llama-server process CLI
    /// (<c>start-llama.sh</c>), not in per-alias <c>router-models.ini</c> presets.
    /// </summary>
    private static readonly HashSet<string> RouterShellKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "models-preset",
        "models-max",
        "no-models-autoload",
        "no-autoload",
    };

    /// <summary>
    /// Process/env-owned knobs (<c>GA_LLAMA_*</c> / <c>start-llama.sh</c> router base preset).
    /// These apply to every child via CLI and must not be authored on alias presets.
    /// </summary>
    private static readonly HashSet<string> ProcessScopedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "n-gpu-layers",
        "no-mmap",
        "threads",
        "parallel",
        "kv-unified",
        "kv-offload",
        "no-kv-offload",
        "jinja",
        "cont-batching",
        "flash-attn",
        "cache-type-k",
        "cache-type-v",
        "tensor-split",
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

            if (RouterShellKeys.Contains(key))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset key '{key}' is router-shell infrastructure and cannot be set on a model alias.",
                    remediation: "Remove router-shell keys from the alias preset.");
            }

            if (ProcessScopedKeys.Contains(key))
            {
                throw new AddModelException(
                    CuratedInstallErrorCodes.PresetInvalid,
                    step: "validation",
                    message: $"Preset key '{key}' is process/env-owned (GA_LLAMA_* / start-llama.sh) and cannot be set on a model alias.",
                    remediation: "Set this value via container env / compose, not the per-model router preset.");
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
