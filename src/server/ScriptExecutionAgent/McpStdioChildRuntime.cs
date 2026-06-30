using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ScriptExecutionAgent;

internal static class McpStdioChildRuntime
{
    public static async Task<McpStdioExecutionResult> ExecuteToolCallAsync(
        McpStdioExecutionRequest request,
        string authorizedWorkingDirectory,
        ScriptExecutionScopeOptions scopeOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var projectId = Guid.Parse(request.ProjectId);
        var guideScopeId = Guid.Parse(request.GuideId);
        var scope = ScriptExecutionScopeRuntime.ResolveScope(projectId, guideScopeId, scopeOptions);
        ScriptExecutionScopeRuntime.EnsureScopeDirectory(scope);

        var environmentValidation = ValidateExecutionEnvironment(request.Environment);
        if (!environmentValidation.IsValid)
        {
            return McpStdioExecutionResult.Failed($"Environment validation failed: {environmentValidation.ErrorMessage}");
        }

        var scopedEnvironment = ScriptExecutionScopeRuntime.BuildScriptEnvironment(
            scope,
            request.Environment,
            authorizedWorkingDirectory,
            logger);

        var command = request.Command.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return McpStdioExecutionResult.Failed("MCP package command is required.");
        }

        var arguments = request.Arguments?.Where(arg => arg is not null).Select(arg => arg!).ToArray()
            ?? Array.Empty<string>();

        var (commandFile, commandArgs) = ApplyPrivacyWrapper(command, arguments);
        var transportEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in scopedEnvironment)
        {
            if (value is not null)
            {
                transportEnvironment[key] = value;
            }
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = commandFile,
                Arguments = commandArgs,
                WorkingDirectory = authorizedWorkingDirectory,
                EnvironmentVariables = transportEnvironment,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            });

            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);
            var callArguments = ConvertToolArguments(request.ToolArguments);
            var result = await client.CallToolAsync(
                request.ToolName,
                callArguments,
                cancellationToken: timeoutCts.Token);

            return McpStdioExecutionResult.Succeeded(McpStdioResultFormatter.Format(result));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return McpStdioExecutionResult.Failed(
                $"MCP stdio tool call timed out after {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "MCP stdio child execution failed. projectId={ProjectId} guideId={GuideId} tool={ToolName}",
                projectId,
                guideScopeId,
                LogValueSanitizer.Sanitize(request.ToolName));

            var message = ex.Message;
            return McpStdioExecutionResult.Failed($"MCP stdio tool call failed: {message}");
        }
    }

    public static ValidationResult ValidateMcpStdioRequest(McpStdioExecutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return ValidationResult.Failure("Command is required.");
        }

        if (request.Command.Length > 512)
        {
            return ValidationResult.Failure("Command exceeds maximum length.");
        }

        if (request.Arguments is not null)
        {
            if (request.Arguments.Count > 128)
            {
                return ValidationResult.Failure("Too many command arguments.");
            }

            foreach (var argument in request.Arguments)
            {
                if (argument is null)
                {
                    return ValidationResult.Failure("Command arguments cannot contain null entries.");
                }

                if (argument.Length > 4096)
                {
                    return ValidationResult.Failure("A command argument exceeds maximum length.");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(request.ToolName))
        {
            return ValidationResult.Failure("ToolName is required.");
        }

        if (request.ToolName.Length > 512)
        {
            return ValidationResult.Failure("ToolName exceeds maximum length.");
        }

        if (!Guid.TryParse(request.ProjectId, out var projectId) || projectId == Guid.Empty)
        {
            return ValidationResult.Failure("ProjectId must be a non-empty GUID.");
        }

        if (!Guid.TryParse(request.NotebookId, out var notebookId) || notebookId == Guid.Empty)
        {
            return ValidationResult.Failure("NotebookId must be a non-empty GUID.");
        }

        if (!Guid.TryParse(request.GuideId, out var guideId) || guideId == Guid.Empty)
        {
            return ValidationResult.Failure("GuideId must be a non-empty GUID.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return ValidationResult.Failure("WorkingDirectory is required.");
        }

        if (request.TimeoutSeconds < 1 || request.TimeoutSeconds > 600)
        {
            return ValidationResult.Failure("TimeoutSeconds must be between 1 and 600.");
        }

        return ValidateExecutionEnvironment(request.Environment);
    }

    private static ValidationResult ValidateExecutionEnvironment(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return ValidationResult.Success();
        }

        if (environment.Count > 128)
        {
            return ValidationResult.Failure("Environment contains too many entries.");
        }

        foreach (var (key, value) in environment)
        {
            var keyValidation = ScriptExecutionScopeRuntime.ValidateEnvironmentKey(key);
            if (!keyValidation.IsValid)
            {
                return keyValidation;
            }

            if (value.Length > 64 * 1024)
            {
                return ValidationResult.Failure($"Environment value for '{key}' exceeds maximum size.");
            }
        }

        return ValidationResult.Success();
    }

    private static Dictionary<string, object?> ConvertToolArguments(JsonElement? toolArguments)
    {
        if (toolArguments is null
            || toolArguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new Dictionary<string, object?>();
        }

        var raw = toolArguments.Value.GetRawText();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(raw)
            ?? new Dictionary<string, object?>();
    }

    private static (string FileName, string[] Arguments) ApplyPrivacyWrapper(string commandFile, string[] commandArgs)
    {
        if (!OperatingSystem.IsLinux())
        {
            return (commandFile, commandArgs);
        }

        var wrapper = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_PRIVACY_WRAPPER");
        if (string.IsNullOrWhiteSpace(wrapper))
        {
            wrapper = "/usr/local/bin/ga-script-exec";
        }

        if (!File.Exists(wrapper))
        {
            return (commandFile, commandArgs);
        }

        var wrappedArgs = new List<string> { commandFile };
        wrappedArgs.AddRange(commandArgs);
        return (wrapper, wrappedArgs.ToArray());
    }
}

internal static class McpStdioResultFormatter
{
    public static string Format(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            return structured.GetRawText();
        }

        if (result.Content is { Count: > 0 })
        {
            var textParts = result.Content
                .OfType<TextContentBlock>()
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();

            if (textParts.Count > 0)
            {
                var combined = string.Join("\n", textParts);
                return result.IsError == true ? $"ERROR: {combined}" : combined;
            }

            return JsonSerializer.Serialize(result.Content);
        }

        return result.IsError == true
            ? "ERROR: MCP tool call returned an error with no content."
            : string.Empty;
    }
}

public sealed record McpStdioExecutionRequest
{
    public string ProjectId { get; init; } = string.Empty;
    public string NotebookId { get; init; } = string.Empty;
    public string GuideId { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string>? Arguments { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public JsonElement? ToolArguments { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed class McpStdioExecutionResult
{
    public bool Success { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? Error { get; init; }

    public static McpStdioExecutionResult Succeeded(string result) =>
        new() { Success = true, Result = result };

    public static McpStdioExecutionResult Failed(string error) =>
        new() { Success = false, Error = error };
}
