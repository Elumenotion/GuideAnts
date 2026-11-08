namespace ScriptExecutionAgent;

internal sealed record AgentSecurityOptions(
    bool RequireAgentToken,
    string? AgentToken,
    bool AllowOwnershipFallback,
    bool EnableNotebookIdentityIsolation);

