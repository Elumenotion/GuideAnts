using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace ScriptExecutionAgent;

/// <summary>
/// Registers agent services, authentication schemes, and authorization policies.
/// Mirrors GuideAntsApi's StartupConfiguration shape.
/// </summary>
internal static class AgentStartupConfiguration
{
    public static void ConfigureServices(WebApplicationBuilder builder, AgentRuntimeOptions options)
    {
        builder.Services.AddLogging();
        builder.Services.AddSingleton(options);

        // No default scheme: UseAuthentication() stays a no-op, so policy-free routes
        // (/health) never run the token handler and its "token missing" warning.
        builder.Services.AddAuthentication()
            .AddScheme<AgentTokenOptions, AgentTokenAuthenticationHandler>(
                AgentTokenAuthenticationHandler.SchemeName,
                authOptions =>
                {
                    authOptions.RequireToken = options.Security.RequireAgentToken;
                    authOptions.Token = options.Security.AgentToken;
                })
            .AddScheme<AgentTokenOptions, AdminTokenAuthenticationHandler>(
                AdminTokenAuthenticationHandler.SchemeName,
                authOptions =>
                {
                    authOptions.RequireToken = true;
                    authOptions.Token = options.AdminOptions.AdminToken;
                });

        builder.Services.AddAuthorization(authOptions =>
        {
            authOptions.AddPolicy(AgentAuthorizationPolicies.Agent, AgentAuthorizationPolicies.AgentPolicy());
            authOptions.AddPolicy(AgentAuthorizationPolicies.Admin, AgentAuthorizationPolicies.AdminPolicy());
        });
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
