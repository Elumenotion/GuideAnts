using System.Collections.Concurrent;
using AntRunner.ToolCalling.Functions;
using static AntRunner.ToolCalling.AssistantDefinitions.Storage.AssistantDefinitionFiles;
using AntRunner.ToolCalling.AssistantDefinitions;
using System.Text.Json.Serialization;
using System.Text;
using AntRunner.ToolCalling;

namespace AntRunner.Chat
{
    /// <summary>
    /// Fetch and autoCreate assistants with caching and database support.
    /// 
    /// Loading Priority:
    /// 1. Database assistants
    /// 2. Template-based Guides (NotebookTemplates/)
    /// 3. File-based assistants (AssistantDefinitions/)
    /// </summary>
    public static class AssistantUtility
    {
        private static bool IsFileFallbackDisabled()
        {
            var raw = Environment.GetEnvironmentVariable("ASSISTANTS_DISABLE_FILE_FALLBACK");
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (bool.TryParse(raw, out var parsed)) return parsed;
            return raw.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

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
        /// Reads the assistant definition from database, templates, or file storage.
        /// Supports database and file-based assistants.
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
                    // Guides may come from database (with updated timestamp) or file-based templates
                }
            }

            // If not a guide template, load from storage (database or file-based)
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
                // For time-based invalidation of database assistants with no updates, still consider valid
                // For file-based assistants (no timestamp), accept cache staleness after duration
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
        /// Gets OpenAPI tool definitions for an assistant, checking database first then falling back to file system.
        /// </summary>
        private static async Task<List<ToolDefinition>> GetOpenApiToolDefinitions(string assistantName)
        {
            var toolDefinitions = new List<ToolDefinition>();
            
            // Try database first
            var storageMetadata = await GetAssistantComplete(assistantName);
            if (storageMetadata != null)
            {
                if (storageMetadata.OpenApiSchemas != null && storageMetadata.OpenApiSchemas.Any())
                {
                    // Database has the schemas as Dictionary<filename, content>
                    foreach (var kvp in storageMetadata.OpenApiSchemas)
                    {
                        var defs = OpenApiHelper.GetToolDefinitionsFromJson(kvp.Value);
                        toolDefinitions.AddRange(defs);
                    }
                }

                if (IsFileFallbackDisabled())
                {
                    return toolDefinitions;
                }
            }

            if (IsFileFallbackDisabled())
            {
                return toolDefinitions;
            }
            
            // Fall back to file system (returns file paths)
            var openApiSchemaFiles = await GetFilesInOpenApiFolder(assistantName);
            if (openApiSchemaFiles != null && openApiSchemaFiles.Any())
            {
                toolDefinitions = await OpenApiHelper.GetToolDefinitionsFromOpenApiSchemaFiles(openApiSchemaFiles);
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

            // Not found in database, fall back to file-based template storage
            if (IsFileFallbackDisabled())
            {
                return null;
            }

            var templatesRoot = ResolveTemplatesRoot();
            if (templatesRoot == null || !Directory.Exists(templatesRoot))
            {
                // No file-based templates available, return null (not found)
                return null;
            }

            // Consider both the full assistantName (may already end with Guide) and stripped name
            static string StripGuide(string name) => name.EndsWith(" Guide", StringComparison.OrdinalIgnoreCase)
                ? name[..^6] : name;

            var candidates = new[] { assistantName, StripGuide(assistantName) };
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.Combine(templatesRoot, candidate);
                if (!Directory.Exists(dir)) continue;

                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    throw new FileNotFoundException("Template manifest.json not found for guide", manifestPath);
                }

                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<TemplateManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (manifest == null)
                {
                    throw new InvalidDataException($"Failed to parse template manifest: {manifestPath}");
                }

                // Determine instructions: manifest.Instructions > instructions.md; otherwise fail fast
                string? instructions = manifest.Instructions;
                if (string.IsNullOrWhiteSpace(instructions))
                {
                    var instructionsPath = Path.Combine(dir, "instructions.md");
                    if (File.Exists(instructionsPath))
                    {
                        instructions = File.ReadAllText(instructionsPath, Encoding.UTF8);
                    }
                }
                if (string.IsNullOrWhiteSpace(instructions))
                {
                    throw new InvalidOperationException($"Guide instructions not found. Provide 'instructions' in manifest or an instructions.md in: {dir}");
                }

                // Build assistant definition from template manifest properties
                var def = new AssistantDefinition
                {
                    Name = assistantName,
                    Description = manifest.Description ?? $"Your intelligent guide for {candidate} workflows",
                    Instructions = instructions,
                    InvocationEvaluator = manifest.InvocationEvaluator,
                    Tools = manifest.Tools ?? new List<ToolDefinition>(),
                    ToolResources = manifest.ToolResources,
                    TopP = manifest.TopP,
                    Metadata = manifest.Metadata,
                    Model = manifest.Model ?? manifest.DefaultModel,
                    Temperature = manifest.Temperature,
                    ReasoningEffort = manifest.ReasoningEffort
                };

                // Load context options from template if they exist
                var contextOptionsPath = Path.Combine(dir, "HostExtensions", "UI", "contextOptions.json");
                if (File.Exists(contextOptionsPath))
                {
                    try
                    {
                        var contextJson = File.ReadAllText(contextOptionsPath, Encoding.UTF8);
                        var contextList = JsonSerializer.Deserialize<List<ContextOptionItem>>(contextJson);
                        if (contextList != null && contextList.Any())
                        {
                            def.ContextOptions = contextList.ToDictionary(i => i.key, i => i.value ?? string.Empty);
                        }
                    }
                    catch (Exception)
                    {
                        // Swallow context options parsing errors - guide can still work without them
                    }
                }

                // Store crew names in metadata for processing in AddFunctionTools
                if (manifest.Crew != null && manifest.Crew.Count > 0)
                {
                    var crewNames = manifest.Crew.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    if (crewNames.Count == 0)
                    {
                        throw new InvalidOperationException($"Template '{candidate}' has an empty crew list.");
                    }
                    // Store crew names in metadata so AddFunctionTools can process them
                    if (def.Metadata == null) def.Metadata = new Dictionary<string, string>();
                    def.Metadata["__crew_names__"] = string.Join(",", crewNames);
                }

                return def;
            }

