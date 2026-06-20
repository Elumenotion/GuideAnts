using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using AntRunner.ToolCalling.Functions;

namespace GuideAntsApi.Services.Guides;

public class GuideExportImportService : IGuideExportImportService
{
    private readonly ApplicationDbContext _context;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly BackgroundJobs.IJobQueueService _jobQueue;

    public GuideExportImportService(
        ApplicationDbContext context,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        BackgroundJobs.IJobQueueService jobQueue)
    {
        _context = context;
        _dbFactory = dbFactory;
        _jobQueue = jobQueue;
    }

    /// <summary>
    /// Extracts distinct auth providers from OpenAPI schemas
    /// </summary>
    private static List<AssistantAuthProvider> GetDistinctAuthProviders(IEnumerable<AssistantOpenApiSchema> schemas)
    {
        return schemas
            .Where(s => s.AuthProvider != null)
            .Select(s => s.AuthProvider!)
            .DistinctBy(ap => ap.ProviderId)
            .ToList();
    }

    /// <summary>
    /// Builds auth provider object in template manifest format
    /// </summary>
    private static Dictionary<string, object?> BuildTemplateAuthProvider(AssistantAuthProvider ap)
    {
        var provider = new Dictionary<string, object?>
        {
            ["id"] = ap.ProviderId,
            ["authType"] = ap.AuthType
        };

        const string MASK_SENTINEL = "••••••••";

        if (ap.AuthType == "oauth" || ap.AuthType == "azure_oauth")
        {
            if (!string.IsNullOrEmpty(ap.ClientId))
                provider["clientId"] = ap.ClientId;
            if (!string.IsNullOrEmpty(ap.Tenant))
                provider["tenant"] = ap.Tenant;
            if (!string.IsNullOrEmpty(ap.ValueTemplate))
                provider["valueTemplate"] = ap.ValueTemplate;
            if (!string.IsNullOrEmpty(ap.UserConfigPolicy))
                provider["userConfigPolicy"] = ap.UserConfigPolicy;
            if (ap.Scopes.Any())
                provider["scopes"] = ap.Scopes.Select(s => s.Scope).ToArray();
        }
        else if (ap.AuthType == "service_http")
        {
            if (!string.IsNullOrEmpty(ap.HeaderName))
                provider["headerName"] = ap.HeaderName;
            // Mask owner-scoped values (non-global assistants pass masked template at export call sites)
            provider["valueTemplate"] = MASK_SENTINEL;
            if (!string.IsNullOrEmpty(ap.UserConfigPolicy))
                provider["userConfigPolicy"] = ap.UserConfigPolicy;
        }

        return provider;
    }

