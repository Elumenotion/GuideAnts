
namespace ScriptExecutionAgent;

/// <summary>
/// Environment-variable readers used by agent bootstrap.
/// </summary>
internal static class AgentEnvironment
{
    internal static bool GetBooleanEnvironmentVariable(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    internal static int GetPositiveIntEnvironmentVariable(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }
}
