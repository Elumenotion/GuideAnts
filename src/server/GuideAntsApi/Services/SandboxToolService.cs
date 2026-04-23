using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Functions;
using System.Text;
using System.Text.Json;

namespace GuideAntsApi.Services;

/// <summary>
/// Service for executing sandbox tools. Sandbox tools execute Python functions in a 
/// sandboxed Docker environment. The initialization script is specified in the OpenAPI 
/// schema's server URL (e.g., sandbox://init.py).
/// </summary>
public interface ISandboxToolService
{
    /// <summary>
    /// Executes a sandbox tool by loading the initialization script, generating a combined
    /// Python script, and executing it via the notebook Docker script service.
    /// </summary>
    /// <param name="toolName">The name of the tool being executed.</param>
    /// <param name="functionName">The Python function name to call (from operationId).</param>
    /// <param name="parameters">The parameters to pass to the function.</param>
    /// <param name="initializationScriptFilename">The initialization script filename (e.g., "init.py").</param>
    /// <param name="assistantName">The name of the assistant that owns this tool.</param>
    /// <param name="context">The invocation context containing project/notebook info.</param>
    /// <returns>Script execution result containing stdout, stderr, and file changes.</returns>
    Task<ScriptExecutionResult> ExecuteSandboxToolAsync(
        string toolName,
        string functionName,
        Dictionary<string, object> parameters,
        string initializationScriptFilename,
        string assistantName,
        InvocationContext? context = null);
}

public class SandboxToolService : ISandboxToolService
{
    private readonly INotebookDockerScriptService _dockerScriptService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IStoragePathResolver _pathResolver;
    private readonly ILogger<SandboxToolService> _logger;

