using System.Text.Json;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Derives scoped sandbox admin artifacts from sandbox_subprocess MCP package metadata (design §3.4).
/// </summary>
public static class McpSandboxSetupComposer
{
    public sealed record StagingArtifacts(
        string RequirementsText,
        string AptPackagesText,
        string InstallScriptsJson);

    public static StagingArtifacts Compose(IReadOnlyList<McpPackageDescriptorDto> packages)
    {
        var requirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scripts = new List<AdminInstallScriptDraft>();
        var needsNode = false;
        var order = 1;

        foreach (var package in packages)
        {
            var registryType = package.RegistryType?.Trim().ToLowerInvariant() ?? string.Empty;
            var identifier = package.Identifier?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            switch (registryType)
            {
                case "pypi":
                case "python":
                    requirements.Add(identifier);
                    break;

                case "npm":
                case "node":
                    needsNode = true;
                    scripts.Add(new AdminInstallScriptDraft(
                        Id: $"mcp-npm-{SanitizeScriptId(identifier)}",
                        Order: order++,
                        Name: $"Install MCP npm package {identifier}",
                        ScriptType: "Bash",
                        Script: $"npm install -g {ShellQuote(identifier)}"));
                    break;

                default:
                    if (string.Equals(package.Command, "npx", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(package.Command, "npm", StringComparison.OrdinalIgnoreCase))
                    {
                        needsNode = true;
                    }

                    var args = package.Args is { Count: > 0 }
                        ? string.Join(' ', package.Args.Select(ShellQuote))
                        : string.Empty;
                    var commandLine = string.IsNullOrWhiteSpace(args)
                        ? ShellQuote(package.Command)
                        : $"{ShellQuote(package.Command)} {args}";
                    scripts.Add(new AdminInstallScriptDraft(
                        Id: $"mcp-pkg-{SanitizeScriptId(identifier)}",
                        Order: order++,
                        Name: $"Prepare MCP package {identifier}",
                        ScriptType: "Bash",
                        Script: commandLine));
                    break;
            }
        }

        var aptPackages = needsNode ? "nodejs\n" : string.Empty;
        var requirementsText = requirements.Count == 0
            ? string.Empty
            : string.Join('\n', requirements.OrderBy(static line => line, StringComparer.OrdinalIgnoreCase)) + '\n';

        var installScripts = new
        {
            version = 1,
            scripts = scripts.Select(static script => new
            {
                id = script.Id,
                order = script.Order,
                name = script.Name,
                scriptType = script.ScriptType,
                script = script.Script,
            }),
        };

        return new StagingArtifacts(
            requirementsText,
            aptPackages,
            JsonSerializer.Serialize(installScripts, JsonSerializerOptions));
    }

    public static bool TryCollectSandboxPackages(
        IEnumerable<string> openApiSpecJsonList,
        out List<McpPackageDescriptorDto> packages)
    {
        packages = new List<McpPackageDescriptorDto>();
        foreach (var spec in openApiSpecJsonList)
        {
            var connection = McpSandboxConnectionReader.TryReadConnection(spec);
            if (connection is not null)
            {
                packages.Add(connection.Package);
            }
        }

        return packages.Count > 0;
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private sealed record AdminInstallScriptDraft(
        string Id,
        int Order,
        string Name,
        string ScriptType,
        string Script);

    private static string SanitizeScriptId(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "pkg" : sanitized.ToLowerInvariant();
    }

    private static string ShellQuote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        if (value.All(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/' or '@'))
        {
            return value;
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
