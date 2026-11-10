using Microsoft.AspNetCore.Authorization;

namespace ScriptExecutionAgent;

/// <summary>
/// Authorization policies for the agent HTTP surface.
/// </summary>
internal static class AgentAuthorizationPolicies
{
    public const string Agent = "ScriptAgent";
    public const string Admin = "ScriptAgentAdmin";

    public static AuthorizationPolicy AgentPolicy() => new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(AgentTokenAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();

    public static AuthorizationPolicy AdminPolicy() => new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(AdminTokenAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
}