            // Guide not found in database or file-based templates
            return null;
        }

        private static string? ResolveTemplatesRoot()
        {
            // 1) Environment variable overrides
            // Primary explicit var used across the server
            var envVar = Environment.GetEnvironmentVariable("NOTEBOOK_TEMPLATES_BASE_FOLDER_PATH");
            if (!string.IsNullOrWhiteSpace(envVar))
            {
                var full = Path.GetFullPath(envVar, AppContext.BaseDirectory);
                if (Directory.Exists(full)) return full;
                if (Directory.Exists(envVar)) return envVar; // already absolute
            }

            // Server publishes configuration keys into environment variables at startup
            // so we can also read the config key directly
            var configKeyVar = Environment.GetEnvironmentVariable("NotebookTemplates:BaseFolderPath");
            if (!string.IsNullOrWhiteSpace(configKeyVar))
            {
                var full = Path.GetFullPath(configKeyVar, AppContext.BaseDirectory);
                if (Directory.Exists(full)) return full;
                if (Directory.Exists(configKeyVar)) return configKeyVar;
            }

            // 2) Common relative locations from current base directory
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "NotebookTemplates"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "NotebookTemplates")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "NotebookTemplates")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "NotebookTemplates"))
            };
            foreach (var c in candidates)
            {
                if (Directory.Exists(c)) return c;
            }

            return null;
        }

        // Minimal manifest backing type (subset plus assistant-like fields)
        private class TemplateManifest
        {
            public string? Name { get; set; }
            public string? Description { get; set; }
            public string? DefaultAssistant { get; set; }
            public string? Instructions { get; set; }
            public string? InvocationEvaluator { get; set; }
            public List<ToolDefinition>? Tools { get; set; }
            public ToolResources? ToolResources { get; set; }
            public double? TopP { get; set; }
            public Dictionary<string, string>? Metadata { get; set; }
            public string? Model { get; set; }
            public string? DefaultModel { get; set; }
            public float? Temperature { get; set; }
            public string? ReasoningEffort { get; set; }
            public List<TemplateCrewMember> Crew { get; set; } = new();
        }

        private class TemplateCrewMember
        {
            public string Name { get; set; } = string.Empty;
        }

        private record ContextOptionItem(string key, string? value);
    }
}
