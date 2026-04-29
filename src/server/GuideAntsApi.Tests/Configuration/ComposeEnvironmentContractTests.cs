using System.Text.Json;
using FluentAssertions;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public sealed class ComposeEnvironmentContractTests
{
    private static readonly string[] ComposeStackFiles =
    [
        "docker-compose.cpu.yml",
        "docker-compose.cuda.yml",
        "docker-compose.ghcr-cpu.yml",
        "docker-compose.ghcr-cuda13.yml"
    ];

    [TestMethod]
    public void GuideantsWebApiUi_EnvironmentKeys_MapToAppSettingsOrRuntime_AcrossComposeStacks()
    {
        var repoRoot = FindRepositoryRoot();
        var appsettingsPath = Path.Combine(repoRoot, "src", "server", "GuideAntsApi", "appsettings.json");
        var appsettingsDevelopmentPath = Path.Combine(repoRoot, "src", "server", "GuideAntsApi", "appsettings.Development.json");

        File.Exists(appsettingsPath).Should().BeTrue($"appsettings file should exist at {appsettingsPath}");
        File.Exists(appsettingsDevelopmentPath).Should().BeTrue($"appsettings development file should exist at {appsettingsDevelopmentPath}");

        var appsettingsKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectAppsettingsKeys(appsettingsPath, appsettingsKeys);
        CollectAppsettingsKeys(appsettingsDevelopmentPath, appsettingsKeys);

        foreach (var composeFile in ComposeStackFiles)
        {
            var composePath = Path.Combine(repoRoot, "docker", composeFile);
            File.Exists(composePath).Should().BeTrue($"compose file should exist at {composePath}");

            var composeEnvironmentKeys = ReadComposeEnvironmentKeys(composePath, "guideants-webapi-ui");
            composeEnvironmentKeys.Should().NotBeEmpty($"service guideants-webapi-ui should define environment keys in {composeFile}");

            var unknownKeys = composeEnvironmentKeys
                .Where(key => !IsAllowedRuntimeKey(key))
                .Where(key =>
                {
                    var mapped = key.Replace("__", ":");
                    return !appsettingsKeys.Contains(mapped);
                })
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            unknownKeys.Should().BeEmpty(
                $"all compose keys for guideants-webapi-ui in {composeFile} must map to appsettings-backed keys. Unknown keys: {string.Join(", ", unknownKeys)}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var dockerDirectory = Path.Combine(current.FullName, "docker");
            var hasAnyKnownComposeFile = ComposeStackFiles.Any(name => File.Exists(Path.Combine(dockerDirectory, name)));
            if (hasAnyKnownComposeFile)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        var processDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (processDirectory != null)
        {
            var dockerDirectory = Path.Combine(processDirectory.FullName, "docker");
            var hasAnyKnownComposeFile = ComposeStackFiles.Any(name => File.Exists(Path.Combine(dockerDirectory, name)));
            if (hasAnyKnownComposeFile)
            {
                return processDirectory.FullName;
            }

            processDirectory = processDirectory.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test execution directory or process working directory.");
    }

    private static HashSet<string> ReadComposeEnvironmentKeys(string composePath, string serviceName)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(composePath);

        var inService = false;
        var inEnvironment = false;
        var serviceHeader = $"  {serviceName}:";

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (!inService)
            {
                if (line.Equals(serviceHeader, StringComparison.Ordinal))
                {
                    inService = true;
                }

                continue;
            }

            if (indent <= 2 && trimmed.EndsWith(':') && !trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                break;
            }

            if (!inEnvironment)
            {
                if (indent == 4 && trimmed.Equals("environment:", StringComparison.Ordinal))
                {
                    inEnvironment = true;
                }

                continue;
            }

            if (indent <= 4)
            {
                inEnvironment = false;
                continue;
            }

            if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            var entry = trimmed[1..].Trim();
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = entry[..separatorIndex].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static void CollectAppsettingsKeys(string appsettingsPath, HashSet<string> keys)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        FlattenJson(document.RootElement, string.Empty, keys);
    }

    private static void FlattenJson(JsonElement element, string prefix, HashSet<string> keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                keys.Add(prefix);
            }

            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var nextPrefix = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}:{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenJson(property.Value, nextPrefix, keys);
            }
            else
            {
                keys.Add(nextPrefix);
            }
        }
    }

    private static bool IsAllowedRuntimeKey(string key)
    {
        if (key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (key.Equals("API_RUNTIME_CONTEXT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Runtime code reads through IHuggingFaceTokenResolver, which
        // reads HuggingFace:Token from IConfiguration (DB-backed via
        // ApplicationSettingsConfigurationProvider). HF_TOKEN is also used
        // by the docker/llama/run/download-*.ps1 shell scripts that talk
        // directly to the HuggingFace CLI outside the app.
        if (key.Equals("HF_TOKEN", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (key.StartsWith("ServiceRouting__Containers__", StringComparison.OrdinalIgnoreCase)
            && key.EndsWith("__BaseUrl", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
