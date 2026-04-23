
namespace AntRunner.Chat
{
    /// <summary>
    /// Writes a dictionary of environment variables to the current process.
    /// </summary>
    public static class EnvironmentSettings
    {
        /// <summary>
        /// Sets the specified environment variables.
        /// </summary>
        /// <param name="environmentVariables">A dictionary containing the environment variable names and their values.</param>
        public static void Set(Dictionary<string, string> environmentVariables)
        {
            foreach (var kvp in environmentVariables)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }
    }
}
