using System.IO;

namespace AntRunner.ToolCalling.AssistantDefinitions.Storage
{
    internal class EmbeddedResourceStorage
    {
        internal static string? GetInstructions(string assistantName)
        {
            return GetEmbeddedResource($"{assistantName}.md");
        }

        internal static string? GetManifest(string asistantName)
        {
            return GetEmbeddedResource($"{asistantName}.json");
        }

        /// <summary>
        /// Looks for the resource and returns a string or null
        /// </summary>
        /// <param name="resourceName"></param>
        /// <returns>The resource as a string or null</returns>
        private static string? GetEmbeddedResource(string resourceName)
        {
            // Search for the resource in all loaded assemblies
            string? json = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.IsDynamic)
                {
                    using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                    if (resourceStream != null)
                    {
                        using StreamReader reader = new(resourceStream);
                        json = reader.ReadToEnd();
                        Trace.TraceInformation($"JSON data successfully read from resourceName '{resourceName}'.");
                    }
                }
            }

            return json;
        }
    }
}