    public SandboxToolService(
        INotebookDockerScriptService dockerScriptService,
        IServiceProvider serviceProvider,
        IStoragePathResolver pathResolver,
        ILogger<SandboxToolService> logger)
    {
        _dockerScriptService = dockerScriptService;
        _serviceProvider = serviceProvider;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    public async Task<ScriptExecutionResult> ExecuteSandboxToolAsync(
        string toolName,
        string functionName,
        Dictionary<string, object> parameters,
        string initializationScriptFilename,
        string assistantName,
        InvocationContext? context = null)
    {
        _logger.LogInformation(
            "Executing sandbox tool {ToolName} (function: {FunctionName}) with init script {InitScript} for assistant {AssistantName}",
            toolName, functionName, initializationScriptFilename, assistantName);

        if (context == null)
        {
            return new ScriptExecutionResult
            {
                StandardOutput = string.Empty,
                StandardError = "ERROR: InvocationContext is required for sandbox tool execution."
            };
        }

        // Determine the module path (container path, not host path)
        var modulePath = GetModuleContainerPath(initializationScriptFilename, assistantName, context);
        if (modulePath == null)
        {
            return new ScriptExecutionResult
            {
                StandardOutput = string.Empty,
                StandardError = $"ERROR: Module containing '{initializationScriptFilename}' not found. " +
                               $"Ensure the files exist in the assistant's CodeInterpreter files and were copied to the notebook Resources folder."
            };
        }

        // Generate the execution script that imports the module properly
        var combinedScript = GenerateExecutionScript(modulePath, functionName, parameters);

        _logger.LogDebug("Generated combined script for sandbox tool {ToolName}:\n{Script}", toolName, combinedScript);

        // Execute via Docker script service
        var result = await _dockerScriptService.ExecuteDockerScriptAsync(
            combinedScript,
            "guideants-ai",
            ScriptType.Python,
            context);

        return result;
    }

    /// <summary>
    /// Gets the container path to the module directory containing the initialization script.
    /// Returns the path as it will be seen inside the Docker container.
    /// </summary>
    private string? GetModuleContainerPath(
        string filename,
        string assistantName,
        InvocationContext context)
    {
        var notebookRoot = _pathResolver.GetNotebookRootPath(context.ProjectId, context.NotebookId);

        // Build safe crew folder name (same logic as NotebookService.CopyGuideFilesToNotebookAsync)
        var safeCrew = assistantName
            .Replace(" ", "-")
            .Replace("/", "-")
            .Replace("\\", "-");

        // Container base path (how the notebook directory is mounted in Docker)
        var containerBasePath = _pathResolver.GetContainerNotebookRootPath(context.ProjectId, context.NotebookId);

        // Try crew-specific folder first: Resources/crew-{assistantName}/{filename}
        var crewScriptPath = Path.Combine(notebookRoot, "Resources", $"crew-{safeCrew}", filename);
        if (File.Exists(crewScriptPath))
        {
            var containerModulePath = $"{containerBasePath}/Resources/crew-{safeCrew}";
            _logger.LogDebug("Found sandbox module at crew folder: {HostPath} -> {ContainerPath}", 
                crewScriptPath, containerModulePath);
            return containerModulePath;
        }

        // Try root Resources/ folder (for guide-level files): Resources/{filename}
        var rootScriptPath = Path.Combine(notebookRoot, "Resources", filename);
        if (File.Exists(rootScriptPath))
        {
            var containerModulePath = $"{containerBasePath}/Resources";
            _logger.LogDebug("Found sandbox module at notebook Resources: {HostPath} -> {ContainerPath}", 
                rootScriptPath, containerModulePath);
            return containerModulePath;
        }

        _logger.LogWarning(
            "Sandbox module containing '{Filename}' not found for assistant '{AssistantName}' in notebook {NotebookId}. " +
            "Checked paths: {CrewPath}, {RootPath}",
            filename, assistantName, context.NotebookId, crewScriptPath, rootScriptPath);

        return null;
    }

    /// <summary>
    /// Generates a Python script that imports the module and calls the specified function.
    /// Uses proper Python module importing (sys.path + importlib) instead of pasting code.
    /// </summary>
    private static string GenerateExecutionScript(
        string modulePath,
        string functionName,
        Dictionary<string, object> parameters)
    {
        var sb = new StringBuilder();

        // Serialize parameters to JSON, handling various types
        var parametersJson = SerializeParameters(parameters);

        // Generate the script
        sb.AppendLine("# === Sandbox Tool Execution Script (auto-generated) ===");
        sb.AppendLine("import sys");
        sb.AppendLine("import os");
        sb.AppendLine("import json");
        sb.AppendLine("import importlib");
        sb.AppendLine();
        
        // Set up the module path
        sb.AppendLine("# Set up module path for proper package imports");
        sb.AppendLine($"_module_dir = '{modulePath}'");
        sb.AppendLine("_parent_dir = os.path.dirname(_module_dir)");
        sb.AppendLine("if _parent_dir not in sys.path:");
        sb.AppendLine("    sys.path.insert(0, _parent_dir)");
        sb.AppendLine();
        
        // Import the module
        sb.AppendLine("# Import the module as a package");
        sb.AppendLine("_module_name = os.path.basename(_module_dir)");
        sb.AppendLine("try:");
        sb.AppendLine("    _module = importlib.import_module(_module_name)");
        sb.AppendLine("except ImportError as e:");
        sb.AppendLine("    print(f\"Error: Failed to import module '{_module_name}' from {_module_dir}: {str(e)}\", file=sys.stderr)");
        sb.AppendLine("    sys.exit(1)");
        sb.AppendLine();
        
        // Get the function from the module
        sb.AppendLine($"# Get the function: {functionName}");
        sb.AppendLine($"if not hasattr(_module, '{functionName}'):");
        sb.AppendLine($"    print(f\"Error: Function '{functionName}' not found in module '{{_module_name}}'\", file=sys.stderr)");
        sb.AppendLine("    sys.exit(1)");
        sb.AppendLine($"_func = getattr(_module, '{functionName}')");
        sb.AppendLine();
        
        // Parse parameters
        sb.AppendLine("# Parse parameters from JSON");
        sb.AppendLine($"_params_json = '''{parametersJson}'''");
        sb.AppendLine("_params = json.loads(_params_json)");
        sb.AppendLine();

        // Call the function and handle the result
        sb.AppendLine($"# Call the function: {functionName}");
        sb.AppendLine("try:");
        sb.AppendLine("    _result = _func(**_params)");
        sb.AppendLine("    ");
        sb.AppendLine("    # Serialize result to JSON");
        sb.AppendLine("    if _result is not None:");
        sb.AppendLine("        _result_json = json.dumps(_result, default=str, indent=2)");
        sb.AppendLine("        print(_result_json)");
        sb.AppendLine("    else:");
        sb.AppendLine("        print('null')");
        sb.AppendLine("except TypeError as e:");
        sb.AppendLine($"    print(f\"Error: Invalid parameters for function '{functionName}'. {{str(e)}}\", file=sys.stderr)");
        sb.AppendLine("    sys.exit(1)");
        sb.AppendLine("except Exception as e:");
        sb.AppendLine("    print(f\"Error: {str(e)}\", file=sys.stderr)");
        sb.AppendLine("    sys.exit(1)");

        return sb.ToString();
    }

    /// <summary>
    /// Serializes parameters to JSON, handling JsonElement values properly.
    /// </summary>
    private static string SerializeParameters(Dictionary<string, object> parameters)
    {
        // Convert any JsonElement values to proper objects for serialization
        var normalized = new Dictionary<string, object?>();
        foreach (var kvp in parameters)
        {
            if (kvp.Value is JsonElement je)
            {
                normalized[kvp.Key] = ConvertJsonElement(je);
            }
            else
            {
                normalized[kvp.Key] = kvp.Value;
            }
        }

        // Escape single quotes in the JSON to prevent breaking the Python string literal
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        // Escape backslashes and single quotes for Python triple-quoted string
        return json.Replace("\\", "\\\\").Replace("'''", "\\'\\'\\'");
    }

    /// <summary>
    /// Converts a JsonElement to its appropriate .NET type for serialization.
    /// </summary>
    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.GetRawText()
        };
    }

    // Static service provider for tool calling system compatibility
    private static IServiceProvider? _staticServiceProvider;

    /// <summary>
    /// Initializes the static service provider for tool calling system compatibility.
    /// </summary>
    public static void InitializeServiceProvider(IServiceProvider serviceProvider)
    {
        _staticServiceProvider = serviceProvider;
    }

    /// <summary>
    /// Gets the static service provider for use by ThreadRun.
    /// </summary>
    internal static IServiceProvider? StaticServiceProvider => _staticServiceProvider;

    /// <summary>
    /// Static wrapper method for tool calling system compatibility.
    /// Called from ThreadRun.DoToolCalls when ActionType is SandboxHandled.
    /// </summary>
    public static async Task<ScriptExecutionResult> ExecuteSandboxTool(
        string toolName,
        string functionName,
        Dictionary<string, object> parameters,
        string initializationScriptFilename,
        string assistantName,
        InvocationContext? context = null)
    {
        if (_staticServiceProvider == null)
        {
            throw new InvalidOperationException(
                "Service provider not initialized. Call SandboxToolService.InitializeServiceProvider during application startup.");
        }

        using var scope = _staticServiceProvider.CreateScope();
        var sandboxService = scope.ServiceProvider.GetRequiredService<ISandboxToolService>();

        return await sandboxService.ExecuteSandboxToolAsync(
            toolName,
            functionName,
            parameters,
            initializationScriptFilename,
            assistantName,
            context);
    }
}

