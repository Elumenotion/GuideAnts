using System.Text.Json;
using FluentAssertions;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public sealed class ComposeEnvironmentContractTests
{
    private static readonly (string FileName, string ApiServiceName)[] ComposeStacks =
    [
        ("docker-compose.cpu.yml", "guideants-webapi-ui"),
        ("docker-compose.cuda.yml", "guideants-webapi-ui"),
        ("docker-compose.rocm.yml", "guideants-webapi-ui"),
        ("docker-compose.slim.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-cpu.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-cuda13.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-rocm.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-slim.yml", "guideants-webapi-ui"),
        ("docker-compose.mssql.yml", "guideants-webapi-ui-mssql")
    ];

    private static readonly string[] RequiredDocumentServerApiKeys =
    [
        "DocumentServer__Enabled",
        "DocumentServer__InternalUrl",
        "DocumentServer__ApiBaseUrl",
        "DocumentServer__JwtEnabled",
        "DocumentServer__JwtSecret",
        "DocumentServer__JwtHeader",
        "DocumentServer__JwtInBody"
    ];

    private static readonly (string FileName, string ApiServiceName)[] ScriptAgentComposeStacks =
    [
        ("docker-compose.cpu.yml", "guideants-webapi-ui"),
        ("docker-compose.cuda.yml", "guideants-webapi-ui"),
        ("docker-compose.rocm.yml", "guideants-webapi-ui"),
        ("docker-compose.slim.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-cpu.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-cuda13.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-rocm.yml", "guideants-webapi-ui"),
        ("docker-compose.ghcr-slim.yml", "guideants-webapi-ui"),
        ("docker-compose.cuda.api-only-local-build.yml", "guideants-webapi-ui")
    ];

    private static readonly string[] RequiredDocumentServerContainerKeys =
    [
        "JWT_ENABLED",
        "JWT_SECRET",
        "JWT_HEADER",
        "JWT_IN_BODY",
        "ALLOW_PRIVATE_IP_ADDRESS"
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

        foreach (var (composeFile, apiServiceName) in ComposeStacks)
        {
            var composePath = Path.Combine(repoRoot, "docker", composeFile);
            File.Exists(composePath).Should().BeTrue($"compose file should exist at {composePath}");

            var composeEnvironmentKeys = ReadComposeEnvironmentKeys(composePath, apiServiceName);
            composeEnvironmentKeys.Should().NotBeEmpty($"service {apiServiceName} should define environment keys in {composeFile}");

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
                $"all compose keys for {apiServiceName} in {composeFile} must map to appsettings-backed keys. Unknown keys: {string.Join(", ", unknownKeys)}");
        }
    }

    [TestMethod]
    public void DocumentServer_EnvContract_IsPresentAcrossAllComposeStacks()
    {
        var repoRoot = FindRepositoryRoot();

        foreach (var (composeFile, apiServiceName) in ComposeStacks)
        {
            var composePath = Path.Combine(repoRoot, "docker", composeFile);
            File.Exists(composePath).Should().BeTrue($"compose file should exist at {composePath}");

            var apiKeys = ReadComposeEnvironmentKeys(composePath, apiServiceName);
            foreach (var key in RequiredDocumentServerApiKeys)
            {
                apiKeys.Should().Contain(key, $"{composeFile} must include {key} for DocumentServer API wiring");
            }

            var documentServerKeys = ReadComposeEnvironmentKeys(composePath, "documentserver");
            foreach (var key in RequiredDocumentServerContainerKeys)
            {
                documentServerKeys.Should().Contain(key, $"{composeFile} must include {key} for DocumentServer container wiring");
            }
        }
    }

    [TestMethod]
    public void DockerEnv_DocumentServerBooleanValues_AreParseable()
    {
        var repoRoot = FindRepositoryRoot();
        var envPath = Path.Combine(repoRoot, "docker", ".env");
        File.Exists(envPath).Should().BeTrue($"docker env file should exist at {envPath}");

        var values = ReadEnvValues(envPath);

        foreach (var key in new[] { "GA_DOCUMENTSERVER_ENABLED", "GA_DOCUMENTSERVER_JWT_ENABLED", "GA_DOCUMENTSERVER_JWT_IN_BODY" })
        {
            values.Should().ContainKey(key, $"docker/.env must define {key}");
            bool.TryParse(values[key], out _).Should().BeTrue($"{key} must be a plain boolean without inline comment text");
        }
    }

    [TestMethod]
    public void DockerEnv_DoesNotUseInlineCommentsInValues()
    {
        var repoRoot = FindRepositoryRoot();
        var envPath = Path.Combine(repoRoot, "docker", ".env");
        File.Exists(envPath).Should().BeTrue($"docker env file should exist at {envPath}");

        var offenders = File.ReadAllLines(envPath)
            .Select((rawLine, index) => new { LineNumber = index + 1, Line = rawLine.Trim() })
            .Where(item => !string.IsNullOrWhiteSpace(item.Line))
            .Where(item => !item.Line.StartsWith('#'))
            .Where(item => item.Line.Contains('=') && item.Line.Contains('#'))
            .Select(item => $"{item.LineNumber}: {item.Line}")
            .ToList();

        offenders.Should().BeEmpty(
            "docker/.env values are passed literally by Docker Compose; put comments on their own lines. Offenders: {0}",
            string.Join(Environment.NewLine, offenders));
    }

    [TestMethod]
    public void DockerBuildScripts_DoNotReconstructEnvByStrippingWhitespace()
    {
        var repoRoot = FindRepositoryRoot();
        var buildScripts = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "docker", "build"), "build_*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            .ToList();

        buildScripts.Should().NotBeEmpty("docker build scripts should exist");

        var forbiddenPatterns = new[]
        {
            "compactRaw",
            "-replace '\\s'",
            "-replace \"\\s\"",
            "declare -A ENV_MAP",
            "ENV_ORDER"
        };

        var offenders = buildScripts
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return forbiddenPatterns
                    .Where(pattern => text.Contains(pattern, StringComparison.Ordinal))
                    .Select(pattern => $"{Path.GetRelativePath(repoRoot, path)} contains {pattern}");
            })
            .ToList();

        offenders.Should().BeEmpty(
            "build scripts must update docker/.env line-by-line; reconstructing it can collapse comments/newlines into values. Offenders: {0}",
            string.Join(Environment.NewLine, offenders));
    }

    [TestMethod]
    public void ScriptExecutionAgentTokenContract_IsPresentAcrossSandboxStacks()
    {
        var repoRoot = FindRepositoryRoot();

        foreach (var (composeFile, apiServiceName) in ScriptAgentComposeStacks)
        {
            var composePath = Path.Combine(repoRoot, "docker", composeFile);
            File.Exists(composePath).Should().BeTrue($"compose file should exist at {composePath}");

            var apiKeys = ReadComposeEnvironmentKeys(composePath, apiServiceName);
            apiKeys.Should().Contain("ScriptExecution__AgentToken", $"{composeFile} must provide API->agent shared token");
            apiKeys.Should().Contain("ScriptExecution__AdminToken", $"{composeFile} must provide API->agent admin token");

            var aiKeys = ReadComposeEnvironmentKeys(composePath, "guideants-ai");
            aiKeys.Should().Contain("SCRIPT_EXECUTION_AGENT_TOKEN", $"{composeFile} must provide script-agent shared token");
            aiKeys.Should().Contain("SCRIPT_EXECUTION_ADMIN_TOKEN", $"{composeFile} must provide script-agent admin token");
            aiKeys.Should().Contain("SCRIPT_EXECUTION_REQUIRE_TOKEN", $"{composeFile} must enforce script-agent token requirement");

            var plantumlKeys = ReadComposeEnvironmentKeys(composePath, "plantuml");
            plantumlKeys.Should().Contain("SCRIPT_EXECUTION_AGENT_TOKEN", $"{composeFile} must provide script-agent shared token for plantuml");
            plantumlKeys.Should().Contain("SCRIPT_EXECUTION_REQUIRE_TOKEN", $"{composeFile} must enforce script-agent token requirement for plantuml");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var dockerDirectory = Path.Combine(current.FullName, "docker");
            var hasAnyKnownComposeFile = ComposeStacks.Any(stack => File.Exists(Path.Combine(dockerDirectory, stack.FileName)));
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
            var hasAnyKnownComposeFile = ComposeStacks.Any(stack => File.Exists(Path.Combine(dockerDirectory, stack.FileName)));
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

            if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
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

                continue;
            }

            var mapSeparatorIndex = trimmed.IndexOf(':');
            if (mapSeparatorIndex <= 0)
            {
                continue;
            }

            var mapKey = trimmed[..mapSeparatorIndex].Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(mapKey))
            {
                keys.Add(mapKey);
            }
        }

        return keys;
    }

    private static void CollectAppsettingsKeys(string appsettingsPath, HashSet<string> keys)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        FlattenJson(document.RootElement, string.Empty, keys);
    }

    private static Dictionary<string, string> ReadEnvValues(string envPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = value;
        }

        return values;
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

        if (key.Equals("ACCEPT_EULA", StringComparison.OrdinalIgnoreCase)
            || key.Equals("MSSQL_DB_NAME", StringComparison.OrdinalIgnoreCase)
            || key.Equals("MSSQL_SA_PASSWORD", StringComparison.OrdinalIgnoreCase))
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
