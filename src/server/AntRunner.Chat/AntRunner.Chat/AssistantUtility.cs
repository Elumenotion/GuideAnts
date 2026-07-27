using System.Collections.Concurrent;
using AntRunner.ToolCalling.Functions;
using static AntRunner.ToolCalling.AssistantDefinitions.Storage.AssistantDefinitionFiles;
using AntRunner.ToolCalling.AssistantDefinitions;
using System.Text.Json.Serialization;
using AntRunner.ToolCalling;
using Microsoft.Extensions.Logging;

namespace AntRunner.Chat
{
    /// <summary>
    /// Fetches and caches assistants resolved from database-backed definitions.
    /// Cache entries live until explicitly cleared via <see cref="ClearCache"/> when the API mutates a definition.
    /// </summary>
    public static class AssistantUtility
    {
        private static readonly ILogger Logger = ChatDiagnostics.CreateLogger(nameof(AssistantUtility));

        private record CachedAssistant(AssistantDefinition Definition);

        // Cache keyed by assistant name. Invalidated only via ClearCache / ClearAllCache.
        private static readonly ConcurrentDictionary<string, CachedAssistant> AssistantDefinitionCache = new();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> LoadLocks = new();

        /// <summary>
        /// Reads the assistant definition from database-backed storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant</param>
        /// <returns>AssistantDefinition if found, null otherwise</returns>
        public static async Task<AssistantDefinition?> GetAssistantCreateRequest(string assistantName)
        {
            var cacheKey = GenerateCacheKey(assistantName);

            if (TryGetCachedDefinition(cacheKey, out var cachedDefinition))
            {
                return cachedDefinition;
            }

            var loadLock = LoadLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await loadLock.WaitAsync();
            try
            {
                if (TryGetCachedDefinition(cacheKey, out cachedDefinition))
                {
                    return cachedDefinition;
                }

                AssistantDefinition? loadedDefinition;
                try
                {
                    loadedDefinition = await LoadAssistantDefinitionAsync(assistantName);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to load assistant definition for {AssistantName}", LogValueSanitizer.Sanitize(assistantName));

                    if (TryGetCachedDefinition(cacheKey, out cachedDefinition))
                    {
                        Logger.LogWarning(
                            "Serving cached assistant definition for {AssistantName} after load failure",
                            LogValueSanitizer.Sanitize(assistantName));
                        return cachedDefinition;
                    }

                    throw;
                }

                if (loadedDefinition == null)
                {
                    return null;
                }

                await AddFunctionTools(assistantName, loadedDefinition);

                AssistantDefinitionCache[cacheKey] = new CachedAssistant(loadedDefinition);
                return loadedDefinition;
            }
            finally
            {
                loadLock.Release();
            }
        }

        private static bool TryGetCachedDefinition(string cacheKey, out AssistantDefinition? definition)
        {
            if (AssistantDefinitionCache.TryGetValue(cacheKey, out var cached))
            {
                definition = cached.Definition;
                return true;
            }

            definition = null;
            return false;
        }

        private static string GenerateCacheKey(string assistantName) => assistantName;

        /// <summary>
        /// Clears a specific assistant from the cache.
        /// Called when the API updates or deletes an assistant or guide definition.
        /// </summary>
        public static void ClearCache(string assistantName)
        {
            var cacheKey = GenerateCacheKey(assistantName);
            AssistantDefinitionCache.TryRemove(cacheKey, out _);
        }

        /// <summary>
        /// Clears all cached assistants.
        /// </summary>
        public static void ClearAllCache()
        {
            AssistantDefinitionCache.Clear();
        }

        private static async Task<AssistantDefinition?> LoadAssistantDefinitionAsync(string assistantName)
        {
            var storageMetadata = await GetAssistantComplete(assistantName);
            if (storageMetadata == null)
            {
                return null;
            }

            var lenientOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
            };
            var definition = JsonSerializer.Deserialize<AssistantDefinition>(storageMetadata.ManifestJson, lenientOptions);
            if (definition == null)
            {
                return null;
            }

            definition.Name = assistantName;
            definition.Id = storageMetadata.Id;

            if (!string.IsNullOrWhiteSpace(storageMetadata.Instructions))
            {
                definition.Instructions = storageMetadata.Instructions;
            }

