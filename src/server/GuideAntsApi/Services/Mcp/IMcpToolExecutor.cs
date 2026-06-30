using System.Text.Json;
using AntRunner.ToolCalling;

namespace GuideAntsApi.Services.Mcp;

public interface IMcpToolExecutor
{
    Task<string> ExecuteApiToolAsync(
        string assistantName,
        string operationId,
        string mcpServerUrl,
        string toolPath,
        JsonElement methodSchema,
        IReadOnlyDictionary<string, object>? arguments,
        InvocationContext? context,
        CancellationToken cancellationToken = default);

    Task<string> ExecuteSandboxToolAsync(
        string assistantName,
        string operationId,
        string mcpServerUrl,
        string toolPath,
        JsonElement methodSchema,
        IReadOnlyDictionary<string, object>? arguments,
        InvocationContext? context,
        CancellationToken cancellationToken = default);
}
