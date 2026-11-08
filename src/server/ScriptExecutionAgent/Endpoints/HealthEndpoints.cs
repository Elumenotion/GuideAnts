namespace ScriptExecutionAgent;

/// <summary>
/// Liveness probe for the agent container.
/// </summary>
internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => "OK");
    }
}
