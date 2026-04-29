using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using AntRunner.ToolCalling.Functions;

namespace AntRunner.ToolCalling.AssistantDefinitions.Storage
{
    /// <summary>
    /// Loads assistant definitions from the database.
    /// </summary>
    public class DatabaseStorage
    {
        /// <summary>
        /// Creates a new ApplicationDbContext instance using connection string from environment.
        /// The connection string must be set in environment variables (done in Program.cs).
        /// </summary>
        private static ApplicationDbContext CreateContext()
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings:DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("Database connection string not found in environment variables. Ensure ConnectionStrings__DefaultConnection is set.");
            }

            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            optionsBuilder.UseSqlServer(connectionString);
            return new ApplicationDbContext(optionsBuilder.Options);
        }

        /// <summary>
        /// Gets avatar bytes and content type for an assistant.
        /// Returns null if not found in database.
        /// </summary>
        public static async Task<(byte[] Bytes, string ContentType)?> GetAssistantAvatarAsync(string assistantName, CancellationToken cancellationToken = default)
        {
            try
            {
                using var context = CreateContext();

                var avatar = await context.Assistants
                    .AsNoTracking()
                    .Where(a => a.Name == assistantName && a.IsActive)
                    .OrderBy(a => a.IsGlobal)
                    .Select(a => new { a.AvatarImageBytes, a.AvatarContentType })
                    .FirstOrDefaultAsync(cancellationToken);

                if (avatar?.AvatarImageBytes != null && !string.IsNullOrEmpty(avatar.AvatarContentType))
                {
                    return (avatar.AvatarImageBytes, avatar.AvatarContentType);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets conversation starters for an assistant.
        /// Returns null if not found in database.
        /// </summary>
        public static async Task<List<string>?> GetAssistantConversationStartersAsync(string assistantName, CancellationToken cancellationToken = default)
        {
            try
            {
                using var context = CreateContext();
                
                Guid? assistantId = null;

                assistantId = await context.Assistants
                    .AsNoTracking()
                    .Where(a => a.Name == assistantName && a.IsActive)
                    .OrderBy(a => a.IsGlobal)
                    .Select(a => a.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!assistantId.HasValue) return null;

                var starters = await context.AssistantConversationStarters
                    .AsNoTracking()
                    .Where(s => s.AssistantId == assistantId.Value)
                    .OrderBy(s => s.OrderIndex)
                    .Select(s => s.Prompt)
                    .ToListAsync(cancellationToken);

                return starters.Any() ? starters : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets assistant metadata from database (name, updated timestamp) for cache validation.
        /// </summary>
        public static async Task<(DateTime? Updated, Guid? Id)?> GetAssistantMetadata(string assistantName)
        {
            try
            {
                using var context = CreateContext();

                var assistant = await context.Assistants
                    .AsNoTracking()
                    .Where(a => a.Name == assistantName && a.IsActive)
                    .OrderBy(a => a.IsGlobal)
                    .Select(a => new { a.Updated, a.Id })
                    .FirstOrDefaultAsync();

                if (assistant != null)
                {
                    return (assistant.Updated, assistant.Id);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the full assistant definition from database and materializes it as AssistantDefinition.
        /// Returns metadata containing the manifest JSON, instructions, and updated timestamp.
        /// </summary>
        public static async Task<AssistantStorageMetadata?> GetAssistant(string assistantName)
        {
            try
            {
                using var context = CreateContext();
                
                var assistant = await context.Assistants
                    .AsNoTracking()
                    .Include(a => a.Model)
                    .Include(a => a.Tools).ThenInclude(t => t.Tool)
                    .Include(a => a.OpenApiSchemas).ThenInclude(s => s.Operations)
                    .Include(a => a.OpenApiSchemas).ThenInclude(s => s.AuthProvider)
                    .Include(a => a.Files)
                    .Include(a => a.ContextOptions)
                    .Include(a => a.ConversationStarters)
                    .Include(a => a.CrewMembers).ThenInclude(m => m.Assistant)
                    .Where(a => a.Name == assistantName && a.IsActive)
                    .OrderBy(a => a.IsGlobal)
                    .FirstOrDefaultAsync();

                if (assistant == null) return null;

                // Materialize to AssistantDefinition format
                return MaterializeAssistant(assistant);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves a requested reasoning effort against the authoritative model catalog row.
        /// </summary>
        public static async Task<string?> ResolveModelReasoningEffortAsync(
            string? modelId,
            string? reasoningEffort,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(reasoningEffort))
            {
                return null;
            }

            try
            {
                using var context = CreateContext();

                var reasoningChoicesJson = await context.Models
                    .AsNoTracking()
                    .Where(m => m.ModelId == modelId)
                    .Select(m => m.ReasoningChoicesJson)
                    .FirstOrDefaultAsync(cancellationToken);

                var requested = reasoningEffort.Trim();
                if (IsNoReasoningChoice(requested))
                {
                    return null;
                }

                var choices = ParseReasoningChoicesJson(reasoningChoicesJson);
                var matched = choices.FirstOrDefault(choice =>
                    string.Equals(choice, requested, StringComparison.OrdinalIgnoreCase));

                return IsNoReasoningChoice(matched) ? null : matched;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsNoReasoningChoice(string? choice)
        {
            return string.Equals(choice?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ParseReasoningChoicesJson(string? reasoningChoicesJson)
        {
            if (string.IsNullOrWhiteSpace(reasoningChoicesJson))
            {
                return [];
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(reasoningChoicesJson);
                return parsed?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToList()
                    ?? [];
            }
            catch
            {
                return [];
            }
        }

        /// <summary>
        /// Materializes a database Assistant entity to AssistantStorageMetadata format.
        /// </summary>
        private static AssistantStorageMetadata MaterializeAssistant(Assistant assistant)
        {
            var modelParameters = NormalizeAssistantModelParameters(assistant);

            // Build the manifest JSON
            var manifest = new
            {
                name = assistant.Name,
                description = assistant.Description,
                model = assistant.ModelId ?? assistant.Model?.ModelId,
                temperature = modelParameters.Temperature,
                top_p = modelParameters.TopP,
                reasoning_effort = modelParameters.ReasoningEffort,
                invocation_evaluator = assistant.InvocationEvaluator,
                tools = BuildToolsArray(assistant),
                tool_resources = BuildToolResources(assistant),
                metadata = ParseMetadataJson(assistant.MetadataJson)
            };

            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            // Extract crew names for metadata
            Dictionary<string, string>? additionalMetadata = null;
            if (assistant.Kind == AssistantKind.Guide && assistant.CrewMembers.Any())
            {
                var crewNames = assistant.CrewMembers
                    .OrderBy(m => m.DisplayOrder ?? int.MaxValue)
                    .Select(m => m.Assistant?.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                if (crewNames.Any())
                {
                    additionalMetadata = new Dictionary<string, string>
                    {
                        ["__crew_names__"] = string.Join(",", crewNames)
                    };
                }
            }

            // Build context options
            Dictionary<string, string>? contextOptions = null;
            if (assistant.ContextOptions.Any())
            {
                contextOptions = assistant.ContextOptions.ToDictionary(
                    co => co.Key,
                    co => co.Value ?? string.Empty
                );
            }

            return new AssistantStorageMetadata(
                ManifestJson: manifestJson,
                Instructions: assistant.Instructions,
                ContextOptionsJson: contextOptions != null 
                    ? JsonSerializer.Serialize(contextOptions.Select(kvp => new { key = kvp.Key, value = kvp.Value }))
                    : null,
                Updated: assistant.Updated,
                AdditionalMetadata: additionalMetadata,
                OpenApiSchemas: BuildOpenApiSchemas(assistant),
                VectorStoreFiles: BuildVectorStoreFiles(assistant),
                Id: assistant.Id,
                DomainAuth: BuildDomainAuth(assistant),
                SamplingParametersJson: modelParameters.SamplingParametersJson
            );
        }

        private static AssistantModelParameters NormalizeAssistantModelParameters(Assistant assistant)
        {
            var reasoningEffort = IsNoReasoningChoice(assistant.ReasoningEffort)
                ? null
                : assistant.ReasoningEffort;

            if (AssistantUsesLocalRuntime(assistant))
            {
                return new AssistantModelParameters(
                    assistant.Temperature,
                    assistant.TopP,
                    reasoningEffort,
                    assistant.SamplingParametersJson);
            }

            return new AssistantModelParameters(null, null, reasoningEffort, null);
        }

        private static bool AssistantUsesLocalRuntime(Assistant assistant)
        {
            return string.Equals(assistant.Model?.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(assistant.Model?.LocalRuntimeJson);
        }

        private static DomainAuth? BuildDomainAuth(Assistant assistant)
        {
            if (!assistant.OpenApiSchemas.Any()) return null;

            var domainAuth = new DomainAuth();

            foreach (var schema in assistant.OpenApiSchemas)
            {
                if (string.IsNullOrWhiteSpace(schema.ApiHost) || schema.AuthProvider == null) continue;

                var host = NormalizeApiAuthority(schema.ApiHost);

                var ap = schema.AuthProvider;
                var cfg = new ActionAuthConfig
                {
                    AuthType = ap.AuthType switch
                    {
                        "service_http" => AuthType.service_http,
                        "service_query" => AuthType.service_query,
                        "oauth" => AuthType.oauth,
                        "azure_oauth" => AuthType.azure_oauth,
                        _ => AuthType.none
                    },
                    // Reuse HeaderKey for both header and query auth (key name semantics)
                    HeaderKey = ap.HeaderName
                };

                if (cfg.AuthType == AuthType.service_http || cfg.AuthType == AuthType.service_query)
                {
                    if (assistant.IsGlobal)
                    {
                        cfg.HeaderValueEnvironmentVariable = ap.ValueTemplate;
                    }
                    else
                    {
                        cfg.HeaderValueLiteral = ap.ValueTemplate;
                    }
                }

                domainAuth.HostAuthorizationConfigurations[host] = cfg;
            }

            return domainAuth.HostAuthorizationConfigurations.Count > 0 ? domainAuth : null;
        }

        private static string NormalizeApiAuthority(string apiHost)
        {
            if (string.IsNullOrWhiteSpace(apiHost))
            {
                return apiHost;
            }

            var trimmed = apiHost.Trim();

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
            {
                if (!string.IsNullOrWhiteSpace(absoluteUri.Authority))
                {
                    return absoluteUri.Authority;
                }
            }

            if (!trimmed.Contains("://", StringComparison.Ordinal) &&
                Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out var withScheme))
            {
                if (!string.IsNullOrWhiteSpace(withScheme.Authority))
                {
                    return withScheme.Authority;
                }
            }

            return trimmed;
        }

        private static List<object> BuildToolsArray(Assistant assistant)
        {
            var tools = new List<object>();

            // Add tools from AssistantTool join table
            foreach (var assistantTool in assistant.Tools)
            {
                if (assistantTool.Tool?.ToolType != null)
                {
                    tools.Add(new { type = assistantTool.Tool.ToolType });
                }
            }

            // Add code_interpreter if there are code interpreter files
            if (assistant.Files.Any(f => f.FolderKind == "CodeInterpreter"))
            {
                tools.Add(new { type = "code_interpreter" });
            }

            // Add file_search if there are vector store files
            if (assistant.Files.Any(f => f.FolderKind == "VectorStore"))
            {
                tools.Add(new { type = "file_search" });
            }

            return tools;
        }

        private static object? BuildToolResources(Assistant assistant)
        {
            if (!string.IsNullOrWhiteSpace(assistant.ToolResourcesJson))
            {
                try
                {
                    return JsonSerializer.Deserialize<object>(assistant.ToolResourcesJson);
                }
                catch
                {
                    // Invalid JSON, ignore
                }
            }

            // Build tool resources from files if needed
            var vectorStoreFiles = assistant.Files.Where(f => f.FolderKind == "VectorStore").ToList();
            if (vectorStoreFiles.Any())
            {
                var vectorStoreIds = vectorStoreFiles
                    .Select(f => f.VectorStoreName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .ToList();

                if (vectorStoreIds.Any())
                {
                    return new
                    {
                        file_search = new
                        {
                            vector_store_ids = vectorStoreIds
                        }
                    };
                }
            }

            return null;
        }

        private static Dictionary<string, string>? ParseMetadataJson(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson)) return null;

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string>? BuildOpenApiSchemas(Assistant assistant)
        {
            if (!assistant.OpenApiSchemas.Any()) return null;

            var schemas = new Dictionary<string, string>();
            foreach (var schema in assistant.OpenApiSchemas)
            {
                if (!string.IsNullOrWhiteSpace(schema.SpecificationJson))
                {
                    var fileName = $"{schema.ApiHost?.Replace("https://", "").Replace("http://", "").Replace("/", "_") ?? "schema"}.json";
                    schemas[fileName] = schema.SpecificationJson;
                }
            }

            return schemas.Any() ? schemas : null;
        }

        private static Dictionary<string, byte[]>? BuildVectorStoreFiles(Assistant assistant)
        {
            var vectorFiles = assistant.Files.Where(f => f.FolderKind == "VectorStore").ToList();
            if (!vectorFiles.Any()) return null;

            var files = new Dictionary<string, byte[]>();
            foreach (var file in vectorFiles)
            {
                if (file.ContentBytes != null && !string.IsNullOrWhiteSpace(file.VectorStoreName) && !string.IsNullOrWhiteSpace(file.RelativePath))
                {
                    var key = $"{file.VectorStoreName}/{file.RelativePath}";
                    files[key] = file.ContentBytes;
                }
            }

            return files.Any() ? files : null;
        }
    }

    /// <summary>
    /// Represents assistant metadata loaded from database.
    /// </summary>
    public record AssistantStorageMetadata(
        string ManifestJson,
        string? Instructions,
        string? ContextOptionsJson,
        DateTime? Updated,
        Dictionary<string, string>? AdditionalMetadata = null,
        Dictionary<string, string>? OpenApiSchemas = null,
        Dictionary<string, byte[]>? VectorStoreFiles = null,
        Guid? Id = null,
        DomainAuth? DomainAuth = null,
        string? SamplingParametersJson = null
    );

    internal sealed record AssistantModelParameters(
        float? Temperature,
        double? TopP,
        string? ReasoningEffort,
        string? SamplingParametersJson);
}

