using Microsoft.Data.SqlClient;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Settings;

public sealed class ApplicationSettingsConfigurationSource(
    string connectionString,
    ISettingsSectionRegistry registry,
    string contentRootPath,
    SettingsSecretsOptions settingsSecrets) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new ApplicationSettingsConfigurationProvider(connectionString, registry, contentRootPath, settingsSecrets);
    }
}

public sealed class ApplicationSettingsConfigurationProvider(
    string connectionString,
    ISettingsSectionRegistry registry,
    string contentRootPath,
    SettingsSecretsOptions settingsSecrets) : ConfigurationProvider
{
    private readonly string _connectionString = connectionString;
    private readonly ISettingsSectionRegistry _registry = registry;
    private readonly SettingsSecretsOptions _settingsSecrets = CloneSecretsOptions(settingsSecrets);
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _legacyProtector =
        ApplicationSettingsJson.CreateLegacyProtector(contentRootPath);

    public override void Load()
    {
        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        try
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();

            if (!ApplicationSettingsTableExists(connection))
            {
                throw new InvalidOperationException(
                    "Required table 'dbo.ApplicationSettings' was not found while loading DB-backed application settings.");
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT SectionName, JsonValue FROM ApplicationSettings";
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var sectionName = reader.GetString(0);
                var jsonValue = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                var jsonObject = ApplicationSettingsJson.DeserializeObject(jsonValue);

                if (_registry.TryGet(sectionName, out var definition))
                {
                    jsonObject = ApplicationSettingsJson.DecryptSecrets(
                        definition,
                        jsonObject,
                        _settingsSecrets,
                        _legacyProtector);
                    EnsureSecretsDecrypted(sectionName, definition, jsonObject);
                    FlattenSection(sectionName, jsonObject, definition);
                }
                else
                {
                    // Do not hydrate unknown DB sections into live runtime configuration.
                    // This prevents non-registry settings (for example legacy ServiceRouting rows)
                    // from silently overriding environment/appsettings values.
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load DB-backed application settings from '{DescribeConnectionTarget()}'. " +
                "Startup was aborted to avoid silently running with incomplete configuration.",
                ex);
        }
    }

    private string DescribeConnectionTarget()
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(_connectionString);
            var dataSource = string.IsNullOrWhiteSpace(builder.DataSource) ? "<unknown-server>" : builder.DataSource;
            var initialCatalog = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "<unknown-database>" : builder.InitialCatalog;
            return $"{dataSource}/{initialCatalog}";
        }
        catch
        {
            return "<unparseable-connection-string>";
        }
    }

    private static bool ApplicationSettingsTableExists(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID('dbo.ApplicationSettings', 'U') IS NULL THEN 0 ELSE 1 END";
        var result = command.ExecuteScalar();
        return result is int i && i == 1;
    }

    private void FlattenSection(string sectionName, JsonObject payload, SettingsSectionDefinition? definition)
    {
        foreach (var kvp in payload)
        {
            var propertyName = kvp.Key;
            var node = kvp.Value;
            if (node is null)
            {
                continue;
            }

            if (node is JsonObject nestedObject)
            {
                FlattenNested($"{sectionName}:{propertyName}", nestedObject);
                continue;
            }

            var value = ApplicationSettingsJson.NodeToString(node);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (definition != null)
            {
                var key = definition.GetCanonicalKey(propertyName);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    Data[key] = value;
                }
            }
            else
            {
                Data[$"{sectionName}:{propertyName}"] = value;
            }
        }
    }

    private void FlattenNested(string prefix, JsonObject payload)
    {
        foreach (var kvp in payload)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            if (kvp.Value is JsonObject childObject)
            {
                FlattenNested($"{prefix}:{kvp.Key}", childObject);
                continue;
            }

            var value = ApplicationSettingsJson.NodeToString(kvp.Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                Data[$"{prefix}:{kvp.Key}"] = value;
            }
        }
    }

    private static void EnsureSecretsDecrypted(string sectionName, SettingsSectionDefinition definition, JsonObject payload)
    {
        foreach (var secret in definition.Properties.Where(p => p.IsSecret))
        {
            if (!payload.TryGetPropertyValue(secret.Name, out var node) || node is null)
            {
                continue;
            }

            var value = ApplicationSettingsJson.NodeToString(node);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (ApplicationSettingsJson.IsEncryptedSecretValue(value))
            {
                throw new InvalidOperationException(
                    $"Failed to decrypt secret '{sectionName}:{secret.Name}'. " +
                    "The DB value is still encrypted (enc::... or encv2::...). Ensure SettingsSecrets keys and legacy key ring are configured correctly.");
            }
        }
    }

    private static SettingsSecretsOptions CloneSecretsOptions(SettingsSecretsOptions source)
    {
        return new SettingsSecretsOptions
        {
            ActiveKeyId = source.ActiveKeyId,
            Keys = source.Keys.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }
}
