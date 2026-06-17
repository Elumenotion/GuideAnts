using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.Mcp;

public sealed class ClaudeSkillPackService : IClaudeSkillPackService
{
    private const string ApiKeyPlaceholder = "gak_REPLACE_ME";
    private const string TemplatePrefix = "GuideAntsApi.Templates.ClaudeSkill.";

    private static readonly Assembly Assembly = typeof(ClaudeSkillPackService).Assembly;

    public Task<ClaudeSkillPackResult> BuildAsync(
        ClaudeSkillPackBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var skillDirectoryName = BuildSkillDirectoryName(request.FriendlyName, request.GuideName);
        var skillName = skillDirectoryName;
        var primaryTool = request.Assistants.FirstOrDefault(a => a.IsGuide)
                          ?? request.Assistants.FirstOrDefault();
        var primaryToolName = primaryTool?.ToolName ?? "assistant";
        var skillDescription = BuildSkillDescription(request.McpDescription, request.GuideName, skillName);
        var mcpEndpointUrl = $"{request.ApiBaseUrl.TrimEnd('/')}/published/mcp?pubId={request.PubId}";
        var toolTable = BuildToolReferenceTable(request.Assistants);

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SkillName"] = skillName,
            ["SkillDirectoryName"] = skillDirectoryName,
            ["SkillDescription"] = skillDescription,
            ["GuideName"] = request.GuideName,
            ["PubId"] = request.PubId.ToString(),
            ["ApiBaseUrl"] = request.ApiBaseUrl.TrimEnd('/'),
            ["McpEndpointUrl"] = mcpEndpointUrl,
            ["PrimaryToolName"] = primaryToolName,
            ["ToolReferenceTable"] = toolTable,
            ["ApiKeyPlaceholder"] = ApiKeyPlaceholder,
        };

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            var root = skillDirectoryName + "/";

            WriteTextEntry(archive, root + "SKILL.md", LoadTemplate("SKILL.md.template", tokens));
            WriteTextEntry(archive, root + "reference.md", LoadTemplate("reference.md.template", tokens));
            WriteTextEntry(archive, root + "README.md", LoadTemplate("README.md.template", tokens));
            WriteTextEntry(archive, root + ".env", LoadTemplate("env.template", tokens));
            WriteTextEntry(archive, root + ".env.example", LoadTemplate("env.example.template", tokens));

            WriteBinaryEntry(archive, root + "scripts/guideants_mcp.py", LoadScript("scripts/guideants_mcp.py"));
        }

        var fileName = $"{skillDirectoryName}-claude-skill.zip";
        return Task.FromResult(new ClaudeSkillPackResult(memoryStream.ToArray(), fileName, skillDirectoryName));
    }

    internal static string BuildSkillDirectoryName(string? friendlyName, string guideName)
    {
        var source = string.IsNullOrWhiteSpace(friendlyName) ? guideName : friendlyName.Trim();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sanitized = McpAssistantToolNaming.SanitizeOperationId(source, used);
        return sanitized.Replace('_', '-').ToLowerInvariant();
    }

    internal static string BuildSkillDescription(string? mcpDescription, string guideName, string skillName)
    {
        if (!string.IsNullOrWhiteSpace(mcpDescription))
        {
            return mcpDescription.Trim();
        }

        return $"Consults the GuideAnts published guide \"{guideName}\" via its MCP endpoint for " +
               $"guidance and content this guide specializes in. Use when the user asks for help from " +
               $"\"{guideName}\", mentions it by name, or runs /{skillName}.";
    }

    internal static string BuildToolReferenceTable(IReadOnlyList<McpAddressableAssistant> assistants)
    {
        if (assistants.Count == 0)
        {
            return "_No assistants configured._";
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Tool | Assistant | Role | Description |");
        sb.AppendLine("|------|-----------|------|-------------|");

        foreach (var assistant in assistants)
        {
            var role = assistant.IsGuide ? "Guide" : "Crew";
            var desc = EscapeMarkdownCell(assistant.Description);
            sb.AppendLine($"| `{assistant.ToolName}` | {EscapeMarkdownCell(assistant.Name)} | {role} | {desc} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string EscapeMarkdownCell(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace('|', '\\').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string LoadTemplate(string fileName, IReadOnlyDictionary<string, string> tokens)
    {
        var content = LoadEmbeddedText(TemplatePrefix + fileName.Replace('/', '.'));
        return ReplaceTokens(content, tokens);
    }

    private static byte[] LoadScript(string relativePath)
    {
        var resourceName = TemplatePrefix + relativePath.Replace('/', '.');
        return LoadEmbeddedBytes(resourceName);
    }

    private static string ReplaceTokens(string content, IReadOnlyDictionary<string, string> tokens)
    {
        var result = content;
        foreach (var (key, value) in tokens)
        {
            result = result.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }

        if (Regex.IsMatch(result, @"\{\{[A-Za-z0-9_]+\}\}"))
        {
            throw new InvalidOperationException("Unresolved template token in Claude skill pack.");
        }

        return result;
    }

    private static string LoadEmbeddedText(string resourceName)
    {
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] LoadEmbeddedBytes(string resourceName)
    {
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteTextEntry(ZipArchive archive, string entryPath, string content)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string entryPath, byte[] content)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
