using System.Collections.Concurrent;
using AntRunner.ToolCalling.Functions;
using static AntRunner.ToolCalling.AssistantDefinitions.Storage.AssistantDefinitionFiles;
using AntRunner.ToolCalling.AssistantDefinitions;
using System.Text.Json.Serialization;
using AntRunner.ToolCalling;

namespace AntRunner.Chat
{
    /// <summary>
    /// Fetches and caches assistants resolved from database-backed definitions.
    /// </summary>
    public static class AssistantUtility
    {
        /// <summary>
        /// Cache entry containing the definition and metadata for invalidation.
        /// </summary>
        private record CachedAssistant(
            AssistantDefinition? Definition,
            DateTime? Updated,
            DateTime CachedAt
        );

        // Cache keyed by assistant name.
        private static readonly ConcurrentDictionary<string, CachedAssistant> AssistantDefinitionCache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Reads the assistant definition from database-backed storage.
        /// </summary>
        /// <param name="assistantName">The name of the assistant</param>
        /// <returns>AssistantDefinition if found, null otherwise</returns>
        public static async Task<AssistantDefinition?> GetAssistantCreateRequest(string assistantName)
        {
            var cacheKey = GenerateCacheKey(assistantName);

            // Check cache and validate against database if needed
            if (AssistantDefinitionCache.TryGetValue(cacheKey, out var cached))
            {
                // Check if cache is still valid
                var isCacheValid = await IsCacheValid(cached, assistantName);
                if (isCacheValid)
                {
                    return cached.Definition;
                }
                
                // Cache is stale, remove it
                AssistantDefinitionCache.TryRemove(cacheKey, out _);
            }

            // Load assistant definition
            AssistantDefinition? definition = null;
            DateTime? updated = null;

            // Handle dynamic notebook template Guide assistants, e.g. "Creative Guide" or "Creative Guide Guide"
            if (assistantName.EndsWith(" Guide", StringComparison.OrdinalIgnoreCase))
            {
                var guide = await TryBuildGuideFromTemplate(assistantName);
                if (guide != null)
                {
                    definition = guide;
                }
            }

            // If not a guide template, load from storage
            if (definition == null)
            {
                var storageMetadata = await GetAssistantComplete(assistantName);
                
                if (storageMetadata == null) return null;

                // Deserialize manifest with lenient options that allow integer enum values
                var lenientOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
                };
                definition = JsonSerializer.Deserialize<AssistantDefinition>(storageMetadata.ManifestJson, lenientOptions);
                
                if (definition != null)
                {
                    definition.Name = assistantName;
                    definition.Id = storageMetadata.Id;  // Set database ID if available
                    
                    // Set instructions
                    if (!string.IsNullOrWhiteSpace(storageMetadata.Instructions))
                    {
                        definition.Instructions = storageMetadata.Instructions;
                    }
                    
                    // Set context options
                    if (!string.IsNullOrWhiteSpace(storageMetadata.ContextOptionsJson))
                    {
                        try
                        {
                            var contextList = JsonSerializer.Deserialize<List<ContextOptionItem>>(storageMetadata.ContextOptionsJson);
                            var contextDict = contextList?.ToDictionary(i => i.key, i => i.value ?? string.Empty);
                            if (contextDict != null && contextDict.Any())
                            {
                                definition.ContextOptions = contextDict;
                            }
                        }
                        catch (Exception)
                        {
                            // swallow parse errors for now, could log
                        }
                    }

                    // Apply additional metadata from database (e.g., crew names)
                    if (storageMetadata.AdditionalMetadata != null)
                    {
                        if (definition.Metadata == null)
                        {
                            definition.Metadata = new Dictionary<string, string>();
                        }
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
                        catch (Exception)
                        {
                        }
                    }

                    updated = storageMetadata.Updated;
                }
            }

            if (definition == null) return null;

            // Add function tools (crew bridge, OpenAPI, vector stores, annotation-based)
            await AddFunctionTools(assistantName, definition);

