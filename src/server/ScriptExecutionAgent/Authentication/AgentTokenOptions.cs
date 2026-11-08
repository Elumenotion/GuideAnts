using Microsoft.AspNetCore.Authentication;

namespace ScriptExecutionAgent;

/// <summary>
/// Static token configuration for the agent and admin authentication schemes.
/// </summary>
internal sealed class AgentTokenOptions : AuthenticationSchemeOptions
{
    public bool RequireToken { get; set; }

    public string? Token { get; set; }
}
