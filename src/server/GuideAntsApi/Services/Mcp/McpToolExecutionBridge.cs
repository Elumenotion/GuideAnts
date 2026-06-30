using System.Text.Json;
using AntRunner.ToolCalling;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Static bridge from AntRunner.Chat ThreadRun to scoped <see cref="IMcpToolExecutor"/>.
/// </summary>
public static class McpToolExecutionBridge
{
    private static IServiceProvider? _staticServiceProvider;

    public static void InitializeServiceProvider(IServiceProvider serviceProvider)
    {
        _staticServiceProvider = serviceProvider
            ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    internal static IServiceProvider? StaticServiceProvider => _staticServiceProvider;

    public static async Task<string> ExecuteMcpApiTool(
        string assistantName,
        string operationId,
        string mcpServerUrl,
        string toolPath,
        JsonElement methodSchema,
        Dictionary<string, object>? arguments,
        InvocationContext? context,
        CancellationToken cancellationToken = default)
    {
        if (_staticServiceProvider is null)
        {
            return "ERROR: MCP tool execution is not initialized. Ensure McpToolExecutionBridge.InitializeServiceProvider runs at startup.";
        }

        using var scope = _staticServiceProvider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IMcpToolExecutor>();
        return await executor.ExecuteApiToolAsync(
            assistantName,
            operationId,
            mcpServerUrl,
            toolPath,
            methodSchema,
            arguments,
            context,
            cancellationToken);
    }

    public static async Task<string> ExecuteMcpSandboxTool(
        string assistantName,
        string operationId,
        string mcpServerUrl,
        string toolPath,
        JsonElement methodSchema,
        Dictionary<string, object>? arguments,
        InvocationContext? context,
        CancellationToken cancellationToken = default)
    {
        if (_staticServiceProvider is null)
        {
            return "ERROR: MCP tool execution is not initialized. Ensure McpToolExecutionBridge.InitializeServiceProvider runs at startup.";
        }

        using var scope = _staticServiceProvider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IMcpToolExecutor>();
        return await executor.ExecuteSandboxToolAsync(
            assistantName,
            operationId,
            mcpServerUrl,
            toolPath,
            methodSchema,
            arguments,
            context,
            cancellationToken);
    }
}
