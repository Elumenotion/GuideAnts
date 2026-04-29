using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AntRunner.ToolCalling.AssistantDefinitions.Storage
{
    /// <summary>
    /// Provides methods for reading assistant definitions from the database.
    /// </summary>
    public static class AssistantDefinitionFiles
    {
        /// <summary>
        /// Gets assistant metadata for cache validation (Updated timestamp).
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>Tuple of (Updated timestamp, Assistant ID) if found, null otherwise.</returns>
        public static async Task<(DateTime? Updated, Guid? Id)?> GetAssistantMetadata(string assistantName)
        {
            return await DatabaseStorage.GetAssistantMetadata(assistantName);
        }

        /// <summary>
        /// Reads the complete assistant definition with all metadata from database storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the assistant storage metadata.</returns>
        public static async Task<AssistantStorageMetadata?> GetAssistantComplete(string assistantName)
        {
            return await DatabaseStorage.GetAssistant(assistantName);
        }

        /// <summary>
        /// Reads the assistant definition JSON from the file system or storage.
        /// Legacy method for backward compatibility - prefer GetAssistantComplete for new code.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the assistant definition JSON.</returns>
        public static async Task<string?> GetManifest(string assistantName)
        {
            var complete = await GetAssistantComplete(assistantName);
            return complete?.ManifestJson;
        }

        /// <summary>
        /// Reads the assistant instructions from the file system or storage.
        /// Legacy method for backward compatibility - prefer GetAssistantComplete for new code.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the assistant instructions.</returns>
        public static async Task<string?> GetInstructions(string assistantName)
        {
            var complete = await GetAssistantComplete(assistantName);
            return complete?.Instructions;
        }

        /// <summary>
        /// Reads assistant action authorization derived from DB-backed OpenAPI auth providers.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the assistant action authorization.</returns>
        public static async Task<string?> GetActionAuth(string assistantName)
        {
            var complete = await GetAssistantComplete(assistantName);
            if (complete?.DomainAuth == null)
            {
                return null;
            }

            return JsonSerializer.Serialize(
                complete.DomainAuth,
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
        }

        public static async Task<string?> GetContextOptions(string assistantName)
        {
            // Check database first
            var complete = await GetAssistantComplete(assistantName);
            if (complete != null && !string.IsNullOrWhiteSpace(complete.ContextOptionsJson))
            {
                return complete.ContextOptionsJson;
            }

            return null;
        }

        /// <summary>
        /// Gets avatar bytes and content type for an assistant.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Tuple of (Bytes, ContentType) if found, null otherwise.</returns>
        public static async Task<(byte[] Bytes, string ContentType)?> GetAssistantAvatarAsync(
            string assistantName, 
            CancellationToken cancellationToken = default)
        {
            return await DatabaseStorage.GetAssistantAvatarAsync(assistantName, cancellationToken);
        }

        /// <summary>
        /// Gets conversation starters for an assistant.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of conversation starter prompts if found, null otherwise.</returns>
        public static async Task<List<string>?> GetAssistantConversationStartersAsync(
            string assistantName, 
            CancellationToken cancellationToken = default)
        {
            return await DatabaseStorage.GetAssistantConversationStartersAsync(assistantName, cancellationToken);
        }
    }
}