            // Cache the definition
            var cachedEntry = new CachedAssistant(definition, updated, DateTime.UtcNow);
            AssistantDefinitionCache.AddOrUpdate(cacheKey, cachedEntry, (k, v) => cachedEntry);

            return definition;
        }

        /// <summary>
        /// Generates a cache key based on assistant name.
        /// </summary>
        private static string GenerateCacheKey(string assistantName)
        {
            return assistantName;
        }

        /// <summary>
        /// Checks if a cached entry is still valid by comparing timestamps with the database.
        /// </summary>
        private static async Task<bool> IsCacheValid(CachedAssistant cached, string assistantName)
        {
            // Check cache age
            if (DateTime.UtcNow - cached.CachedAt > CacheDuration)
            {
                // Cache expired by time, but check if database has been updated
                if (cached.Updated.HasValue)
                {
                    var metadata = await GetAssistantMetadata(assistantName);
                    if (metadata.HasValue)
                    {
                        // If database Updated timestamp is newer, cache is invalid
                        if (metadata.Value.Updated.HasValue && metadata.Value.Updated > cached.Updated)
                        {
                            return false;
                        }
                    }
                }
                // For time-based invalidation of database assistants with no updates, still consider valid.
                return cached.Updated.HasValue;
            }

            return true;
        }

        /// <summary>
        /// Clears a specific assistant from the cache.
        /// Useful when an assistant is updated and immediate invalidation is needed.
        /// </summary>
        /// <param name="assistantName">The name of the assistant to clear</param>
        public static void ClearCache(string assistantName)
        {
            var cacheKey = GenerateCacheKey(assistantName);
            AssistantDefinitionCache.TryRemove(cacheKey, out _);
        }

        /// <summary>
        /// Clears all cached assistants.
        /// Useful for testing or when bulk updates are made.
        /// </summary>
        public static void ClearAllCache()
        {
            AssistantDefinitionCache.Clear();
        }