    /// <summary>
    /// Builds auth provider object in migration tool auth.json format (for OpenAPI/auth.json)
    /// </summary>
    private static Dictionary<string, object?> BuildMigrationAuthProvider(AssistantAuthProvider ap)
    {
        var hostConfig = new Dictionary<string, object?>
        {
            ["auth_type"] = ap.AuthType
        };

        const string MASK_SENTINEL = "••••••••";
        
        if (!string.IsNullOrEmpty(ap.ClientId))
            hostConfig["client_id"] = ap.ClientId;
        if (!string.IsNullOrEmpty(ap.Tenant))
            hostConfig["tenant"] = ap.Tenant;
        if (!string.IsNullOrEmpty(ap.HeaderName))
            hostConfig["header_name"] = ap.HeaderName;
        if (ap.AuthType == "service_http")
        {
            // Always mask for owner-scoped assistants; callers ensure we export only non-global assistants here
            hostConfig["header_value_env_var"] = MASK_SENTINEL;
        }
        if (!string.IsNullOrEmpty(ap.UserConfigPolicy))
            hostConfig["user_config_policy"] = ap.UserConfigPolicy;
        if (ap.Scopes.Any())
            hostConfig["scopes"] = ap.Scopes.Select(s => s.Scope).ToArray();
        
        return hostConfig;
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes a text file to the archive if content is not empty
    /// </summary>
    private static async Task WriteTextFileAsync(ZipArchive archive, string entryPath, string? content)
    {
        if (string.IsNullOrEmpty(content))
            return;

        var entry = archive.CreateEntry(entryPath);
        using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(content);
    }

    /// <summary>
    /// Writes a JSON file to the archive
    /// </summary>
    private static async Task WriteJsonFileAsync(ZipArchive archive, string entryPath, object data)
    {
        var entry = archive.CreateEntry(entryPath);
        using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(JsonSerializer.Serialize(data, IndentedJsonOptions));
    }

    /// <summary>
    /// Writes binary data to the archive if not empty
    /// </summary>
    private static async Task WriteBinaryFileAsync(ZipArchive archive, string entryPath, byte[]? data)
    {
        if (data == null || data.Length == 0)
            return;

        var entry = archive.CreateEntry(entryPath);
        using var stream = entry.Open();
        await stream.WriteAsync(data);
    }

    /// <summary>
    /// Writes avatar image to archive
    /// </summary>
    private static async Task WriteAvatarAsync(ZipArchive archive, string basePath, byte[]? avatarBytes, string? contentType)
    {
        if (avatarBytes == null)
            return;

        var extension = contentType?.Split('/').Last() ?? "png";
        await WriteBinaryFileAsync(archive, NormalizePath(basePath, $"HostExtensions/UI/avatar.{extension}"), avatarBytes);
    }

    /// <summary>
    /// Writes conversation starters to archive
    /// </summary>
    private static async Task WriteConversationStartersAsync(ZipArchive archive, string basePath, IEnumerable<AssistantConversationStarter> starters)
    {
        if (!starters.Any())
            return;

        var starterList = starters
            .OrderBy(cs => cs.OrderIndex)
            .Select(cs => cs.Prompt)
            .ToArray();

        await WriteJsonFileAsync(archive, NormalizePath(basePath, "HostExtensions/UI/conversationStarters.json"), starterList);
    }

    /// <summary>
    /// Writes context options to archive
    /// </summary>
    private static async Task WriteContextOptionsAsync(ZipArchive archive, string basePath, IEnumerable<AssistantContextOption> options)
    {
        if (!options.Any())
            return;

        var optionsList = options.Select(co => new { key = co.Key, value = co.Value }).ToArray();
        await WriteJsonFileAsync(archive, NormalizePath(basePath, "HostExtensions/UI/contextOptions.json"), optionsList);
    }

    /// <summary>
    /// Writes OpenAPI schemas and auth.json to archive
    /// </summary>
    private static async Task WriteOpenApiSchemasAsync(ZipArchive archive, string basePath, IEnumerable<AssistantOpenApiSchema> schemas)
    {
        var schemaList = schemas.ToList();
        if (!schemaList.Any())
            return;

        // Write auth.json (migration tool format)
        var authProviders = GetDistinctAuthProviders(schemaList);
        if (authProviders.Any())
        {
            var hosts = authProviders.ToDictionary(
                ap => ap.ProviderId,
                ap => BuildMigrationAuthProvider(ap) as object
            );
            
            var authData = new Dictionary<string, object> { ["hosts"] = hosts };
            await WriteJsonFileAsync(archive, NormalizePath(basePath, "OpenAPI/auth.json"), authData);
        }

        // Write OpenAPI schema specification files
        foreach (var schema in schemaList)
        {
            await WriteTextFileAsync(archive, NormalizePath(basePath, $"OpenAPI/{schema.Name}.json"), schema.SpecificationJson);
        }
    }

    /// <summary>
    /// Writes assistant files (VectorStores and CodeInterpreter) to archive
    /// </summary>
    private static async Task WriteAssistantFilesAsync(ZipArchive archive, string basePath, IEnumerable<AssistantFile> files)
    {
        foreach (var file in files)
        {
            if (file.ContentBytes == null || file.ContentBytes.Length == 0)
                continue;

            string entryPath;
            if (file.FolderKind == "VectorStore" && !string.IsNullOrEmpty(file.VectorStoreName))
            {
                entryPath = NormalizePath(basePath, $"VectorStores/{file.VectorStoreName}/{Path.GetFileName(file.RelativePath)}");
            }
            else if (file.FolderKind == "CodeInterpreter")
            {
                entryPath = NormalizePath(basePath, $"CodeInterpreter/{Path.GetFileName(file.RelativePath)}");
            }
            else
            {
                entryPath = NormalizePath(basePath, file.RelativePath);
            }

            await WriteBinaryFileAsync(archive, entryPath, file.ContentBytes);
        }
    }

    /// <summary>
    /// Normalizes a path by combining basePath and relativePath, avoiding leading slashes
    /// </summary>
    private static string NormalizePath(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return relativePath;
        return $"{basePath}/{relativePath}";
    }

    /// <summary>
    /// Exports a complete assistant to a specific path in the archive
    /// </summary>
    private static async Task ExportAssistantToArchiveAsync(ZipArchive archive, string basePath, Assistant assistant)
    {
        // Manifest - use AssistantDefinition for proper serialization with all attributes
        var assistantDef = new AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition
        {
            Name = assistant.Name,
            Description = assistant.Description,
            Model = assistant.Model?.ModelId,
            Temperature = assistant.Temperature,
            TopP = assistant.TopP,
            ReasoningEffort = assistant.ReasoningEffort,
            InvocationEvaluator = assistant.InvocationEvaluator,
            Tools = assistant.Tools.Select(t => new AntRunner.ToolCalling.AssistantDefinitions.ToolDefinition 
            { 
                Type = t.Tool.ToolType 
            }).ToList()
        };
        
        var manifestOptions = new JsonSerializerOptions
        { 
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var manifestNode = JsonSerializer.SerializeToNode(assistantDef, manifestOptions)?.AsObject()
            ?? new JsonObject();

        await WriteTextFileAsync(archive, NormalizePath(basePath, "manifest.json"), manifestNode.ToJsonString(manifestOptions));

        // Instructions
        await WriteTextFileAsync(archive, NormalizePath(basePath, "instructions.md"), assistant.Instructions);

        // Avatar
        await WriteAvatarAsync(archive, basePath, assistant.AvatarImageBytes, assistant.AvatarContentType);

        // Conversation Starters
        await WriteConversationStartersAsync(archive, basePath, assistant.ConversationStarters);

        // Context Options
        await WriteContextOptionsAsync(archive, basePath, assistant.ContextOptions);

        // OpenAPI Schemas and Auth
        await WriteOpenApiSchemasAsync(archive, basePath, assistant.OpenApiSchemas);

        // Assistant Files
        await WriteAssistantFilesAsync(archive, basePath, assistant.Files);
    }

    public async Task<byte[]> ExportGuideAsync(Guid guideId)
    {

        var guide = await _context.Assistants
            .Include(a => a.Model)
            .Include(a => a.Tools).ThenInclude(t => t.Tool)
            .Include(a => a.ContextOptions)
            .Include(a => a.OpenApiSchemas).ThenInclude(s => s.AuthProvider).ThenInclude(ap => ap!.Scopes)
            .Include(a => a.Files)
            .Include(a => a.ConversationStarters)
            // Include full data for crew member assistants
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant).ThenInclude(a => a.Model)
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant).ThenInclude(a => a.Tools).ThenInclude(t => t.Tool)
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant).ThenInclude(a => a.ContextOptions)
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant).ThenInclude(a => a.OpenApiSchemas).ThenInclude(s => s.AuthProvider).ThenInclude(ap => ap!.Scopes)
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant).ThenInclude(a => a.Files)
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant).ThenInclude(a => a.ConversationStarters)
            .Where(a => a.Id == guideId && a.Kind == AssistantKind.Guide)
            .FirstOrDefaultAsync();

        if (guide == null)
        {
            throw new InvalidOperationException("Guide not found");
        }

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            // Create manifest.json (Guide format with crew, tools, and auth)
            var manifestData = new Dictionary<string, object?>
            {
                ["name"] = guide.Name,
                ["description"] = guide.Description,
                ["defaultModel"] = guide.Model?.ModelId,
                ["defaultAssistant"] = guide.DefaultAssistant
            };

            if (!string.IsNullOrWhiteSpace(guide.InvocationEvaluator))
            {
                manifestData["invocationEvaluator"] = guide.InvocationEvaluator;
            }

            // Add tools if present
            if (guide.Tools.Any())
            {
                manifestData["tools"] = guide.Tools
                    .Select(t => new { type = t.Tool.ToolType })
                    .ToArray();
            }

            // Add crew
            manifestData["crew"] = guide.CrewMembers
                .OrderBy(ga => ga.DisplayOrder)
                .Select(ga => new { name = ga.Assistant.Name })
                .ToArray();

            // Add auth config if present (stored as JSON)
            if (!string.IsNullOrEmpty(guide.AuthConfigJson))
            {
                var authConfig = JsonSerializer.Deserialize<JsonElement>(guide.AuthConfigJson);
                manifestData["auth"] = authConfig;
            }

            await WriteJsonFileAsync(archive, "manifest.json", manifestData);
            await WriteTextFileAsync(archive, "instructions.md", guide.Instructions);
            await WriteTextFileAsync(archive, "HostExtensions/UI/home.md", guide.HomePageMarkdown);
            await WriteAvatarAsync(archive, "", guide.AvatarImageBytes, guide.AvatarContentType);
            await WriteConversationStartersAsync(archive, "", guide.ConversationStarters);
            await WriteContextOptionsAsync(archive, "", guide.ContextOptions);
            // Write only the guide's own OpenAPI schemas (crew schemas are in their own assistants/ folders)
            await WriteOpenApiSchemasAsync(archive, "", guide.OpenApiSchemas);
            await WriteAssistantFilesAsync(archive, "", guide.Files);

            // Note: Crew is included in the manifest.json above, no separate crews/ directory needed

            var customAssistants = guide.CrewMembers
                .Select(ga => ga.Assistant)
                .Where(a => a.Kind == AssistantKind.Assistant)
                .ToList();

            foreach (var assistant in customAssistants)
            {
                await ExportAssistantToArchiveAsync(archive, $"assistants/{assistant.Name}", assistant);
            }
        }

        memoryStream.Seek(0, SeekOrigin.Begin);
        return memoryStream.ToArray();
    }

    public async Task<ImportPreviewDto> PreviewImportAsync(Stream zipStream)
    {

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true);
        
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry == null)
        {
            throw new InvalidOperationException("Invalid export: manifest.json not found");
        }

        using var manifestReader = new StreamReader(manifestEntry.Open());
        var manifestJson = await manifestReader.ReadToEndAsync();
        var manifest = JsonSerializer.Deserialize<JsonElement>(manifestJson);
        
        var guideName = manifest.GetProperty("name").GetString() ?? "Unknown";
        
        // Collect all names to check for conflicts
        var namesToCheck = new List<string> { guideName };
        
        // Count crews from manifest (they're just references, not embedded)
        int crewCount = 0;
        if (manifest.TryGetProperty("crew", out var crewElement) && crewElement.ValueKind == JsonValueKind.Array)
        {
            crewCount = crewElement.GetArrayLength();
        }

        // Parse custom assistant manifests and collect their names
        var assistantEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("assistants/") && e.FullName.EndsWith("/manifest.json"))
            .ToList();
        
        foreach (var assistantEntry in assistantEntries)
        {
            using var assistantReader = new StreamReader(assistantEntry.Open());
            var assistantJson = await assistantReader.ReadToEndAsync();
            var assistantManifest = JsonSerializer.Deserialize<JsonElement>(assistantJson);
            
            var assistantName = assistantManifest.GetProperty("name").GetString();
            if (!string.IsNullOrEmpty(assistantName))
            {
                namesToCheck.Add(assistantName);
            }
        }

        // Check database for name conflicts
        var conflicts = await _context.Assistants
            .Where(a => namesToCheck.Contains(a.Name))
            .Select(a => a.Name)
            .ToListAsync();

        return new ImportPreviewDto(
            guideName,
            crewCount,
            assistantEntries.Count,
            conflicts
        );
    }

    public async Task<ImportGuideResultDto> ImportGuideAsync(Stream zipStream)
    {

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true);
        
        // Parse guide manifest
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry == null)
        {
            throw new InvalidOperationException("Invalid guide export: manifest.json not found");
        }

        using var manifestReader = new StreamReader(manifestEntry.Open());
        var manifestJson = await manifestReader.ReadToEndAsync();
        var manifest = JsonSerializer.Deserialize<JsonElement>(manifestJson);
        
        var guideName = manifest.GetProperty("name").GetString();
        if (string.IsNullOrEmpty(guideName))
        {
            throw new InvalidOperationException("Guide name is required");
        }

        // Determine if this is a create or update operation
        var existing = await _context.Assistants
            .FirstOrDefaultAsync(a => a.Name == guideName && a.Kind == AssistantKind.Guide);
        var isUpdate = existing != null;

        var warnings = new List<string>();
        var customAssistantIds = new Dictionary<string, Guid>(); // name -> ID mapping
        var newlyCreatedAssistantIds = new List<Guid>(); // track only new assistants
        Guid guideId = Guid.Empty;
        int customAssistantsCreated = 0;
        int crewsCreated = 0;
        int globalLinked = 0;
        
        // Use execution strategy to handle retry logic
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Step 1: Import custom assistants first
            var assistantEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("assistants/") && e.FullName.EndsWith("/manifest.json"))
                .ToList();

            foreach (var assistantEntry in assistantEntries)
            {
                var assistantBasePath = Path.GetDirectoryName(assistantEntry.FullName)?.Replace('\\', '/');
                
                using var assistantReader = new StreamReader(assistantEntry.Open());
                var assistantJson = await assistantReader.ReadToEndAsync();
                
                // Parse just the name for validation
                var assistantElement = JsonSerializer.Deserialize<JsonElement>(assistantJson);
                var assistantName = assistantElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                
                if (string.IsNullOrEmpty(assistantName))
                {
                    warnings.Add($"Skipped assistant with no name in {assistantEntry.FullName}");
                    continue;
                }

                // Check if assistant with this name already exists
                var existingAssistant = await _context.Assistants
                    .FirstOrDefaultAsync(a => a.Name == assistantName);
                
                if (existingAssistant != null)
                {
                    // Use existing assistant
                    customAssistantIds[assistantName] = existingAssistant.Id;
                    warnings.Add($"Assistant '{assistantName}' already exists, using existing");
                    continue;
                }

                // Import new assistant
                var assistant = await ImportAssistantFromArchiveAsync(archive, assistantBasePath ?? "", assistantJson, warnings);
                _context.Assistants.Add(assistant);
                await _context.SaveChangesAsync(); // Save to get ID
                
                customAssistantIds[assistantName] = assistant.Id;
                newlyCreatedAssistantIds.Add(assistant.Id);
            }

            // Step 2: Create or update the guide
            var description = manifest.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
            var defaultAssistant = manifest.TryGetProperty("defaultAssistant", out var daProp) ? daProp.GetString() : null;
            var invocationEvaluator = manifest.TryGetProperty("invocationEvaluator", out var ieProp) ? ieProp.GetString() : null;
            
            var modelId = manifest.TryGetProperty("defaultModel", out var modelProp)
                ? await ResolveImportedModelIdOrWarnAsync(modelProp.GetString(), $"guide '{guideName}'", warnings)
                : null;

            Assistant guide;
            if (isUpdate)
            {
                // Load the existing guide for update
                guide = await _context.Assistants
                    .FirstAsync(a => a.Id == existing!.Id && a.Kind == AssistantKind.Guide);

                // Preserve IsGlobal and Created; update mutable fields only
                guide.Description = description ?? "";
                guide.DefaultAssistant = defaultAssistant;
                guide.InvocationEvaluator = invocationEvaluator;
                guide.ModelId = modelId;
                guide.Updated = DateTime.UtcNow;

                // Wipe and replace dependent collections
                await _context.AssistantContextOptions.Where(o => o.AssistantId == guide.Id).ExecuteDeleteAsync();
                await _context.AssistantConversationStarters.Where(c => c.AssistantId == guide.Id).ExecuteDeleteAsync();
                await _context.AssistantTools.Where(t => t.AssistantId == guide.Id).ExecuteDeleteAsync();
                await _context.GuideMembers.Where(m => m.GuideId == guide.Id).ExecuteDeleteAsync();
                await _context.AssistantOpenApiSchemas.Where(s => s.AssistantId == guide.Id).ExecuteDeleteAsync(); // cascades operations
                await _context.AssistantFiles.Where(f => f.AssistantId == guide.Id).ExecuteDeleteAsync();
            }
            else
            {
                guide = new Assistant
                {
                    Id = Guid.NewGuid(),
                    Kind = AssistantKind.Guide,
                    Name = guideName,
                    Description = description ?? "",
                    DefaultAssistant = defaultAssistant,
                    InvocationEvaluator = invocationEvaluator,
                    ModelId = modelId,
                    Created = DateTime.UtcNow
                };
            }

            // Store auth config as JSON if present
            if (manifest.TryGetProperty("auth", out var authElement))
            {
                guide.AuthConfigJson = authElement.GetRawText();
            }

            // Import guide instructions
            var instructionsEntry = archive.GetEntry("instructions.md");
            if (instructionsEntry != null)
            {
                using var reader = new StreamReader(instructionsEntry.Open());
                guide.Instructions = await reader.ReadToEndAsync();
            }

            // Import home page
            var homeEntry = archive.GetEntry("HostExtensions/UI/home.md");
            if (homeEntry != null)
            {
                using var reader = new StreamReader(homeEntry.Open());
                guide.HomePageMarkdown = await reader.ReadToEndAsync();
            }

            // Import avatar
            var avatarEntry = archive.Entries.FirstOrDefault(e => 
                e.FullName.StartsWith("HostExtensions/UI/avatar.") && 
                !e.FullName.EndsWith("/"));
            
            if (avatarEntry != null)
            {
                using var avatarStream = avatarEntry.Open();
                using var memStream = new MemoryStream();
                await avatarStream.CopyToAsync(memStream);
                guide.AvatarImageBytes = memStream.ToArray();
                guide.AvatarContentType = GetContentTypeFromExtension(Path.GetExtension(avatarEntry.Name));
            }

            // Import context options
            var contextOptionsEntry = archive.GetEntry("HostExtensions/UI/contextOptions.json");
            if (contextOptionsEntry != null)
            {
                using var reader = new StreamReader(contextOptionsEntry.Open());
                var json = await reader.ReadToEndAsync();
                var options = JsonSerializer.Deserialize<List<JsonElement>>(json);
                
                if (options != null)
                {
                    foreach (var option in options)
                    {
                        var key = option.GetProperty("key").GetString();
                        var value = option.TryGetProperty("value", out var valProp) ? valProp.GetString() : null;
                        
                        if (!string.IsNullOrEmpty(key))
                        {
                            var ctx = new AssistantContextOption
                            {
                                AssistantId = guide.Id,
                                Key = key,
                                Value = value ?? "",
                                Created = DateTime.UtcNow
                            };
                            if (isUpdate)
                            {
                                await _context.AssistantContextOptions.AddAsync(ctx);
                            }
                            else
                            {
                                guide.ContextOptions.Add(ctx);
                            }
                        }
                    }
                }
            }

            // Import conversation starters
            var startersEntry = archive.GetEntry("HostExtensions/UI/conversationStarters.json");
            if (startersEntry != null)
            {
                using var reader = new StreamReader(startersEntry.Open());
                var json = await reader.ReadToEndAsync();
                var starters = JsonSerializer.Deserialize<List<string>>(json);
                
                if (starters != null)
                {
                    for (int i = 0; i < starters.Count; i++)
                    {
                        var starter = new AssistantConversationStarter
                        {
                            Id = Guid.NewGuid(),
                            AssistantId = guide.Id,
                            Prompt = starters[i],
                            OrderIndex = i,
                            Created = DateTime.UtcNow
                        };
                        if (isUpdate)
                        {
                            await _context.AssistantConversationStarters.AddAsync(starter);
                        }
                        else
                        {
                            guide.ConversationStarters.Add(starter);
                        }
                    }
                }
            }

            // Import OpenAPI schemas and auth providers (same logic as assistant)
            var authProvidersMap = new Dictionary<string, AssistantAuthProvider>();
            
            var authEntry = archive.GetEntry("OpenAPI/auth.json");
            if (authEntry != null)
            {
                using var reader = new StreamReader(authEntry.Open());
                var authJson = await reader.ReadToEndAsync();
                var authConfig = JsonSerializer.Deserialize<JsonElement>(authJson);
                
                if (authConfig.TryGetProperty("hosts", out var hosts))
                {
                    foreach (var host in hosts.EnumerateObject())
                    {
                        var providerId = host.Name;
                        var providerConfig = host.Value;
                        
                        var authType = providerConfig.GetProperty("auth_type").GetString() ?? "service_http";
                        
                        var provider = new AssistantAuthProvider
                        {
                            Id = Guid.NewGuid(),
                            ProviderId = providerId,
                            AuthType = authType,
                            Created = DateTime.UtcNow
                        };

                        if (authType == "oauth" || authType == "azure_oauth")
                        {
                            if (providerConfig.TryGetProperty("client_id", out var clientIdProp))
                                provider.ClientId = clientIdProp.GetString();
                            if (providerConfig.TryGetProperty("tenant", out var tenantProp))
                                provider.Tenant = tenantProp.GetString();
                            if (providerConfig.TryGetProperty("value_template", out var vtProp))
                                provider.ValueTemplate = vtProp.GetString();
                            if (providerConfig.TryGetProperty("user_config_policy", out var ucpProp))
                                provider.UserConfigPolicy = ucpProp.GetString();
                            
                            if (providerConfig.TryGetProperty("scopes", out var scopesProp) && scopesProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var scope in scopesProp.EnumerateArray())
                                {
                                    var scopeValue = scope.GetString();
                                    if (!string.IsNullOrEmpty(scopeValue))
                                    {
                                        provider.Scopes.Add(new AssistantAuthScope
                                        {
                                            AssistantAuthProviderId = provider.Id,
                                            Scope = scopeValue
                                        });
                                    }
                                }
                            }
                        }
                        else if (authType == "service_http")
                        {
                            if (providerConfig.TryGetProperty("header_name", out var hnProp))
                                provider.HeaderName = hnProp.GetString();
                            if (providerConfig.TryGetProperty("header_value_env_var", out var hvProp))
                                provider.ValueTemplate = hvProp.GetString();
                            if (providerConfig.TryGetProperty("value_template", out var vtProp2))
                                provider.ValueTemplate = vtProp2.GetString();
                            if (providerConfig.TryGetProperty("user_config_policy", out var ucpProp2))
                                provider.UserConfigPolicy = ucpProp2.GetString();
                        }

                        authProvidersMap[providerId] = provider;
                    }
                }
            }
            
            // Save auth providers first before linking them to schemas
            foreach (var provider in authProvidersMap.Values)
            {
                _context.AssistantAuthProviders.Add(provider);
            }
            if (authProvidersMap.Any())
            {
                await _context.SaveChangesAsync();
            }

            // Import OpenAPI schemas
            var openApiEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("OpenAPI/") && 
                           e.FullName.EndsWith(".json") && 
                           !e.FullName.EndsWith("/auth.json"))
                .ToList();
                
            foreach (var schemaEntry in openApiEntries)
            {
                var schemaName = Path.GetFileNameWithoutExtension(schemaEntry.Name);
                
                using var reader = new StreamReader(schemaEntry.Open());
                var specJson = await reader.ReadToEndAsync();
                
                // Extract API host from spec
                var apiHost = schemaName;
                try
                {
                    var spec = JsonSerializer.Deserialize<JsonElement>(specJson);
                    if (spec.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                    {
                        var firstServer = servers.EnumerateArray().FirstOrDefault();
                        if (firstServer.ValueKind != JsonValueKind.Undefined && firstServer.TryGetProperty("url", out var urlProp))
                        {
                            var url = urlProp.GetString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                var uri = new Uri(url);
                                apiHost = uri.Host;
                            }
                        }
                    }
                }
                catch
                {
                    // If we can't parse, use schema name as host
                }

                AssistantAuthProvider? authProvider = null;
                if (authProvidersMap.TryGetValue(apiHost, out var provider))
                {
                    authProvider = provider;
                }

                var schema = new AssistantOpenApiSchema
                {
                    Id = Guid.NewGuid(),
                    AssistantId = guide.Id,
                    Name = schemaName,
                    ApiHost = apiHost,
                    SpecificationJson = specJson,
                    AuthProvider = authProvider,
                    Created = DateTime.UtcNow
                };

                // Parse operations from the OpenAPI spec
                var operations = ParseOpenApiOperations(schema.Id, specJson);
                foreach (var operation in operations)
                {
                    schema.Operations.Add(operation);
                }

                if (isUpdate)
                {
                    await _context.AssistantOpenApiSchemas.AddAsync(schema);
                }
                else
                {
                    guide.OpenApiSchemas.Add(schema);
                }
            }

            // Import guide files
            var vectorStoreEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("VectorStores/") && !e.FullName.EndsWith("/"))
                .ToList();

            foreach (var fileEntry in vectorStoreEntries)
            {
                var relativePath = fileEntry.FullName.Substring("VectorStores/".Length);
                var parts = relativePath.Split('/', 2);
                if (parts.Length < 2) continue;
                
                var vectorStoreName = parts[0];
                var fileName = parts[1];

                using var fileStream = fileEntry.Open();
                using var memStream = new MemoryStream();
                await fileStream.CopyToAsync(memStream);

                var vf = new AssistantFile
                {
                    Id = Guid.NewGuid(),
                    AssistantId = guide.Id,
                    FolderKind = "VectorStore",
                    VectorStoreName = vectorStoreName,
                    RelativePath = fileName,
                    ContentBytes = memStream.ToArray(),
                    ContentType = GetContentTypeFromExtension(Path.GetExtension(fileName)),
                    Created = DateTime.UtcNow
                };
                if (isUpdate)
                {
                    await _context.AssistantFiles.AddAsync(vf);
                }
                else
                {
                    guide.Files.Add(vf);
                }
            }

            var codeInterpreterEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("CodeInterpreter/") && !e.FullName.EndsWith("/"))
                .ToList();

            foreach (var fileEntry in codeInterpreterEntries)
            {
                var fileName = Path.GetFileName(fileEntry.FullName);

                using var fileStream = fileEntry.Open();
                using var memStream = new MemoryStream();
                await fileStream.CopyToAsync(memStream);

                var cf = new AssistantFile
                {
                    Id = Guid.NewGuid(),
                    AssistantId = guide.Id,
                    FolderKind = "CodeInterpreter",
                    RelativePath = fileName,
                    ContentBytes = memStream.ToArray(),
                    ContentType = GetContentTypeFromExtension(Path.GetExtension(fileName)),
                    Created = DateTime.UtcNow
                };
                if (isUpdate)
                {
                    await _context.AssistantFiles.AddAsync(cf);
                }
                else
                {
                    guide.Files.Add(cf);
                }
            }

            // Import tools from guide manifest
            if (manifest.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
            {
                // Get all tools from catalog to map tool types to IDs
                var allTools = await _context.Tools.ToListAsync();
                var toolMapping = allTools.ToDictionary(t => t.ToolType, t => t.Id);

                foreach (var toolElement in toolsElement.EnumerateArray())
                {
                    if (toolElement.TryGetProperty("type", out var typeProp))
                    {
                        var toolType = typeProp.GetString();
                        if (!string.IsNullOrEmpty(toolType) && toolMapping.TryGetValue(toolType, out var toolId))
                        {
                            var at = new AssistantTool
                            {
                                AssistantId = guide.Id,
                                ToolId = toolId,
                                Created = DateTime.UtcNow
                            };
                            if (isUpdate)
                            {
                                await _context.AssistantTools.AddAsync(at);
                            }
                            else
                            {
                                guide.Tools.Add(at);
                            }
                        }
                    }
                }
            }

            if (isUpdate)
            {
                // Update scalar properties via bulk update to avoid tracked-entity concurrency issues
                await _context.Assistants
                    .Where(a => a.Id == guide.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(a => a.Description, guide.Description)
                        .SetProperty(a => a.DefaultAssistant, guide.DefaultAssistant)
                        .SetProperty(a => a.ModelId, guide.ModelId)
                        .SetProperty(a => a.Instructions, guide.Instructions)
                        .SetProperty(a => a.HomePageMarkdown, guide.HomePageMarkdown)
                        .SetProperty(a => a.AvatarImageBytes, guide.AvatarImageBytes)
                        .SetProperty(a => a.AvatarContentType, guide.AvatarContentType)
                        .SetProperty(a => a.AuthConfigJson, guide.AuthConfigJson)
                        .SetProperty(a => a.Updated, DateTime.UtcNow)
                    );

                // Ensure EF does not attempt an additional update on the tracked entity
                _context.Entry(guide).State = EntityState.Unchanged;
            }
            else
            {
                _context.Assistants.Add(guide);
            }
            // Defer SaveChanges until after crew linking to avoid concurrency issues

            // Step 3: Link crew members
            int globalLinked = 0;
            int crewsCreated = 0;
            
            if (manifest.TryGetProperty("crew", out var crewElement) && crewElement.ValueKind == JsonValueKind.Array)
            {
                int displayOrder = 0;
                foreach (var crewMember in crewElement.EnumerateArray())
                {
                    var crewName = crewMember.GetProperty("name").GetString();
                    if (string.IsNullOrEmpty(crewName))
                    {
                        warnings.Add("Skipped crew member with no name");
                        continue;
                    }

                    Guid? crewAssistantId = null;

                    // Try custom assistants first
                    if (customAssistantIds.TryGetValue(crewName, out var customId))
                    {
                        crewAssistantId = customId;
                    }
                    else
                    {
                        var existingAssistant = await _context.Assistants
                            .FirstOrDefaultAsync(a => a.Name == crewName && 
                                                     a.Kind == AssistantKind.Assistant);
                        
                        if (existingAssistant != null)
                        {
                            crewAssistantId = existingAssistant.Id;
                            globalLinked++;
                        }
                    }

                    if (crewAssistantId.HasValue)
                    {
                        var gm = new GuideMember
                        {
                            GuideId = guide.Id,
                            AssistantId = crewAssistantId.Value,
                            DisplayOrder = displayOrder++,
                            Created = DateTime.UtcNow
                        };
                        if (isUpdate)
                        {
                            await _context.GuideMembers.AddAsync(gm);
                        }
                        else
                        {
                            guide.CrewMembers.Add(gm);
                        }
                        crewsCreated++;
                    }
                    else
                    {
                        warnings.Add($"Crew member '{crewName}' not found (neither custom nor global)");
                    }
                }
            }

            await _context.SaveChangesAsync();
            
            // Capture values to return
            guideId = guide.Id;
            customAssistantsCreated = newlyCreatedAssistantIds.Count;
        });

        // Create markdown shadows and enqueue indexing jobs for the guide and all custom assistants (idempotent)
        await CreateMarkdownShadowsForAssistantAsync(guideId);
        foreach (var assistantId in customAssistantIds.Values)
        {
            await CreateMarkdownShadowsForAssistantAsync(assistantId);
        }

        return new ImportGuideResultDto(
            true,
            guideId,
            customAssistantsCreated,
            crewsCreated,
            globalLinked,
            0,
            warnings
        );
    }

    public async Task<byte[]> ExportAssistantAsync(Guid assistantId)
    {

        var assistant = await _context.Assistants
            .Include(a => a.Model)
            .Include(a => a.Tools).ThenInclude(t => t.Tool)
            .Include(a => a.ContextOptions)
            .Include(a => a.OpenApiSchemas).ThenInclude(s => s.AuthProvider).ThenInclude(ap => ap!.Scopes)
            .Include(a => a.Files)
            .Include(a => a.ConversationStarters)
            .Where(a => a.Id == assistantId && a.Kind == AssistantKind.Assistant)
            .FirstOrDefaultAsync();

        if (assistant == null)
        {
            throw new InvalidOperationException("Assistant not found");
        }

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            await ExportAssistantToArchiveAsync(archive, "", assistant);
        }

        memoryStream.Seek(0, SeekOrigin.Begin);
        return memoryStream.ToArray();
    }

    public async Task<ImportGuideResultDto> ImportAssistantAsync(Stream zipStream)
    {

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true);
        
        // Parse manifest
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry == null)
        {
            throw new InvalidOperationException("Invalid assistant export: manifest.json not found");
        }

        using var manifestReader = new StreamReader(manifestEntry.Open());
        var manifestJson = await manifestReader.ReadToEndAsync();
        
        // Quick validation - parse just the name to check for conflicts
        var manifestElement = JsonSerializer.Deserialize<JsonElement>(manifestJson);
        var name = manifestElement.GetProperty("name").GetString();
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException("Assistant name is required");
        }

        // Check for name conflict
        var existing = await _context.Assistants
            .FirstOrDefaultAsync(a => a.Name == name);
        
        if (existing != null)
        {
            throw new InvalidOperationException($"Assistant '{name}' already exists");
        }

        Guid assistantId = Guid.Empty;
        var warnings = new List<string>();
        
        // Use execution strategy to handle retry logic
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var assistant = await ImportAssistantFromArchiveAsync(archive, "", manifestJson, warnings);
            
            _context.Assistants.Add(assistant);
            await _context.SaveChangesAsync();
            
            // Capture ID to return
            assistantId = assistant.Id;
        });

        // Create markdown shadows and enqueue indexing jobs for imported files
        await CreateMarkdownShadowsForAssistantAsync(assistantId);

        return new ImportGuideResultDto(
            true,
            assistantId,
            0,  // CustomAssistantsCreated
            0,  // CrewsCreated
            0,  // GlobalAssistantsLinked
            0,  // ItemsSkipped
            warnings
        );
    }

    /// <summary>
    /// Normalizes legacy reasoning fields to reasoning_effort.
    /// </summary>
    private static string NormalizeManifestJson(string manifestJson)
    {
        var manifest = JsonSerializer.Deserialize<JsonElement>(manifestJson);

        if (manifest.TryGetProperty("reasoning_effort", out var alreadyEffort) && alreadyEffort.ValueKind == JsonValueKind.String)
        {
            return manifestJson;
        }

        if (!manifest.TryGetProperty("reasoning_effort", out var legacyEffort))
        {
            return manifestJson;
        }

        string? reasoningValue = legacyEffort.ValueKind switch
        {
            JsonValueKind.String => legacyEffort.GetString()?.ToLowerInvariant(),
            JsonValueKind.Number when legacyEffort.TryGetByte(out var numValue) => numValue switch
            {
                0 => "low",
                1 => "minimal",
                2 => "low",
                3 => "medium",
                4 => "high",
                _ => null
            },
            _ => null
        };

        if (string.IsNullOrWhiteSpace(reasoningValue))
        {
            return manifestJson;
        }

        using var doc = JsonDocument.Parse(manifestJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "reasoning_effort", StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteString("reasoning_effort", reasoningValue);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<string?> ResolveImportedModelIdOrWarnAsync(
        string? importedModelId,
        string importedEntityLabel,
        List<string>? warnings)
    {
        var modelId = importedModelId?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var modelExists = await _context.Models.AnyAsync(m => m.ModelId == modelId);
        if (modelExists)
        {
            return modelId;
        }

        warnings?.Add(
            $"Imported {importedEntityLabel} referenced catalog model '{modelId}', but that model is not in Settings → Models & Runtime. "
            + "It will use the default chat model if one is configured; otherwise chat will fail until you set a model.");
        return null;
    }

    /// <summary>
    /// Imports an assistant from a ZIP archive at the specified base path
    /// </summary>
    private async Task<Assistant> ImportAssistantFromArchiveAsync(
        ZipArchive archive,
        string basePath,
        string manifestJson,
        List<string>? warnings = null)
    {
        // Normalize the JSON to convert numeric reasoning_effort to string format
        var normalizedJson = NormalizeManifestJson(manifestJson);
        var normalizedManifest = JsonSerializer.Deserialize<JsonElement>(normalizedJson);
        
        // Now deserialize to AssistantDefinition (all fields will work properly)
        var assistantDef = JsonSerializer.Deserialize<AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition>(normalizedJson);
        if (assistantDef == null)
        {
            throw new InvalidOperationException("Failed to deserialize assistant manifest");
        }

        var name = assistantDef.Name ?? throw new InvalidOperationException("Assistant name is required");
        var description = assistantDef.Description ?? "";
        var invocationEvaluator = string.IsNullOrWhiteSpace(assistantDef.InvocationEvaluator)
            ? null
            : assistantDef.InvocationEvaluator;
        
        var modelId = await ResolveImportedModelIdOrWarnAsync(
            assistantDef.Model,
            $"assistant '{name}'",
            warnings);

        // Get temperature, top_p, reasoning_effort from properly deserialized object
        float? temperature = assistantDef.Temperature;
        double? topP = assistantDef.TopP;
        string? reasoningEffort = string.IsNullOrWhiteSpace(assistantDef.ReasoningEffort)
            ? null
            : assistantDef.ReasoningEffort;

        // Create assistant entity
        var assistant = new Assistant
        {
            Id = Guid.NewGuid(),
            Kind = AssistantKind.Assistant,
            Name = name,
            Description = description ?? "",
            InvocationEvaluator = invocationEvaluator,
            ModelId = modelId,
            Temperature = temperature,
            TopP = topP,
            ReasoningEffort = reasoningEffort,
            Created = DateTime.UtcNow
        };

        // Import instructions
        var instructionsEntry = archive.GetEntry($"{basePath}/instructions.md".TrimStart('/'));
        if (instructionsEntry != null)
        {
            using var reader = new StreamReader(instructionsEntry.Open());
            assistant.Instructions = await reader.ReadToEndAsync();
        }

        // Import avatar
        var avatarEntry = archive.Entries.FirstOrDefault(e => 
            e.FullName.StartsWith($"{basePath}/HostExtensions/UI/avatar.".TrimStart('/')) && 
            !e.FullName.EndsWith("/"));
        
        if (avatarEntry != null)
        {
            using var avatarStream = avatarEntry.Open();
            using var memStream = new MemoryStream();
            await avatarStream.CopyToAsync(memStream);
            assistant.AvatarImageBytes = memStream.ToArray();
            assistant.AvatarContentType = GetContentTypeFromExtension(Path.GetExtension(avatarEntry.Name));
        }

        // Import context options
        var contextOptionsEntry = archive.GetEntry($"{basePath}/HostExtensions/UI/contextOptions.json".TrimStart('/'));
        if (contextOptionsEntry != null)
        {
            using var reader = new StreamReader(contextOptionsEntry.Open());
            var json = await reader.ReadToEndAsync();
            var options = JsonSerializer.Deserialize<List<JsonElement>>(json);
            
            if (options != null)
            {
                foreach (var option in options)
                {
                    var key = option.GetProperty("key").GetString();
                    var value = option.TryGetProperty("value", out var valProp) ? valProp.GetString() : null;
                    
                    if (!string.IsNullOrEmpty(key))
                    {
                        assistant.ContextOptions.Add(new AssistantContextOption
                        {
                            AssistantId = assistant.Id,
                            Key = key,
                            Value = value ?? "",
                            Created = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        // Import conversation starters
        var startersEntry = archive.GetEntry($"{basePath}/HostExtensions/UI/conversationStarters.json".TrimStart('/'));
        if (startersEntry != null)
        {
            using var reader = new StreamReader(startersEntry.Open());
            var json = await reader.ReadToEndAsync();
            var starters = JsonSerializer.Deserialize<List<string>>(json);
            
            if (starters != null)
            {
                for (int i = 0; i < starters.Count; i++)
                {
                    assistant.ConversationStarters.Add(new AssistantConversationStarter
                    {
                        Id = Guid.NewGuid(),
                        AssistantId = assistant.Id,
                        Prompt = starters[i],
                        OrderIndex = i,
                        Created = DateTime.UtcNow
                    });
                }
            }
        }

        // Import tools from manifest
        if (assistantDef.Tools != null && assistantDef.Tools.Any())
        {
            // Get all tools from catalog to map tool types to IDs
            var allTools = await _context.Tools.ToListAsync();
            var toolMapping = allTools.ToDictionary(t => t.ToolType, t => t.Id);
            
            foreach (var toolDef in assistantDef.Tools)
            {
                // ToolDefinition has a Type property
                var toolType = toolDef.Type;
                if (!string.IsNullOrEmpty(toolType) && toolMapping.TryGetValue(toolType, out var toolId))
                {
                    assistant.Tools.Add(new AssistantTool
                    {
                        AssistantId = assistant.Id,
                        ToolId = toolId,
                        Created = DateTime.UtcNow
                    });
                }
            }
        }

        // Import OpenAPI schemas and auth providers
        var authProvidersMap = new Dictionary<string, AssistantAuthProvider>();
        
        // First, load auth.json if it exists
        var authEntry = archive.GetEntry($"{basePath}/OpenAPI/auth.json".TrimStart('/'));
        if (authEntry != null)
        {
            using var reader = new StreamReader(authEntry.Open());
            var authJson = await reader.ReadToEndAsync();
            var authConfig = JsonSerializer.Deserialize<JsonElement>(authJson);
            
            if (authConfig.TryGetProperty("hosts", out var hosts))
            {
                foreach (var host in hosts.EnumerateObject())
                {
                    var providerId = host.Name;
                    var providerConfig = host.Value;
                    
                    var authType = providerConfig.GetProperty("auth_type").GetString() ?? "service_http";
                    
                    var provider = new AssistantAuthProvider
                    {
                        Id = Guid.NewGuid(),
                        ProviderId = providerId,
                        AuthType = authType,
                        Created = DateTime.UtcNow
                    };

                    if (authType == "oauth" || authType == "azure_oauth")
                    {
                        if (providerConfig.TryGetProperty("client_id", out var clientIdProp))
                            provider.ClientId = clientIdProp.GetString();
                        if (providerConfig.TryGetProperty("tenant", out var tenantProp))
                            provider.Tenant = tenantProp.GetString();
                        if (providerConfig.TryGetProperty("value_template", out var vtProp))
                            provider.ValueTemplate = vtProp.GetString();
                        if (providerConfig.TryGetProperty("user_config_policy", out var ucpProp))
                            provider.UserConfigPolicy = ucpProp.GetString();
                        
                        if (providerConfig.TryGetProperty("scopes", out var scopesProp) && scopesProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var scope in scopesProp.EnumerateArray())
                            {
                                var scopeValue = scope.GetString();
                                if (!string.IsNullOrEmpty(scopeValue))
                                {
                                    provider.Scopes.Add(new AssistantAuthScope
                                    {
                                        AssistantAuthProviderId = provider.Id,
                                        Scope = scopeValue
                                    });
                                }
                            }
                        }
                    }
                    else if (authType == "service_http")
                    {
                        if (providerConfig.TryGetProperty("header_name", out var hnProp))
                            provider.HeaderName = hnProp.GetString();
                        if (providerConfig.TryGetProperty("header_value_env_var", out var hvProp))
                        {
                            var v = hvProp.GetString();
                            // Treat masked sentinel as not set and require user configuration
                            if (v == "••••••••")
                            {
                                provider.ValueTemplate = null;
                                provider.UserConfigPolicy = "required";
                            }
                            else
                            {
                                provider.ValueTemplate = v;
                            }
                        }
                        if (providerConfig.TryGetProperty("value_template", out var vtProp2))
                            provider.ValueTemplate = vtProp2.GetString();
                        if (providerConfig.TryGetProperty("user_config_policy", out var ucpProp2))
                            provider.UserConfigPolicy = ucpProp2.GetString();
                    }

                    authProvidersMap[providerId] = provider;
                }
            }
        }
        
        // Save auth providers first before linking them to schemas
        foreach (var provider in authProvidersMap.Values)
        {
            _context.AssistantAuthProviders.Add(provider);
        }
        if (authProvidersMap.Any())
        {
            await _context.SaveChangesAsync();
        }

        // Now load OpenAPI schema files
        var openApiEntries = archive.Entries
            .Where(e => e.FullName.StartsWith($"{basePath}/OpenAPI/".TrimStart('/')) && 
                       e.FullName.EndsWith(".json") && 
                       !e.FullName.EndsWith("/auth.json"))
            .ToList();

        foreach (var schemaEntry in openApiEntries)
        {
            var schemaName = Path.GetFileNameWithoutExtension(schemaEntry.Name);
            
            using var reader = new StreamReader(schemaEntry.Open());
            var specJson = await reader.ReadToEndAsync();
            
            // Extract API host from spec
            var apiHost = schemaName; // Default to schema name
            try
            {
                var spec = JsonSerializer.Deserialize<JsonElement>(specJson);
                if (spec.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                {
                    var firstServer = servers.EnumerateArray().FirstOrDefault();
                    if (firstServer.ValueKind != JsonValueKind.Undefined && firstServer.TryGetProperty("url", out var urlProp))
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrEmpty(url))
                        {
                            var uri = new Uri(url);
                            apiHost = uri.Host;
                        }
                    }
                }
            }
            catch
            {
                // If we can't parse, use schema name as host
            }

            // Link to auth provider if available
            AssistantAuthProvider? authProvider = null;
            if (authProvidersMap.TryGetValue(apiHost, out var provider))
            {
                authProvider = provider;
            }

            var schema = new AssistantOpenApiSchema
            {
                Id = Guid.NewGuid(),
                AssistantId = assistant.Id,
                Name = schemaName,
                ApiHost = apiHost,
                SpecificationJson = specJson,
                AuthProvider = authProvider,
                Created = DateTime.UtcNow
            };

            // Parse operations from the OpenAPI spec
            var operations = ParseOpenApiOperations(schema.Id, specJson);
            foreach (var operation in operations)
            {
                schema.Operations.Add(operation);
            }

            assistant.OpenApiSchemas.Add(schema);
        }

        // Import assistant files
        var vectorStoreEntries = archive.Entries
            .Where(e => e.FullName.StartsWith($"{basePath}/VectorStores/".TrimStart('/')) && !e.FullName.EndsWith("/"))
            .ToList();

        foreach (var fileEntry in vectorStoreEntries)
        {
            var relativePath = fileEntry.FullName.Substring($"{basePath}/VectorStores/".TrimStart('/').Length);
            var parts = relativePath.Split('/', 2);
            if (parts.Length < 2) continue;
            
            var vectorStoreName = parts[0];
            var fileName = parts[1];

            using var fileStream = fileEntry.Open();
            using var memStream = new MemoryStream();
            await fileStream.CopyToAsync(memStream);

            assistant.Files.Add(new AssistantFile
            {
                Id = Guid.NewGuid(),
                AssistantId = assistant.Id,
                FolderKind = "VectorStore",
                VectorStoreName = vectorStoreName,
                RelativePath = fileName,
                ContentBytes = memStream.ToArray(),
                ContentType = GetContentTypeFromExtension(Path.GetExtension(fileName)),
                Created = DateTime.UtcNow
            });
        }

        var codeInterpreterEntries = archive.Entries
            .Where(e => e.FullName.StartsWith($"{basePath}/CodeInterpreter/".TrimStart('/')) && !e.FullName.EndsWith("/"))
            .ToList();

        foreach (var fileEntry in codeInterpreterEntries)
        {
            var fileName = Path.GetFileName(fileEntry.FullName);

            using var fileStream = fileEntry.Open();
            using var memStream = new MemoryStream();
            await fileStream.CopyToAsync(memStream);

            assistant.Files.Add(new AssistantFile
            {
                Id = Guid.NewGuid(),
                AssistantId = assistant.Id,
                FolderKind = "CodeInterpreter",
                RelativePath = fileName,
                ContentBytes = memStream.ToArray(),
                ContentType = GetContentTypeFromExtension(Path.GetExtension(fileName)),
                Created = DateTime.UtcNow
            });
        }

        return assistant;
    }

    /// <summary>
    /// Parses an OpenAPI specification and creates AssistantOpenApiOperation entities.
    /// Each operation (path+method) becomes a separate entity with its tool definition and schema fragment.
    /// </summary>
    private static List<AssistantOpenApiOperation> ParseOpenApiOperations(Guid schemaId, string openApiSpecJson)
    {
        var operations = new List<AssistantOpenApiOperation>();

        try
        {
            // Validate and parse the OpenAPI spec
            var validationResult = OpenApiHelper.ValidateAndParseOpenApiSpec(openApiSpecJson);
            if (!validationResult.Status || validationResult.Spec == null)
            {
                // Return empty list instead of throwing - allow import to succeed without operations
                return operations;
            }

            var spec = validationResult.Spec;
            var root = spec.RootElement;

            // Get tool definitions from the spec
            var toolDefinitions = OpenApiHelper.GetToolDefinitionsFromSchema(spec);

            // Iterate through paths and methods to extract operations
            if (!root.TryGetProperty("paths", out var paths))
            {
                return operations;
            }

            foreach (var pathProperty in paths.EnumerateObject())
            {
                var path = pathProperty.Name;

                foreach (var methodProperty in pathProperty.Value.EnumerateObject())
                {
                    var method = methodProperty.Name.ToUpper();
                    var operationObj = methodProperty.Value;

                    // Extract operation ID (handle non-string operationId gracefully)
                    string? operationId;
                    if (operationObj.TryGetProperty("operationId", out var opId))
                    {
                        operationId = opId.ValueKind == JsonValueKind.String 
                            ? opId.GetString() 
                            : opId.GetRawText(); // Handle numeric/other operationIds
                    }
                    else
                    {
                        operationId = $"{method}_{path}";
                    }

                    if (string.IsNullOrEmpty(operationId))
                    {
                        continue;
                    }

                    // Extract summary (handle non-string values gracefully)
                    var summary = operationObj.TryGetProperty("summary", out var summaryEl)
                        ? summaryEl.ValueKind == JsonValueKind.String ? summaryEl.GetString() : null
                        : operationObj.TryGetProperty("description", out var descEl)
                        ? descEl.ValueKind == JsonValueKind.String ? descEl.GetString() : null
                        : null;

                    // Find the matching tool definition
                    var toolDef = toolDefinitions.FirstOrDefault(t => 
                        t.Function?.AsObject?.Name == operationId);

                    if (toolDef == null)
                    {
                        continue;
                    }

                    // Serialize the tool definition to JSON
                    var toolDefJson = JsonSerializer.Serialize(toolDef, new JsonSerializerOptions 
                    { 
                        WriteIndented = false 
                    });

                    // Create schema fragment containing this specific operation
                    var schemaFragment = new
                    {
                        path = path,
                        method = method.ToLower(),
                        operation = JsonDocument.Parse(operationObj.GetRawText()).RootElement
                    };

                    var schemaFragmentJson = JsonSerializer.Serialize(schemaFragment, new JsonSerializerOptions 
                    { 
                        WriteIndented = true 
                    });

                    // Create the operation entity
                    operations.Add(new AssistantOpenApiOperation
                    {
                        Id = Guid.NewGuid(),
                        SchemaId = schemaId,
                        OperationId = operationId,
                        Method = method,
                        Path = path,
                        Summary = summary,
                        ToolDefinitionJson = toolDefJson,
                        SchemaFragmentJson = schemaFragmentJson,
                        Created = DateTime.UtcNow
                    });
                }
            }
        }
        catch (Exception)
        {
            // Swallow exceptions - allow import to succeed without operations
            // Operations can be regenerated later if needed
        }

        return operations;
    }

    private static string GetContentTypeFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Creates markdown shadows and enqueues indexing jobs for imported assistant files
    /// </summary>
    private async Task CreateMarkdownShadowsForAssistantAsync(Guid assistantId)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();
        
        // Get all vector store files for this assistant
        var vectorStoreFiles = await context.AssistantFiles
            .Where(f => f.AssistantId == assistantId && f.FolderKind == "VectorStore")
            .Select(f => f.Id)
            .ToListAsync();

        if (!vectorStoreFiles.Any())
        {
            return;
        }

        // Only create missing shadow records so repeated imports/startup seeding stay idempotent.
        var existingShadowFileIds = await context.AssistantFileMarkdownShadows
            .Where(s => vectorStoreFiles.Contains(s.OriginalAssistantFileId))
            .Select(s => s.OriginalAssistantFileId)
            .ToListAsync();

        var existingShadowFileIdSet = existingShadowFileIds.ToHashSet();
        var fileIdsNeedingShadows = vectorStoreFiles
            .Where(fileId => !existingShadowFileIdSet.Contains(fileId))
            .ToList();

        if (!fileIdsNeedingShadows.Any())
        {
            return;
        }

        // Create markdown shadow records for only missing files.
        foreach (var fileId in fileIdsNeedingShadows)
        {
            var shadow = new AssistantFileMarkdownShadow
            {
                Id = Guid.NewGuid(),
                OriginalAssistantFileId = fileId,
                ContentHash = string.Empty,
                StoragePath = string.Empty,
                FileSize = 0,
                Status = MarkdownExtractionStatus.Pending,
                Created = DateTime.UtcNow
            };

            context.AssistantFileMarkdownShadows.Add(shadow);
        }

        await context.SaveChangesAsync();

        // Enqueue extraction jobs (which will chain to indexing) only for newly created shadows.
        foreach (var fileId in fileIdsNeedingShadows)
        {
            await _jobQueue.EnqueueAsync(
                jobType: "ExtractAssistantFileMarkdown",
                payload: new BackgroundJobs.Jobs.ExtractAssistantFileMarkdownJob(fileId));
        }
    }
}

