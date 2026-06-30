using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Per-call streamable HTTP MCP client for tools/call (E5: no pooling).
/// </summary>
internal static class McpStreamableHttpToolClient
{
    public static async Task<CallToolResult> CallToolAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        string backingToolName,
        IReadOnlyDictionary<string, object>? arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await using var client = await CreateMcpClientAsync(endpoint, headers, timeout, timeoutCts.Token);
        var callArguments = McpToolArgumentConverter.Convert(arguments);
        return await client.CallToolAsync(
            backingToolName,
            callArguments.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value),
            cancellationToken: timeoutCts.Token);
    }

    private static async Task<McpClient> CreateMcpClientAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan connectionTimeout,
        CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = connectionTimeout,
            AdditionalHeaders = headers.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }
}
