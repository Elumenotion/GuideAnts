using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Resolves <c>{{secret:VAR}}</c> header templates at tool-call time.
/// </summary>
public static class McpSecretTemplateResolver
{
    private static readonly Regex SecretTemplatePattern = new(
        @"\{\{secret:([A-Za-z_][A-Za-z0-9_]*)\}\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static Dictionary<string, string> ResolveHeaders(
        IReadOnlyDictionary<string, string>? headerTemplates,
        IReadOnlyDictionary<string, string> executionEnvironment)
    {
        if (headerTemplates is null || headerTemplates.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, templateValue) in headerTemplates)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            resolved[key] = ResolveTemplateValue(templateValue, executionEnvironment);
        }

        return resolved;
    }

    public static string ResolveTemplateValue(
        string templateValue,
        IReadOnlyDictionary<string, string> executionEnvironment)
    {
        if (string.IsNullOrEmpty(templateValue))
        {
            return templateValue;
        }

        return SecretTemplatePattern.Replace(templateValue, match =>
        {
            var variableName = match.Groups[1].Value;
            if (!executionEnvironment.TryGetValue(variableName, out var secretValue)
                || string.IsNullOrEmpty(secretValue))
            {
                throw new InvalidOperationException(
                    $"MCP header template references unresolved secret '{variableName}'. " +
                    "Configure the variable in Guide Environment before using this tool.");
            }

            return secretValue;
        });
    }

    public static Dictionary<string, string> ResolveEnvironmentVariables(
        IReadOnlyList<Models.Guides.McpEnvironmentVariableRefDto>? environmentVariableRefs,
        IReadOnlyDictionary<string, string> executionEnvironment)
    {
        if (environmentVariableRefs is null || environmentVariableRefs.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variableRef in environmentVariableRefs)
        {
            if (string.IsNullOrWhiteSpace(variableRef.Name))
            {
                continue;
            }

            resolved[variableRef.Name] = ResolveTemplateValue(
                variableRef.SecretRef,
                executionEnvironment);
        }

        return resolved;
    }
}