            if (!string.IsNullOrWhiteSpace(storageMetadata.ContextOptionsJson))
            {
                try
                {
                    var contextList = JsonSerializer.Deserialize<List<ContextOptionItem>>(storageMetadata.ContextOptionsJson);
                    var contextDict = contextList?.ToDictionary(i => i.key, i => i.value ?? string.Empty);
                    if (contextDict != null && contextDict.Count > 0)
                    {
                        definition.ContextOptions = contextDict;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to parse context options for assistant {AssistantName}", LogValueSanitizer.Sanitize(assistantName));
                }
            }

            if (storageMetadata.AdditionalMetadata != null)
            {
                definition.Metadata ??= new Dictionary<string, string>();
                foreach (var kvp in storageMetadata.AdditionalMetadata)
                {
                    definition.Metadata[kvp.Key] = kvp.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(storageMetadata.SamplingParametersJson))
            {
                try
                {
                    definition.SamplingParameters = JsonSerializer.Deserialize<Dictionary<string, double>>(
                        storageMetadata.SamplingParametersJson);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to parse sampling parameters for assistant {AssistantName}", LogValueSanitizer.Sanitize(assistantName));
                }
            }

            return definition;
        }

        private static async Task AddFunctionTools(string assistantName, AssistantDefinition options)
        {
            // Add crew-bridge tools for Guide assistants
            if (options.Metadata != null && options.Metadata.TryGetValue("__crew_names__", out var crewNamesStr))
            {
                var crewNames = crewNamesStr.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (crewNames.Count > 0)
                {
                    var crewAssistants = new List<AssistantDefinition>();
                    foreach (var crewName in crewNames)
                    {
                        var crewAssistant = await GetAssistantCreateRequest(crewName);
                        if (crewAssistant != null)
                        {
                            crewAssistants.Add(crewAssistant);
                        }
                    }

                    if (crewAssistants.Count > 0)
                    {
                        var bridgeSchema = CrewBridgeSchemaGenerator.GetSchema(crewAssistants);
                        var bridgeDefs = OpenApiHelper.GetToolDefinitionsFromJson(bridgeSchema);
                        foreach (var td in bridgeDefs)
                        {
                            options.Tools!.Add(td);
                        }
                    }
                }
            }

            var openApiToolDefinitions = await GetOpenApiToolDefinitions(assistantName);
            foreach (var toolDefinition in openApiToolDefinitions)
            {
                options.Tools!.Add(toolDefinition);
            }

            var hasVectorStores = options.ToolResources?.FileSearch?.VectorStoreIds != null
                && options.ToolResources.FileSearch.VectorStoreIds.Any();

            if (hasVectorStores)
            {
                TryInjectRegisteredTool(options, "SearchAssistantFiles");
            }

            if (options.Skills is { Count: > 0 })
            {
                TryInjectRegisteredTool(options, "skills_list");
                TryInjectRegisteredTool(options, "skills_read");
            }

            var requestedPlaceholders = options.Tools?
                .Where(t => t.Type != null)
                .ToList();

            if (requestedPlaceholders?.Count > 0)
            {
                var allToolOperations = ToolContractRegistry.GetAllToolOperations();
                var processedSchemas = new HashSet<string>();

                foreach (var placeholder in requestedPlaceholders.Where(p => p.Type != null))
                {
                    var matchingOperation = allToolOperations.FirstOrDefault(kvp =>
                        string.Equals(kvp.Key, placeholder.Type, StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrEmpty(matchingOperation.Key) &&
                        !processedSchemas.Contains(matchingOperation.Value))
                    {
                        try
                        {
                            var schema = ToolContractRegistry.GenerateOpenApiSchema(matchingOperation.Value);
                            var toolDefinitions = OpenApiHelper.GetToolDefinitionsFromJson(schema);

                            foreach (var def in toolDefinitions)
                            {
                                if (def.Function?.AsObject?.Name ==
                                    ToolOperationIdSanitizer.ToWireName(matchingOperation.Key))
                                {
                                    options.Tools!.Add(def);
                                }
                            }

                            processedSchemas.Add(matchingOperation.Value);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(
                                ex,
                                "Failed to generate schema for tool operation {Operation}",
                                LogValueSanitizer.Sanitize(matchingOperation.Value));
                        }
                    }
                }

                options.Tools = options.Tools!
                    .Where(t => t.Type == null || !allToolOperations.ContainsKey(t.Type!))
                    .ToList();
            }
        }

        private static void TryInjectRegisteredTool(AssistantDefinition options, string operationId)
        {
            var allToolOperations = ToolContractRegistry.GetAllToolOperations();
            var operation = allToolOperations.FirstOrDefault(kvp =>
                kvp.Key.Equals(operationId, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(operation.Key))
            {
                return;
            }

            try
            {
                var schema = ToolContractRegistry.GenerateOpenApiSchema(operation.Value);
                var toolDefinitions = OpenApiHelper.GetToolDefinitionsFromJson(schema);

                foreach (var def in toolDefinitions)
                {
                    if (def.Function?.AsObject?.Name == ToolOperationIdSanitizer.ToWireName(operationId))
                    {
                        options.Tools!.Add(def);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to inject registered tool {OperationId}", LogValueSanitizer.Sanitize(operationId));
            }
        }

        private static async Task<List<ToolDefinition>> GetOpenApiToolDefinitions(string assistantName)
        {
            var toolDefinitions = new List<ToolDefinition>();

            var storageMetadata = await GetAssistantComplete(assistantName);
            if (storageMetadata?.OpenApiSchemas == null || storageMetadata.OpenApiSchemas.Count == 0)
            {
                return toolDefinitions;
            }

            foreach (var kvp in storageMetadata.OpenApiSchemas)
            {
                var defs = OpenApiHelper.GetToolDefinitionsFromJson(kvp.Value);
                toolDefinitions.AddRange(defs);
            }

            return toolDefinitions;
        }

        private record ContextOptionItem(string key, string? value);
    }
}
