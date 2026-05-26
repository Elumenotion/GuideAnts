namespace GuideAntsApi.Configuration;

using GuideAntsApi.Settings;

/// <summary>
/// Startup-time configuration validator for cross-cutting shape constraints
/// (secrets rotation, UI root path, gateway prefixes). The Phase A-H refactor
/// retired this validator's per-service <c>ActiveProviderId</c> checks in
/// favor of <c>IRoutingReadinessService</c> + <c>IChatTargetValidator</c>,
/// which fail-fast at dispatch time with structured
/// <see cref="GuideAntsApi.Services.Routing.RoutingException"/>s instead of
/// startup crashes.
/// <para>
/// Per R-4.4 this class MUST NOT read any <c>{Service}:ActiveProviderId</c>
/// key. Provider-section readiness is now computed from
/// <c>ApplicationSettings.ServiceModes</c> at runtime.
/// </para>
/// </summary>
public static class ServiceRoutingStartupValidator
{
    public static IReadOnlyList<string> Evaluate(IConfiguration configuration)
    {
        var errors = new List<string>();

        ValidateSettingsSecrets(configuration, errors);
        ValidateNoEncryptedSecrets(configuration, errors);
        ValidateUiRootPath(configuration, errors);

        var llamaBaseUrl = configuration[ServiceRoutingContracts.LlamaBaseUrlKey];
        ValidateLlamaBaseUrl(llamaBaseUrl, errors);
        var requireExplicitServiceRouting = ShouldRequireExplicitServiceRouting(configuration);
        ValidateGuideantsAiBaseUrl(configuration, errors, requireExplicitServiceRouting);

        foreach (var containerSection in configuration.GetSection("ServiceRouting:Containers").GetChildren())
        {
            var containerName = containerSection.Key;
            var baseUrl = containerSection["BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                continue;
            }

            if (!TryParseHttpUri(baseUrl, out var uri, out var parseError))
            {
                errors.Add($"ServiceRouting:Containers:{containerName}:BaseUrl {parseError}");
                continue;
            }

            if (string.Equals(containerName, ServiceRoutingContracts.GuideantsAiContainerName, StringComparison.OrdinalIgnoreCase))
            {
                var normalizedPath = NormalizePath(uri);
                if (!string.Equals(normalizedPath, ServiceRoutingContracts.SandboxPath, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"ServiceRouting:Containers:{ServiceRoutingContracts.GuideantsAiContainerName}:BaseUrl must include the '{ServiceRoutingContracts.SandboxPath}' prefix for hard cutover routing.");
                }
            }
        }

        return errors;
    }

    public static void Validate(IConfiguration configuration)
    {
        var errors = Evaluate(configuration);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid service routing configuration:\n - " + string.Join("\n - ", errors));
        }
    }

    private static void ValidateSettingsSecrets(IConfiguration configuration, List<string> errors)
    {
        var options = configuration.GetSection(SettingsSecretsOptions.SectionName).Get<SettingsSecretsOptions>()
            ?? new SettingsSecretsOptions();
        var secretErrors = ApplicationSettingsJson.ValidateSettingsSecrets(options);
        errors.AddRange(secretErrors);
    }

    private static void ValidateNoEncryptedSecrets(IConfiguration configuration, List<string> errors)
    {
        var secretKeys = new[]
        {
            "AzureOpenAI:ApiKey",
            "AzureOpenAiImages:ApiKey",
            "AzureOpenAiEmbedding:ApiKey",
            "AzureSpeechService:ApiKey",
            "AzureDocumentIntelligence:ApiKey",
            "OpenAI:ApiKey",
            "Anthropic:ApiKey",
            "Anthropic:AuthToken",
            "GoogleGeminiApi:ApiKey",
            "HuggingFace:Token",
            "OpenRouter:ApiKey"
        };

        foreach (var key in secretKeys)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (ApplicationSettingsJson.IsEncryptedSecretValue(value))
            {
                errors.Add(
                    $"{key} is still encrypted (enc::... or encv2::...). " +
                    "DB secret decryption failed. Verify SettingsSecrets keys and legacy key ring.");
            }
        }
    }

    private static void ValidateUiRootPath(IConfiguration configuration, List<string> errors)
    {
        var uiRootPath = configuration["Ui:RootPath"];
        if (string.IsNullOrWhiteSpace(uiRootPath))
        {
            errors.Add("Ui:RootPath is required.");
        }
    }

    private static bool ShouldRequireExplicitServiceRouting(IConfiguration configuration)
    {
        var runtimeContext = configuration["API_RUNTIME_CONTEXT"];
        if (string.Equals(runtimeContext, "compose", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var envSuffix = configuration["CONTAINER_APP_ENV_DNS_SUFFIX"];
        return !string.IsNullOrWhiteSpace(envSuffix);
    }

    private static void ValidateGuideantsAiBaseUrl(
        IConfiguration configuration,
        List<string> errors,
        bool requireExplicitServiceRouting)
    {
        var baseUrl = configuration[ServiceRoutingContracts.GuideantsAiBaseUrlKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            if (requireExplicitServiceRouting)
            {
                errors.Add(
                    $"{ServiceRoutingContracts.GuideantsAiBaseUrlKey} is required and must include the '{ServiceRoutingContracts.SandboxPath}' prefix for hard cutover routing.");
            }
            return;
        }

        if (!TryParseHttpUri(baseUrl, out var uri, out var parseError))
        {
            errors.Add($"{ServiceRoutingContracts.GuideantsAiBaseUrlKey} {parseError}");
            return;
        }

        var normalizedPath = NormalizePath(uri);
        if (!string.Equals(normalizedPath, ServiceRoutingContracts.SandboxPath, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{ServiceRoutingContracts.GuideantsAiBaseUrlKey} must include the '{ServiceRoutingContracts.SandboxPath}' prefix for hard cutover routing.");
        }
    }

    private static void ValidateLlamaBaseUrl(string? baseUrl, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            errors.Add($"{ServiceRoutingContracts.LlamaBaseUrlKey} is required and must include the '{ServiceRoutingContracts.LlamaCppPath}' prefix for hard cutover routing.");
            return;
        }

        if (!TryParseHttpUri(baseUrl, out var uri, out var parseError))
        {
            errors.Add($"{ServiceRoutingContracts.LlamaBaseUrlKey} {parseError}");
            return;
        }

        var normalizedPath = NormalizePath(uri);
        if (!string.Equals(normalizedPath, ServiceRoutingContracts.LlamaCppPath, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{ServiceRoutingContracts.LlamaBaseUrlKey} must include the '{ServiceRoutingContracts.LlamaCppPath}' prefix for hard cutover routing.");
        }
    }

    private static string NormalizePath(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        return string.IsNullOrWhiteSpace(path) ? "/" : path;
    }

    private static bool TryParseHttpUri(string value, out Uri uri, out string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!))
        {
            error = "must be an absolute URI.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "must use http or https.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "must include a host.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
