using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GuideAntsApi.Models;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.EnvironmentVariables;

public static class EnvironmentVariableConfigSerializer
{
    public const string MaskedSecretValue = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    private const int MaxEntries = 128;
    private const int MaxValueLength = 64 * 1024;
    private static readonly SettingsSectionDefinition SecretValueDefinition = new()
    {
        SectionName = "ScriptExecutionEnvironment",
        Properties = new[]
        {
            new SettingsPropertyDefinition("value", "ScriptExecutionEnvironment:value", IsSecret: true)
        }
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex EnvironmentVariableNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCRIPT_EXECUTION_SCOPE_ROOT",
        "SCRIPT_EXECUTION_SCOPE_PROJECT_ID",
        "SCRIPT_EXECUTION_SCOPE_GUIDE_ID",
        "SCRIPT_EXECUTION_SCOPE_CREDENTIALS_FILE",
        "PATH",
        "HOME",
        "USER",
        "USERNAME",
        "SHELL",
        "LD_PRELOAD",
        "LD_LIBRARY_PATH",
        "DYLD_INSERT_LIBRARIES",
        "PYTHONPATH",
        "PYTHONHOME",
        "BASH_ENV",
        "ENV",
        "PROMPT_COMMAND",
        "PSModulePath",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT",
        "SCRIPT_EXECUTION_AGENT_TOKEN",
        "SCRIPT_EXECUTION_ADMIN_TOKEN"
    };

    public static List<EnvironmentVariableDto> DeserializeForClient(string? json)
    {
        return DeserializeManifest(json)
            .Select(entry => new EnvironmentVariableDto(
                entry.Name,
                entry.IsSecret && !string.IsNullOrEmpty(entry.Value) ? MaskedSecretValue : entry.Value,
                entry.IsSecret))
            .ToList();
    }

    public static IReadOnlyDictionary<string, string> DeserializeForExecution(
        SettingsSecretsOptions settingsSecretsOptions,
        params string?[] manifests)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestJson in manifests)
        {
            foreach (var entry in DeserializeManifest(manifestJson))
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value is null)
                {
                    continue;
                }

                var value = entry.IsSecret
                    ? DecryptSecretValue(entry.Value, settingsSecretsOptions)
                    : entry.Value;

                if (value is not null)
                {
                    environment[entry.Name] = value;
                }
            }
        }

        return environment;
    }

    public static string? SerializeFromClient(
        IReadOnlyCollection<EnvironmentVariableDto>? variables,
        string? existingJson,
        SettingsSecretsOptions settingsSecretsOptions)
    {
        if (variables is null || variables.Count == 0)
        {
            return null;
        }

        var existingByName = DeserializeManifest(existingJson)
            .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<EnvironmentVariableEntry>();

        foreach (var variable in variables)
        {
            var name = (variable.Name ?? string.Empty).Trim();
            ValidateName(name);

            if (!names.Add(name))
            {
                throw new ArgumentException($"Environment variable '{name}' is duplicated.");
            }

            var value = variable.Value ?? string.Empty;
            if (variable.IsSecret && value == MaskedSecretValue)
            {
                value = existingByName.TryGetValue(name, out var existing) && existing.IsSecret
                    ? existing.Value ?? string.Empty
                    : string.Empty;
            }

            if (value.Length > MaxValueLength)
            {
                throw new ArgumentException($"Environment variable '{name}' exceeds maximum value length.");
            }

            var persistedValue = variable.IsSecret && value.Length > 0
                ? EncryptSecretValue(value, settingsSecretsOptions)
                : value;

            entries.Add(new EnvironmentVariableEntry
            {
                Name = name,
                Value = persistedValue,
                IsSecret = variable.IsSecret
            });
        }

        if (entries.Count > MaxEntries)
        {
            throw new ArgumentException($"Environment configuration cannot contain more than {MaxEntries} variables.");
        }

        return JsonSerializer.Serialize(new EnvironmentVariableManifest { Variables = entries }, JsonOptions);
    }

    private static List<EnvironmentVariableEntry> DeserializeManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<EnvironmentVariableManifest>(json, JsonOptions)?.Variables ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string EncryptSecretValue(string plaintext, SettingsSecretsOptions settingsSecretsOptions)
    {
        var payload = new JsonObject
        {
            ["value"] = JsonValue.Create(plaintext)
        };

        var encryptedPayload = ApplicationSettingsJson.EncryptSecrets(
            SecretValueDefinition,
            payload,
            settingsSecretsOptions);

        return ApplicationSettingsJson.NodeToString(encryptedPayload["value"]);
    }

    private static string? DecryptSecretValue(string persistedValue, SettingsSecretsOptions settingsSecretsOptions)
    {
        if (!ApplicationSettingsJson.IsEncryptedSecretValue(persistedValue))
        {
            // Backward compatibility for any pre-encryption rows produced locally before this hardening pass.
            return persistedValue;
        }

        var payload = new JsonObject
        {
            ["value"] = JsonValue.Create(persistedValue)
        };

        var decryptedPayload = ApplicationSettingsJson.DecryptSecrets(
            SecretValueDefinition,
            payload,
            settingsSecretsOptions);

        var value = ApplicationSettingsJson.NodeToString(decryptedPayload["value"]);
        return ApplicationSettingsJson.IsEncryptedSecretValue(value) ? null : value;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !EnvironmentVariableNamePattern.IsMatch(name))
        {
            throw new ArgumentException("Environment variable name must be valid.");
        }

        if (ReservedEnvironmentKeys.Contains(name)
            || name.StartsWith("SCRIPT_EXECUTION_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GUIDEANTS_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Environment variable '{name}' is reserved by ScriptExecutionAgent.");
        }
    }

    private sealed class EnvironmentVariableManifest
    {
        public List<EnvironmentVariableEntry> Variables { get; set; } = [];
    }

    private sealed class EnvironmentVariableEntry
    {
        public string Name { get; set; } = string.Empty;
        public string? Value { get; set; }
        public bool IsSecret { get; set; }
    }
}
