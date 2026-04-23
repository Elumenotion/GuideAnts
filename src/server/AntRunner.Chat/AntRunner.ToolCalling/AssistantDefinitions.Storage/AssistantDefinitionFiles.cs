using System.Threading;

namespace AntRunner.ToolCalling.AssistantDefinitions.Storage
{
    /// <summary>
    /// Provides methods for reading assistant definition files from the file system or storage.
    /// Files from storage by default must be located under ./Assistants where the "./" represents the execution folder.
    /// Set your files to "Copy Always" to copy them to the correct location(s) at build time.
    /// Set the "ASSISTANTS_BASE_FOLDER_PATH" environment variable to override the default location "./Assistants""
    /// 
    /// Loading Priority:
    /// 1. Database assistants
    /// 2. Embedded resources
    /// 3. File storage
    /// 4. Blob storage
    /// </summary>
    public class AssistantDefinitionFiles
    {
        private static bool IsFileFallbackDisabled()
        {
            var raw = Environment.GetEnvironmentVariable("ASSISTANTS_DISABLE_FILE_FALLBACK");
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (bool.TryParse(raw, out var parsed)) return parsed;
            return raw.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

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
        /// Reads the complete assistant definition with all metadata from storage.
        /// Checks database first, then falls back to file-based storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the assistant storage metadata.</returns>
        public static async Task<AssistantStorageMetadata?> GetAssistantComplete(string assistantName)
        {
            // Try database first
            var dbResult = await DatabaseStorage.GetAssistant(assistantName);
            if (dbResult != null) return dbResult;

            if (IsFileFallbackDisabled()) return null;

            // Fall back to file-based storage
            var manifest = EmbeddedResourceStorage.GetManifest(assistantName) 
                ?? await FileStorage.GetManifest(assistantName) 
                ?? await BlobStorage.GetManifest(assistantName);

            if (manifest == null) return null;

            var instructions = EmbeddedResourceStorage.GetInstructions(assistantName) 
                ?? await FileStorage.GetInstructions(assistantName) 
                ?? await BlobStorage.GetInstructions(assistantName);

            var contextOptions = await FileStorage.GetContextOptions(assistantName);

            return new AssistantStorageMetadata(
                ManifestJson: manifest,
                Instructions: instructions,
                ContextOptionsJson: contextOptions,
                Updated: null  // File-based assistants don't have timestamps
            );
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
        /// Reads the assistant action authorization from the file system or storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the assistant action authorization.</returns>
        public static async Task<string?> GetActionAuth(string assistantName)
        {
            return await FileStorage.GetActionAuth(assistantName) ?? await BlobStorage.GetActionAuth(assistantName);
        }

        /// <summary>
        /// Retrieves the list of files in the OpenAPI folder for the specified assistant from the file system or storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the list of files in the OpenAPI folder.</returns>
        public static async Task<List<string>?> GetFilesInOpenApiFolder(string assistantName)
        {
            return FileStorage.GetFilesInOpenApiFolder(assistantName) ?? await BlobStorage.GetFilesInOpenApiFolder(assistantName);
        }

        /// <summary>
        /// Retrieves a file from the file system or storage.
        /// </summary>
        /// <param name="filePath">The path of the file.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the byte array of the file content.</returns>
        public static async Task<byte[]?> GetFile(string filePath)
        {
            return await FileStorage.GetFile(filePath) ?? await BlobStorage.GetFile(filePath);
        }

        /// <summary>
        /// Retrieves the list of files in the vector store folder for the specified assistant and vector store name from the file system or storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <param name="vectorStoreName">The name of the vector store.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the list of files in the vector store folder.</returns>
        public static async Task<List<string>?> GetFilesInVectorStoreFolder(string assistantName, string vectorStoreName)
        {
            return FileStorage.GetFilesInVectorStoreFolder(assistantName, vectorStoreName) ?? await BlobStorage.GetFilesInVectorStoreFolder(assistantName, vectorStoreName);
        }

        /// <summary>
        /// Retrieves the list of files in the code interpreter folder for the specified assistant from the file system or storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the list of files in the code interpreter folder.</returns>
        public static async Task<List<string>?> GetFilesInCodeInterpreterFolder(string assistantName)
        {
            return FileStorage.GetFilesInCodeInterpreterFolder(assistantName) ?? await BlobStorage.GetFilesInCodeInterpreterFolder(assistantName);
        }

        public static async Task<string?> GetContextOptions(string assistantName)
        {
            // Check database first
            var complete = await GetAssistantComplete(assistantName);
            if (complete != null && !string.IsNullOrWhiteSpace(complete.ContextOptionsJson))
            {
                return complete.ContextOptionsJson;
            }

            if (IsFileFallbackDisabled()) return null;

            // Fall back to file storage
            return await FileStorage.GetContextOptions(assistantName);
        }

        /// <summary>
        /// Gets avatar bytes and content type for an assistant.
        /// Checks database first, then falls back to file-based storage.
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
        /// Checks database first, then falls back to file-based storage.
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
