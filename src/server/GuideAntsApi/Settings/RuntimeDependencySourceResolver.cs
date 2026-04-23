using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace GuideAntsApi.Settings;

/// <summary>
/// Phase E (R-5.7): Resolves the origin of a runtime-owned configuration key
/// so the Infrastructure tab can show operators where a value came from
/// (appsettings file, environment variable, docker-compose secret, user-secrets,
/// or unknown).
///
/// Walks providers in <i>reverse</i> (last-wins, matching ASP.NET Core override
/// semantics) and returns the first provider that owns the key. Classification
/// is by provider type + path inspection — the path-sniff is the stable surface
/// because ASP.NET Core exposes <c>FileConfigurationSource.Path</c> publicly.
/// </summary>
public static class RuntimeDependencySourceResolver
{
    public const string SourceAppSettings = "appsettings";
    public const string SourceEnvironment = "environment";
    public const string SourceCompose = "compose";
    public const string SourceUserSecrets = "user-secrets";
    /// <summary>DB-backed values from <see cref="ApplicationSettingsConfigurationProvider"/>.</summary>
    public const string SourceApplicationSettings = "application-settings";
    public const string SourceUnknown = "unknown";

    /// <summary>
    /// Returns the <c>source</c> label for <paramref name="key"/> or <c>"unknown"</c>
    /// if no provider claims the key, or if the claiming provider is of a type
    /// we don't map explicitly.
    /// </summary>
    public static string Resolve(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (configuration is not IConfigurationRoot root)
        {
            return SourceUnknown;
        }

        // Walk in reverse so the final override wins (matches Get<> semantics).
        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out _))
            {
                continue;
            }

            return Classify(provider);
        }

        return SourceUnknown;
    }

    private static string Classify(IConfigurationProvider provider)
    {
        switch (provider)
        {
            case ApplicationSettingsConfigurationProvider:
                return SourceApplicationSettings;

            case EnvironmentVariablesConfigurationProvider:
                return SourceEnvironment;

            case JsonConfigurationProvider jsonProvider:
            {
                var path = jsonProvider.Source?.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return SourceUnknown;
                }

                if (IsUserSecretsPath(path))
                {
                    return SourceUserSecrets;
                }

                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return SourceAppSettings;
                }

                return SourceUnknown;
            }
        }

        // Some provider types (KeyPerFile, UserSecrets) ship in packages we don't
        // reference directly. Match by type FullName so we stay robust without
        // forcing a package dependency.
        var typeName = provider.GetType().FullName ?? string.Empty;
        return typeName switch
        {
            "Microsoft.Extensions.Configuration.KeyPerFile.KeyPerFileConfigurationProvider"
                => SourceCompose,
            "Microsoft.Extensions.Configuration.UserSecrets.UserSecretsConfigurationProvider"
                => SourceUserSecrets,
            _ => SourceUnknown
        };
    }

    private static bool IsUserSecretsPath(string path)
    {
        if (path.Contains("Microsoft\\UserSecrets", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.Contains(".microsoft/usersecrets", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