        private static async Task AddFunctionTools(string assistantName, AssistantDefinition options)
        {
            // Add crew-bridge tools for Guide assistants
            if (options.Metadata != null && options.Metadata.TryGetValue("__crew_names__", out var crewNamesStr))
            {
                var crewNames = crewNamesStr.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (crewNames.Any())
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
                // Keep the metadata for EnsureRequestBuilderCache to use
            }

            // Add OpenAPI-based function tools
            var openApiToolDefinitions = await GetOpenApiToolDefinitions(assistantName);
            foreach (var toolDefinition in openApiToolDefinitions)
            {
                options.Tools!.Add(toolDefinition);
            }

            // Add SearchAssistantFiles tool if assistant has indexed content (vector stores)
            // This tool is annotation-based and will be injected via ToolContractRegistry
            var hasVectorStores = options.ToolResources?.FileSearch?.VectorStoreIds != null 
                && options.ToolResources.FileSearch.VectorStoreIds.Any();
            
            if (hasVectorStores)
            {
                // Check if SearchAssistantFiles is registered in ToolContractRegistry
                var allToolOperations = ToolContractRegistry.GetAllToolOperations();
                var searchAssistantFilesOperation = allToolOperations.FirstOrDefault(kvp => 
                    kvp.Key.Equals("SearchAssistantFiles", StringComparison.OrdinalIgnoreCase));
                
                if (!string.IsNullOrEmpty(searchAssistantFilesOperation.Key))
                {
                    try
                    {
                        var schema = ToolContractRegistry.GenerateOpenApiSchema(searchAssistantFilesOperation.Value);
                        var toolDefinitions = OpenApiHelper.GetToolDefinitionsFromJson(schema);
                        
                        foreach (var def in toolDefinitions)
                        {
                            if (def.Function?.AsObject?.Name == "SearchAssistantFiles")
                            {
                                options.Tools!.Add(def);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log warning but continue - non-critical failure
                        Console.WriteLine($"Warning: Failed to inject SearchAssistantFiles tool: {ex.Message}");
                    }
                }
            }

            // ---------------------------------------------------------------------
            // Process dynamic schema placeholders using annotation-driven discovery
            // ---------------------------------------------------------------------
            var requestedPlaceholders = options.Tools?
                .Where(t => t.Type != null)
                .ToList();

            if (requestedPlaceholders?.Any() == true)
            {
                var allToolOperations = ToolContractRegistry.GetAllToolOperations();
                var processedSchemas = new HashSet<string>();
                
                foreach (var placeholder in requestedPlaceholders.Where(p => p.Type != null))
                {
                    // Find the tool operation that matches this placeholder type
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
                                if (def.Function?.AsObject?.Name == matchingOperation.Key)
                                {
                                    options.Tools!.Add(def);
                                }
                            }
                            
                            processedSchemas.Add(matchingOperation.Value);
                        }
                        catch (Exception ex)
                        {
                            // Log warning but continue processing other tools
                            Console.WriteLine($"Warning: Failed to generate schema for {matchingOperation.Value}: {ex.Message}");
                        }
                    }
                }

                // Remove all placeholder entries that were successfully processed
                options.Tools = options.Tools!
                    .Where(t => t.Type == null || !allToolOperations.ContainsKey(t.Type!))
                    .ToList();
            }
        }

        // -----------------------------
        // Helper methods
        // -----------------------------
        
        /// <summary>
        /// Gets OpenAPI tool definitions for an assistant from database-backed metadata.
        /// </summary>
        private static async Task<List<ToolDefinition>> GetOpenApiToolDefinitions(string assistantName)
        {
            var toolDefinitions = new List<ToolDefinition>();

            var storageMetadata = await GetAssistantComplete(assistantName);
            if (storageMetadata?.OpenApiSchemas == null || !storageMetadata.OpenApiSchemas.Any())
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
        
        // -----------------------------
        // Helper methods for Guide load
        // -----------------------------
        private static async Task<AssistantDefinition?> TryBuildGuideFromTemplate(string assistantName)
        {
            // First, try to load the guide from database
            var storageMetadata = await GetAssistantComplete(assistantName);
            if (storageMetadata != null)
            {
                // Found in database, deserialize and return
                // Use lenient options that allow integer enum values from database
                var lenientOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
                };
                var definition = JsonSerializer.Deserialize<AssistantDefinition>(storageMetadata.ManifestJson, lenientOptions);
                if (definition != null)
                {
                    definition.Name = assistantName;
                    definition.Id = storageMetadata.Id;  // Set database ID if available
                    
                    // Set instructions
                    if (!string.IsNullOrWhiteSpace(storageMetadata.Instructions))
                    {
                        definition.Instructions = storageMetadata.Instructions;
                    }
                    
                    // Set context options
                    if (!string.IsNullOrWhiteSpace(storageMetadata.ContextOptionsJson))
                    {
                        try
                        {
                            var contextList = JsonSerializer.Deserialize<List<ContextOptionItem>>(storageMetadata.ContextOptionsJson);
                            var contextDict = contextList?.ToDictionary(i => i.key, i => i.value ?? string.Empty);
                            if (contextDict != null && contextDict.Any())
                            {
                                definition.ContextOptions = contextDict;
                            }
                        }
                        catch (Exception)
                        {
                            // Swallow context options parsing errors
                        }
                    }
                    
                    // Set additional metadata (includes __crew_names__ for guides)
                    if (storageMetadata.AdditionalMetadata != null && storageMetadata.AdditionalMetadata.Any())
                    {
                        if (definition.Metadata == null)
                        {
                            definition.Metadata = new Dictionary<string, string>(storageMetadata.AdditionalMetadata);
                        }
                        else
                        {
                            // Merge additional metadata into existing metadata
                            foreach (var kvp in storageMetadata.AdditionalMetadata)
                            {
                                definition.Metadata[kvp.Key] = kvp.Value;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(storageMetadata.SamplingParametersJson))
                    {
                        try
                        {
                            definition.SamplingParameters = JsonSerializer.Deserialize<Dictionary<string, double>>(
                                storageMetadata.SamplingParametersJson);
                        }
                        catch (Exception)
                        {
                        }
                    }
                    
                    return definition;
                }
            }

            return null;
        }

        private record ContextOptionItem(string key, string? value);
    }
}
