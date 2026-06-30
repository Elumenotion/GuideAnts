namespace GuideAntsApi.Models.Guides;

/// <summary>
/// Locked values for MCP tool source runtime execution mode (design §3.1, E1–E2, E8).
/// </summary>
public static class McpRuntimeExecution
{
    public const string Api = "api";
    public const string SandboxSubprocess = "sandbox_subprocess";

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Api,
        SandboxSubprocess,
    };
}

/// <summary>
/// Locked values for MCP discovery transport (design §3.3, D2 revised).
/// </summary>
public static class McpDiscoveryTransport
{
    public const string StreamableHttp = "streamable_http";
    public const string Stdio = "stdio";

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        StreamableHttp,
        Stdio,
    };
}

public record McpPackageDescriptorDto(
    string RegistryType,
    string Identifier,
    string Command,
    List<string>? Args = null);

public record McpEnvironmentVariableRefDto(
    string Name,
    string SecretRef);
