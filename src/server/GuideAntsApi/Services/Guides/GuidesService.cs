using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.EnvironmentVariables;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Mcp;
using GuideAntsApi.Services.SystemGuide;
using GuideAntsApi.Settings;
using AntRunner.ToolCalling.Functions;
using AntRunner.Chat;
using Microsoft.Extensions.Options;
using GuideAntsApi.Services.Guides.Skills;
using AntRunner.ToolCalling.AssistantDefinitions;

namespace GuideAntsApi.Services.Guides;

public class GuidesService(
    ApplicationDbContext context,
    MarkdownExtractionService markdownExtractionService,
    IRuntimeProfileResolver runtimeProfileResolver,
    IOptionsMonitor<SettingsSecretsOptions> settingsSecretsOptions,
    ISystemGuideCatalogFilter systemGuideCatalogFilter,
    IMcpSandboxSetupStagingService mcpSandboxSetupStagingService,
    IAssistantSkillMetaSync assistantSkillMetaSync,
    ILogger<GuidesService> logger) : IGuidesService
{
    private readonly ApplicationDbContext _context = context;
    private readonly MarkdownExtractionService _markdownExtractionService = markdownExtractionService;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver = runtimeProfileResolver;
    private readonly IOptionsMonitor<SettingsSecretsOptions> _settingsSecretsOptions = settingsSecretsOptions;
    private readonly ISystemGuideCatalogFilter _systemGuideCatalogFilter = systemGuideCatalogFilter;
    private readonly IMcpSandboxSetupStagingService _mcpSandboxSetupStagingService = mcpSandboxSetupStagingService;
    private readonly IAssistantSkillMetaSync _assistantSkillMetaSync = assistantSkillMetaSync;
    private readonly ILogger<GuidesService> _logger = logger;

    private static readonly JsonSerializerOptions JsonCaseInsensitiveOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonCamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions JsonIndentedOptions = new()
    {
        WriteIndented = true
    };

    #region Guides

    public async Task<IEnumerable<GuideDto>> GetGuidesAsync(Guid? projectId = null)
    {
        var hiddenGuideIds = await _systemGuideCatalogFilter.GetHiddenGuideIdsForCatalogAsync(projectId);

        return await _context.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && !hiddenGuideIds.Contains(a.Id))
            .OrderBy(a => a.Created)
            .Select(a => new GuideDto(
                a.Id,
                a.Name,
                a.Description ?? string.Empty,
                a.AvatarImageBytes != null ? $"/api/guides/{a.Id}/avatar" : null,
                a.ModelId,
                a.Model != null ? a.Model.DisplayName : null,
                a.Tools.Count + a.OpenApiSchemas.Sum(s => s.Operations.Count),
                a.CrewMembers.Select(ga => ga.AssistantId).Distinct().Count(),
                a.Created,
                a.Updated
            ))
            .ToListAsync();
    }

    public async Task<GuideDetailsDto?> GetGuideAsync(Guid guideId, Guid? projectId = null)
    {
        var guide = await _context.Assistants
            .Include(a => a.Model)
            .Include(a => a.Tools).ThenInclude(t => t.Tool)
            .Include(a => a.ContextOptions)
            .Include(a => a.OpenApiSchemas).ThenInclude(s => s.AuthProvider).ThenInclude(ap => ap!.Scopes)
            .Include(a => a.OpenApiSchemas).ThenInclude(s => s.Operations)
            .Include(a => a.Files)
            .Include(a => a.SkillMetas)
            .Include(a => a.ConversationStarters)
            .Include(a => a.CrewMembers).ThenInclude(ga => ga.Assistant)
            .Where(a => a.Id == guideId && a.Kind == AssistantKind.Guide)
            .FirstOrDefaultAsync();

        if (guide == null)
            return null;

        var guideDto = new GuideDto(
            guide.Id,
            guide.Name,
            guide.Description ?? string.Empty,
            guide.AvatarImageBytes != null ? $"/api/guides/{guide.Id}/avatar?t={guide.Updated?.Ticks ?? guide.Created.Ticks}" : null,
            guide.ModelId,
            guide.Model?.DisplayName,
            guide.Tools.Count + guide.OpenApiSchemas.Sum(s => s.Operations.Count),
            guide.CrewMembers.Select(ga => ga.AssistantId).Distinct().Count(),
            guide.Created,
            guide.Updated
        );

        var tools = guide.Tools.Select(t => new ToolAssignmentDto(
            t.ToolId,
            t.Tool.ToolType,
            t.Tool.DisplayName,
            false
        )).ToList();

        var contextOptions = guide.ContextOptions.Select(co => new ContextOptionDto(
            co.Key,
            co.Value
        )).ToList();

        // Convert OpenApiSchemas back to CustomToolDto format
        var customTools = guide.OpenApiSchemas.Select(schema => 
        {
            OpenApiAuthConfigDto? authConfig = null;
            if (schema.AuthProvider != null)
            {
                // Never return the actual ValueTemplate (secret) - mask it if present
                var maskedValue = string.IsNullOrEmpty(schema.AuthProvider.ValueTemplate) 
                    ? null 
                    : "••••••••";
                
                authConfig = new OpenApiAuthConfigDto(
                    schema.AuthProvider.AuthType,
                    schema.AuthProvider.ClientId,
                    schema.AuthProvider.Tenant,
                    [.. schema.AuthProvider.Scopes.Select(s => s.Scope)],
                    maskedValue, // Return masked value for write-only field
                    schema.AuthProvider.HeaderName,
                    schema.AuthProvider.UserConfigPolicy
                );
            }

            var operations = schema.Operations.Select(op => new OpenApiOperationDto(
                op.Id,
                op.OperationId,
                op.Method,
                op.Path,
                op.Summary,
                op.SchemaFragmentJson,
                op.ToolDefinitionJson
            )).ToList();

            return new CustomToolDto(
                schema.Name,
                schema.SpecificationJson,
                schema.ApiHost,
                authConfig,
                operations
            );
        }).ToList();

        var files = new List<FileDto>();
        foreach (var f in guide.Files.Where(file =>
                     !string.Equals(file.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)))
        {
            AssistantFileMarkdownShadowDto? shadowDto = null;
            if (f.FolderKind == "VectorStore")
            {
                var shadow = await _markdownExtractionService.GetAssistantFileMarkdownShadowAsync(f.Id);
                if (shadow != null)
                {
                    shadowDto = new AssistantFileMarkdownShadowDto(
                        shadow.Id,
                        shadow.OriginalAssistantFileId,
                        shadow.ContentHash,
                        shadow.FileSize,
                        shadow.Status,
                        shadow.ErrorMessage,
                        shadow.Created,
                        shadow.ProcessedAt
                    );
                }
            }

            files.Add(new FileDto(
                f.Id,
                f.FolderKind,
                f.VectorStoreName,
                f.RelativePath,
                f.ContentType,
                f.Created,
                shadowDto
            ));
        }

        var skills = SkillDtoBuilder.BuildFromAssistantFiles(guide.Files, guide.SkillMetas);

        var conversationStarters = guide.ConversationStarters
            .OrderBy(cs => cs.OrderIndex)
            .Select(cs => new ConversationStarterDto(
                cs.Id,
                cs.Prompt,
                cs.OrderIndex
            )).ToList();

        // Guide has one crew (simplified from multiple crews)
        var crewMembers = guide.CrewMembers
            .OrderBy(ga => ga.DisplayOrder)
            .Select(ga => new CrewMemberDto(
                ga.AssistantId,
                ga.Assistant.Name,
                ga.Assistant.AvatarImageBytes != null ? $"/api/assistants/{ga.AssistantId}/avatar" : null,
                false, // IsGlobal - to be determined by business logic
                ga.DisplayOrder ?? 0
            )).ToList();
        
        List<CrewSummaryDto> crews = [new CrewSummaryDto(guide.Id, $"{guide.Name} Crew", crewMembers)];

        // Parse auth providers from AuthConfigJson
        List<AuthProviderDto>? authProviders = null;
        if (!string.IsNullOrWhiteSpace(guide.AuthConfigJson))
        {
            try
            {
                var authConfig = JsonSerializer.Deserialize<AuthConfigManifest>(guide.AuthConfigJson, JsonCaseInsensitiveOptions);
                if (authConfig?.Providers is { Count: > 0 })
                {
                    authProviders = [.. authConfig.Providers.Select(p => new AuthProviderDto(
                        null,
                        p.Id,
                        p.AuthType ?? "oauth",
                        p.ClientId,
                        p.Tenant,
                        p.HeaderName,
                        p.ValueTemplate,
                        p.UserConfigPolicy,
                        p.Scopes
                    ))];
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse AuthConfigJson for guide {GuideName}", LogValueSanitizer.Sanitize(guide.Name));
            }
        }

        var environmentVariables = await GetProjectEnvironmentForClientAsync(projectId, guide.Id);

        return new GuideDetailsDto(
            guideDto,
            guide.Instructions,
            guide.HomePageMarkdown,
            guide.Temperature,
            guide.TopP,
            guide.ReasoningEffort,
            tools,
            contextOptions,
            authProviders,
            customTools,
            files,
            conversationStarters,
            crews
        )
        {
            EnvironmentVariables = environmentVariables.Count > 0 ? environmentVariables : null,
            Skills = skills.Count > 0 ? skills : null
        };
    }

    // Internal class for deserializing AuthConfigJson
    private sealed class AuthConfigManifest
    {
        public List<AuthProviderManifest> Providers { get; set; } = [];
    }

    private sealed class AuthProviderManifest
    {
        public string Id { get; set; } = string.Empty;
        public string? AuthType { get; set; }
        public string? ClientId { get; set; }
        public string? Tenant { get; set; }
        public List<string>? Scopes { get; set; }
        public string? ValueTemplate { get; set; }
        public string? HeaderName { get; set; }
        public string? UserConfigPolicy { get; set; }
    }

    private static string SerializeAuthProviders(List<AuthProviderDto> providers)
    {
        var manifest = new AuthConfigManifest
        {
            Providers = [.. providers.Select(p => new AuthProviderManifest
            {
                Id = p.ProviderId,
                AuthType = p.AuthType,
                ClientId = p.ClientId,
                Tenant = p.Tenant,
                Scopes = p.Scopes,
                ValueTemplate = p.ValueTemplate,
                HeaderName = p.HeaderName,
                UserConfigPolicy = p.UserConfigPolicy
            })]
        };
        return JsonSerializer.Serialize(manifest, JsonCamelCaseOptions);
    }

    private async Task<List<EnvironmentVariableDto>> GetProjectEnvironmentForClientAsync(Guid? projectId, Guid assistantId)
    {
        if (!projectId.HasValue || projectId.Value == Guid.Empty)
        {
            return [];
        }

        var environmentJson = await _context.ProjectAssistantEnvironments
            .AsNoTracking()
            .Where(environment => environment.ProjectId == projectId.Value && environment.AssistantId == assistantId)
            .Select(environment => environment.EnvironmentConfigJson)
            .FirstOrDefaultAsync();

        return EnvironmentVariableConfigSerializer.DeserializeForClient(environmentJson);
    }

    private async Task StageMcpSandboxSetupIfNeededAsync(
        Guid? projectId,
        Guid guideId,
        IReadOnlyList<CustomToolDto>? customTools)
    {
        if (!projectId.HasValue || projectId.Value == Guid.Empty || customTools is null || customTools.Count == 0)
        {
            return;
        }

        try
        {
            await _mcpSandboxSetupStagingService.StageGuideSandboxSetupAsync(
                projectId.Value,
                guideId,
                customTools);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to stage sandbox MCP setup for guide {GuideId} in project {ProjectId}",
                guideId,
                projectId.Value);
            throw new InvalidOperationException(
                "Failed to stage sandbox MCP package setup. Ensure guideants-ai is reachable and try again.",
                ex);
        }
    }

    private static void ValidateMcpAssistantConstraints(IReadOnlyList<CustomToolDto> customTools)
    {
        ToolSourceValidator.EnsureMcpAssistantConstraintsOrThrow(
            customTools
                .Where(tool => !string.IsNullOrWhiteSpace(tool.Name) && !string.IsNullOrWhiteSpace(tool.OpenApiSpec))
                .Select(tool => (tool.Name, tool.OpenApiSpec))
                .ToList());
    }

    private async Task SaveProjectEnvironmentAsync(
        Guid? projectId,
        Guid assistantId,
        IReadOnlyCollection<EnvironmentVariableDto>? variables)
    {
        if (!projectId.HasValue || projectId.Value == Guid.Empty)
        {
            return;
        }

        var projectExists = await _context.Projects
            .AnyAsync(project => project.Id == projectId.Value && !project.Deleted);
        if (!projectExists)
        {
            throw new InvalidOperationException($"Project '{projectId.Value}' was not found.");
        }

        var existing = await _context.ProjectAssistantEnvironments
            .FirstOrDefaultAsync(environment => environment.ProjectId == projectId.Value && environment.AssistantId == assistantId);

        var serialized = EnvironmentVariableConfigSerializer.SerializeFromClient(
            variables,
            existing?.EnvironmentConfigJson,
            _settingsSecretsOptions.CurrentValue);

        if (serialized is null)
        {
            if (existing is not null)
            {
                _context.ProjectAssistantEnvironments.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            _context.ProjectAssistantEnvironments.Add(new ProjectAssistantEnvironment
            {
                ProjectId = projectId.Value,
                AssistantId = assistantId,
                EnvironmentConfigJson = serialized,
                Created = DateTime.UtcNow
            });
            return;
        }

        existing.EnvironmentConfigJson = serialized;
        existing.Updated = DateTime.UtcNow;
    }

    public async Task<GuideDto> CreateGuideAsync(CreateGuideDto dto)
    {
        var modelParameters = await NormalizeAndValidateModelParametersAsync(
            dto.ModelId,
            dto.Temperature,
            dto.TopP,
            dto.ReasoningEffort,
            dto.SamplingParametersJson);

        // Validate runtime compatibility
        var members = new List<GuideRuntimeValidationMember>
        {
            new("guide", null, dto.Name, dto.ModelId)
        };
        
        if (dto.CrewMemberIds != null && dto.CrewMemberIds.Count > 0)
        {
            var crewAssistants = await _context.Assistants
                .Where(a => dto.CrewMemberIds.Contains(a.Id))
                .ToListAsync();
                
            members.AddRange(crewAssistants.Select(a => new GuideRuntimeValidationMember("assistant", a.Id, a.Name, a.ModelId)));
        }

        var validation = await ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest(members));
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Conflicts));
        }

        var mergedFiles = MergeCreateFiles(dto.Files, dto.Skills);

        var guide = CreateAssistantEntity(
            AssistantKind.Guide,
            dto.Name,
            dto.Description,
            dto.Instructions,
            dto.HomePageMarkdown, // guides have home pages
            dto.ModelId,
            modelParameters.Temperature,
            modelParameters.TopP,
            modelParameters.ReasoningEffort,
            modelParameters.SamplingParametersJson,
            dto.AvatarImageBytes,
            dto.AvatarContentType,
            dto.ToolIds,
            dto.CustomTools,
            dto.ContextOptions,
            mergedFiles,
            dto.ConversationStarters,
            dto.CrewMemberIds  // guides have crews
        );

        // Set auth config JSON if provided
        if (dto.AuthProviders is { Count: > 0 })
        {
            guide.AuthConfigJson = SerializeAuthProviders(dto.AuthProviders);
        }
        _context.Assistants.Add(guide);
        await SaveProjectEnvironmentAsync(dto.ProjectId, guide.Id, dto.EnvironmentVariables);
        await _context.SaveChangesAsync();
        await StageMcpSandboxSetupIfNeededAsync(dto.ProjectId, guide.Id, dto.CustomTools);

        // Create markdown shadows for vector store files
        var vectorStoreFiles = guide.Files.Where(f => f.FolderKind == "VectorStore").ToList();
        foreach (var file in vectorStoreFiles)
        {
            try
            {
                await _markdownExtractionService.CreateAssistantFileMarkdownShadowAsync(file.Id);
                _logger.LogInformation("Created markdown shadow for AssistantFile {AssistantFileId} ({RelativePath})", 
                    file.Id, LogValueSanitizer.Sanitize(file.RelativePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create markdown shadow for AssistantFile {AssistantFileId} ({RelativePath})", 
                    file.Id, LogValueSanitizer.Sanitize(file.RelativePath));
                // Don't fail the creation - extraction will be retried
            }
        }

        return new GuideDto(
            guide.Id,
            guide.Name,
            guide.Description ?? string.Empty,
            guide.AvatarImageBytes != null ? $"/api/guides/{guide.Id}/avatar?t={guide.Updated?.Ticks ?? guide.Created.Ticks}" : null,
            guide.ModelId,
            null,
            guide.Tools.Count,
            guide.CrewMembers.Count,
            guide.Created,
            guide.Updated
        );
    }

    public async Task<GuideDto> UpdateGuideAsync(Guid guideId, UpdateGuideDto dto)
    {
        var modelParameters = await NormalizeAndValidateModelParametersAsync(
            dto.ModelId,
            dto.Temperature,
            dto.TopP,
            dto.ReasoningEffort,
            dto.SamplingParametersJson);

        // Validate runtime compatibility
        var members = new List<GuideRuntimeValidationMember>
        {
            new("guide", guideId, dto.Name, dto.ModelId)
        };
        
        if (dto.CrewMemberIds != null && dto.CrewMemberIds.Count > 0)
        {
            var crewAssistants = await _context.Assistants
                .Where(a => dto.CrewMemberIds.Contains(a.Id))
                .ToListAsync();
                
            members.AddRange(crewAssistants.Select(a => new GuideRuntimeValidationMember("assistant", a.Id, a.Name, a.ModelId)));
        }

        var validation = await ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest(members));
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Conflicts));
        }

        var guide = await UpdateAssistantEntityAsync(
            guideId,
            AssistantKind.Guide,
            dto.Name,
            dto.Description,
            dto.Instructions,
            dto.HomePageMarkdown, // guides have home pages
            dto.ModelId,
            modelParameters.Temperature,
            modelParameters.TopP,
            modelParameters.ReasoningEffort,
            modelParameters.SamplingParametersJson,
            dto.AvatarImageBytes,
            dto.AvatarContentType,
            dto.ToolIds,
            dto.CustomTools,
            dto.ContextOptions,
            dto.FileIdsToKeep,
            MergeUpdateFiles(dto.FilesToAdd, dto.Skills),
            dto.Skills,
            dto.ConversationStarters,
            dto.CrewMemberIds  // guides have crews
        );

        // Update auth config JSON
        guide.AuthConfigJson = dto.AuthProviders is { Count: > 0 }
            ? SerializeAuthProviders(dto.AuthProviders)
            : null;
        await SaveProjectEnvironmentAsync(dto.ProjectId, guide.Id, dto.EnvironmentVariables);
        await _context.SaveChangesAsync();
        await StageMcpSandboxSetupIfNeededAsync(dto.ProjectId, guide.Id, dto.CustomTools);

        return new GuideDto(
            guide.Id,
            guide.Name,
            guide.Description ?? string.Empty,
            guide.AvatarImageBytes != null ? $"/api/guides/{guide.Id}/avatar?t={guide.Updated?.Ticks ?? guide.Created.Ticks}" : null,
            guide.ModelId,
            null,
            guide.Tools.Count,
            guide.CrewMembers.Count,
            guide.Created,
            guide.Updated
        );
    }

    public async Task<bool> DeleteGuideAsync(Guid guideId)
    {
        var guide = await _context.Assistants
            .Where(a => a.Id == guideId && a.Kind == AssistantKind.Guide)
            .FirstOrDefaultAsync();

        if (guide == null)
            return false;

        var guideName = guide.Name;

        _context.Assistants.Remove(guide); // Cascade will handle guide-assistant relationships
        await _context.SaveChangesAsync();

        // Invalidate caches to ensure deleted guide is no longer accessible
        AssistantUtility.ClearCache(guideName);
        ThreadRun.ClearRequestBuilderCache(guideName);

        return true;
    }

    public Task<GuideDto> DuplicateGuideAsync(Guid guideId)
    {
        throw new NotImplementedException("DuplicateGuideAsync - to be implemented");
    }

    public async Task<byte[]?> GetGuideAvatarBytesAsync(Guid guideId)
    {
        var guide = await _context.Assistants
            .Where(a => a.Id == guideId && a.Kind == AssistantKind.Guide)
            .FirstOrDefaultAsync();

        return guide?.AvatarImageBytes;
    }

    #endregion

    #region Assistants

    public async Task<IEnumerable<AssistantDto>> GetAssistantsAsync()
    {
        return await _context.Assistants
            .Where(a => a.Kind == AssistantKind.Assistant)
            .OrderBy(a => a.Name)
            .Select(a => new AssistantDto(
                a.Id,
                a.Name,
                a.Description ?? string.Empty,
                a.AvatarImageBytes != null ? $"/api/assistants/{a.Id}/avatar" : null,
                a.ModelId,
                a.Model != null ? a.Model.DisplayName : null,
                a.Tools.Count + a.OpenApiSchemas.Sum(s => s.Operations.Count),
                a.GuideMemberships.Select(ga => ga.Guide.Name).ToList(),
                a.Created,
                a.Updated
            ))
            .ToListAsync();
    }

    public async Task<AssistantDetailsDto?> GetAssistantAsync(Guid assistantId, Guid? projectId = null)
    {
        var assistant = await _context.Assistants
            .Include(a => a.Model)
            .Include(a => a.Tools).ThenInclude(t => t.Tool)
            .Include(a => a.ContextOptions)
            .Include(a => a.OpenApiSchemas).ThenInclude(s => s.AuthProvider).ThenInclude(ap => ap!.Scopes)
            .Include(a => a.OpenApiSchemas).ThenInclude(s => s.Operations)
            .Include(a => a.Files)
            .Include(a => a.SkillMetas)
            .Include(a => a.ConversationStarters)
            .Include(a => a.GuideMemberships).ThenInclude(ga => ga.Guide)
            .Where(a => a.Id == assistantId && a.Kind == AssistantKind.Assistant)
            .FirstOrDefaultAsync();

        if (assistant == null)
            return null;

        var assistantDto = new AssistantDto(
            assistant.Id,
            assistant.Name,
            assistant.Description ?? string.Empty,
            assistant.AvatarImageBytes != null ? $"/api/assistants/{assistant.Id}/avatar" : null,
            assistant.ModelId,
            assistant.Model?.DisplayName,
            assistant.Tools.Count + assistant.OpenApiSchemas.Sum(s => s.Operations.Count),
            [.. assistant.GuideMemberships.Select(ga => ga.Guide.Name)],
            assistant.Created,
            assistant.Updated
        );

        var tools = assistant.Tools.Select(t => new ToolAssignmentDto(
            t.ToolId,
            t.Tool.ToolType,
            t.Tool.DisplayName,
            false
        )).ToList();

        var contextOptions = assistant.ContextOptions.Select(co => new ContextOptionDto(
            co.Key,
            co.Value
        )).ToList();

        // Convert OpenApiSchemas back to CustomToolDto format
        var customTools = assistant.OpenApiSchemas.Select(schema => 
        {
            OpenApiAuthConfigDto? authConfig = null;
            if (schema.AuthProvider != null)
            {
                // Never return the actual ValueTemplate (secret) - mask it if present
                var maskedValue = string.IsNullOrEmpty(schema.AuthProvider.ValueTemplate) 
                    ? null 
                    : "••••••••";
                
                authConfig = new OpenApiAuthConfigDto(
                    schema.AuthProvider.AuthType,
                    schema.AuthProvider.ClientId,
                    schema.AuthProvider.Tenant,
                    [.. schema.AuthProvider.Scopes.Select(s => s.Scope)],
                    maskedValue, // Return masked value for write-only field
                    schema.AuthProvider.HeaderName,
                    schema.AuthProvider.UserConfigPolicy
                );
            }

            var operations = schema.Operations.Select(op => new OpenApiOperationDto(
                op.Id,
                op.OperationId,
                op.Method,
                op.Path,
                op.Summary,
                op.SchemaFragmentJson,
                op.ToolDefinitionJson
            )).ToList();

            return new CustomToolDto(
                schema.Name,
                schema.SpecificationJson,
                schema.ApiHost,
                authConfig,
                operations
            );
        }).ToList();

        // Load non-skill files with markdown shadows
        var files = new List<FileDto>();
        foreach (var f in assistant.Files.Where(file =>
                     !string.Equals(file.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)))
        {
            AssistantFileMarkdownShadowDto? shadowDto = null;
            if (f.FolderKind == "VectorStore")
            {
                var shadow = await _markdownExtractionService.GetAssistantFileMarkdownShadowAsync(f.Id);
                if (shadow != null)
                {
                    shadowDto = new AssistantFileMarkdownShadowDto(
                        shadow.Id,
                        shadow.OriginalAssistantFileId,
                        shadow.ContentHash,
                        shadow.FileSize,
                        shadow.Status,
                        shadow.ErrorMessage,
                        shadow.Created,
                        shadow.ProcessedAt
                    );
                }
            }

            files.Add(new FileDto(
                f.Id,
                f.FolderKind,
                f.VectorStoreName,
                f.RelativePath,
                f.ContentType,
                f.Created,
                shadowDto
            ));
        }

        var skills = assistant.Kind == AssistantKind.Guide
            ? SkillDtoBuilder.BuildFromAssistantFiles(assistant.Files, assistant.SkillMetas)
            : [];

        if (assistant.Kind == AssistantKind.Assistant)
        {
            foreach (var f in assistant.Files.Where(file =>
                         string.Equals(file.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(new FileDto(
                    f.Id,
                    f.FolderKind,
                    f.VectorStoreName,
                    f.RelativePath,
                    f.ContentType,
                    f.Created,
                    null));
            }
        }

        var conversationStarters = assistant.ConversationStarters
            .OrderBy(cs => cs.OrderIndex)
            .Select(cs => new ConversationStarterDto(
                cs.Id,
                cs.Prompt,
                cs.OrderIndex
            )).ToList();

        var environmentVariables = await GetProjectEnvironmentForClientAsync(projectId, assistant.Id);

        return new AssistantDetailsDto(
            assistantDto,
            assistant.Instructions,
            assistant.Temperature,
            assistant.TopP,
            assistant.ReasoningEffort,
            tools,
            contextOptions,
            customTools,
            files,
            conversationStarters
        )
        {
            EnvironmentVariables = environmentVariables.Count > 0 ? environmentVariables : null,
            Skills = skills.Count > 0 ? skills : null
        };
    }

    public async Task<AssistantDto> CreateAssistantAsync(CreateAssistantDto dto)
    {
        var modelParameters = await NormalizeAndValidateModelParametersAsync(
            dto.ModelId,
            dto.Temperature,
            dto.TopP,
            dto.ReasoningEffort,
            dto.SamplingParametersJson);

        var mergedFiles = MergeCreateFiles(dto.Files, dto.Skills);

        var assistant = CreateAssistantEntity(
            AssistantKind.Assistant,
            dto.Name,
            dto.Description,
            dto.Instructions,
            null, // homePageMarkdown - assistants don't have home pages
            dto.ModelId,
            modelParameters.Temperature,
            modelParameters.TopP,
            modelParameters.ReasoningEffort,
            modelParameters.SamplingParametersJson,
            dto.AvatarImageBytes,
            dto.AvatarContentType,
            dto.ToolIds,
            dto.CustomTools,
            dto.ContextOptions,
            mergedFiles,
            dto.ConversationStarters,
            null  // crewMemberIds - assistants don't have crews
        );
        _context.Assistants.Add(assistant);
        await SaveProjectEnvironmentAsync(dto.ProjectId, assistant.Id, dto.EnvironmentVariables);
        await _context.SaveChangesAsync();

        if (dto.Skills is { Count: > 0 })
        {
            await _assistantSkillMetaSync.SyncFromSkillSavesAsync(assistant.Id, dto.Skills);
        }

        // Create markdown shadows for vector store files
        var vectorStoreFiles = assistant.Files.Where(f => f.FolderKind == "VectorStore").ToList();
        foreach (var file in vectorStoreFiles)
        {
            try
            {
                await _markdownExtractionService.CreateAssistantFileMarkdownShadowAsync(file.Id);
                _logger.LogInformation("Created markdown shadow for AssistantFile {AssistantFileId} ({RelativePath})", 
                    file.Id, LogValueSanitizer.Sanitize(file.RelativePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create markdown shadow for AssistantFile {AssistantFileId} ({RelativePath})", 
                    file.Id, LogValueSanitizer.Sanitize(file.RelativePath));
                // Don't fail the creation - extraction will be retried
            }
        }

        return new AssistantDto(
            assistant.Id,
            assistant.Name,
            assistant.Description ?? string.Empty,
            assistant.AvatarImageBytes != null ? $"/api/assistants/{assistant.Id}/avatar?t={assistant.Updated?.Ticks ?? assistant.Created.Ticks}" : null,
            assistant.ModelId,
            null,
            assistant.Tools.Count,
            [], // No crew memberships at creation
            assistant.Created,
            assistant.Updated
        );
    }

    public async Task<AssistantDto> UpdateAssistantAsync(Guid assistantId, UpdateAssistantDto dto)
    {
        var modelParameters = await NormalizeAndValidateModelParametersAsync(
            dto.ModelId,
            dto.Temperature,
            dto.TopP,
            dto.ReasoningEffort,
            dto.SamplingParametersJson);

        // Validate runtime compatibility for all guides this assistant is part of
        var guideMemberships = await _context.GuideMembers
            .Include(gm => gm.Guide)
                .ThenInclude(g => g.CrewMembers)
                    .ThenInclude(cm => cm.Assistant)
            .Where(gm => gm.AssistantId == assistantId)
            .ToListAsync();

        foreach (var membership in guideMemberships)
        {
            var members = new List<GuideRuntimeValidationMember>
            {
                new("guide", membership.GuideId, membership.Guide.Name, membership.Guide.ModelId)
            };

            foreach (var cm in membership.Guide.CrewMembers)
            {
                if (cm.AssistantId == assistantId)
                {
                    members.Add(new GuideRuntimeValidationMember("assistant", assistantId, dto.Name, dto.ModelId));
                }
                else if (cm.Assistant != null)
                {
                    members.Add(new GuideRuntimeValidationMember("assistant", cm.AssistantId, cm.Assistant.Name, cm.Assistant.ModelId));
                }
            }

            var validation = await ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest(members));
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"Cannot update assistant because it would break compatibility in guide '{membership.Guide.Name}': {string.Join(" ", validation.Conflicts)}");
            }
        }

        var assistant = await UpdateAssistantEntityAsync(
            assistantId,
            AssistantKind.Assistant,
            dto.Name,
            dto.Description,
            dto.Instructions,
            null, // homePageMarkdown - assistants don't have home pages
            dto.ModelId,
            modelParameters.Temperature,
            modelParameters.TopP,
            modelParameters.ReasoningEffort,
            modelParameters.SamplingParametersJson,
            dto.AvatarImageBytes,
            dto.AvatarContentType,
            dto.ToolIds,
            dto.CustomTools,
            dto.ContextOptions,
            dto.FileIdsToKeep,
            MergeUpdateFiles(dto.FilesToAdd, dto.Skills),
            dto.Skills,
            dto.ConversationStarters,
            null  // crewMemberIds - assistants don't have crews
        );
        await SaveProjectEnvironmentAsync(dto.ProjectId, assistant.Id, dto.EnvironmentVariables);
        await _context.SaveChangesAsync();

        return new AssistantDto(
            assistant.Id,
            assistant.Name,
            assistant.Description ?? string.Empty,
            assistant.AvatarImageBytes != null ? $"/api/assistants/{assistant.Id}/avatar?t={assistant.Updated?.Ticks ?? assistant.Created.Ticks}" : null,
            assistant.ModelId,
            null,
            assistant.Tools.Count,
            [.. assistant.GuideMemberships.Select(ga => ga.Guide.Name)],
            assistant.Created,
            assistant.Updated
        );
    }

    public async Task<bool> DeleteAssistantAsync(Guid assistantId)
    {
        var assistant = await _context.Assistants
            .Where(a => a.Id == assistantId && a.Kind == AssistantKind.Assistant)
            .FirstOrDefaultAsync();

        if (assistant == null)
            return false;

        var assistantName = assistant.Name;

        _context.Assistants.Remove(assistant);
        await _context.SaveChangesAsync();

        // Invalidate caches to ensure deleted assistant is no longer accessible
        AssistantUtility.ClearCache(assistantName);
        ThreadRun.ClearRequestBuilderCache(assistantName);

        return true;
    }

    public Task<AssistantDto> DuplicateAssistantAsync(Guid assistantId)
    {
        throw new NotImplementedException("DuplicateAssistantAsync - to be implemented");
    }

    public async Task<byte[]?> GetAssistantAvatarBytesAsync(Guid assistantId)
    {
        var assistant = await _context.Assistants
            .Where(a => a.Id == assistantId && a.Kind == AssistantKind.Assistant)
            .FirstOrDefaultAsync();

        return assistant?.AvatarImageBytes;
    }

    #endregion

    #region OpenAPI Operations

    public async Task<OpenApiOperationDto?> GetOperationAsync(Guid operationId)
    {
        var operation = await _context.AssistantOpenApiOperations
            .Include(o => o.Schema)
            .ThenInclude(s => s.Assistant)
            .Where(o => o.Id == operationId)
            .FirstOrDefaultAsync();

        if (operation == null)
            return null;

        return new OpenApiOperationDto(
            operation.Id,
            operation.OperationId,
            operation.Method,
            operation.Path,
            operation.Summary,
            operation.SchemaFragmentJson,
            operation.ToolDefinitionJson
        );
    }

    public async Task<OpenApiOperationDto> UpdateOperationAsync(Guid operationId, UpdateOperationDto dto)
    {
        var operation = await _context.AssistantOpenApiOperations
            .Include(o => o.Schema)
            .ThenInclude(s => s.Assistant)
            .Where(o => o.Id == operationId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"Operation {operationId} not found");

        // Parse the schema fragment to extract metadata and regenerate tool definition
        try
        {
            var fragment = JsonDocument.Parse(dto.SchemaFragmentJson);
            var root = fragment.RootElement;

            // Extract path, method, and operation details
            var path = root.GetProperty("path").GetString() ?? operation.Path;
            var method = root.GetProperty("method").GetString()?.ToUpper() ?? operation.Method;
            var operationElement = root.GetProperty("operation");

            // Extract summary
            var summary = operationElement.TryGetProperty("summary", out var summaryEl)
                ? summaryEl.GetString()
                : operationElement.TryGetProperty("description", out var descEl)
                ? descEl.GetString()
                : operation.Summary;

            // Build OpenAPI spec using the parent schema server URL (not a hardcoded placeholder)
            var tempSpecJson = ToolSourceValidator.BuildSingleOperationOpenApiSpec(
                operation.Schema.SpecificationJson,
                path,
                method,
                operationElement);
            var toolDefinitions = OpenApiHelper.GetToolDefinitionsFromJson(tempSpecJson);

            var toolDef = toolDefinitions.FirstOrDefault() ?? throw new InvalidOperationException("Failed to generate tool definition from schema fragment");
            var toolDefJson = JsonSerializer.Serialize(toolDef);

            // Update the operation
            operation.Path = path;
            operation.Method = method;
            operation.Summary = summary;
            operation.SchemaFragmentJson = dto.SchemaFragmentJson;
            operation.ToolDefinitionJson = toolDefJson;
            operation.Updated = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return new OpenApiOperationDto(
                operation.Id,
                operation.OperationId,
                operation.Method,
                operation.Path,
                operation.Summary,
                operation.SchemaFragmentJson,
                operation.ToolDefinitionJson
            );
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to update operation: {ex.Message}", ex);
        }
    }

    public Task<ToolDefinitionPreviewResultDto> PreviewToolDefinitionAsync(PreviewToolDefinitionDto dto)
    {
        try
        {
            var result = ToolSourceValidator.PreviewOperation(dto.SchemaFragmentJson, dto.OpenApiSpecJson);
            return Task.FromResult(result);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to preview tool definition: {ex.Message}", ex);
        }
    }

    #endregion

    #region Validation

    public async Task<GuideRuntimeValidationDto> ValidateRuntimeCompatibilityAsync(GuideRuntimeValidationRequest request)
    {
        var result = new GuideRuntimeValidationDto(
            IsValid: true,
            Profiles: new List<string>(),
            Conflicts: new List<string>(),
            Warnings: new List<string>()
        );

        if (request.Members == null || request.Members.Count == 0)
        {
            return result;
        }

        var modelIds = request.Members
            .Where(m => !string.IsNullOrEmpty(m.ModelId))
            .Select(m => m.ModelId!)
            .Distinct()
            .ToList();

        if (modelIds.Count == 0)
        {
            return result;
        }

        var models = await _context.Models
            .Where(m => modelIds.Contains(m.ModelId))
            .ToListAsync();

        var localModels = models.Where(m => m.Provider == "llama-cpp").ToList();
        if (localModels.Count == 0)
        {
            return result;
        }

        foreach (var model in localModels)
        {
            try
            {
                var runtime = LocalRuntimeConfigurationParser.ParseRequired(model.ModelId, model.RuntimeConfigJson);
                var runtimeProfile = await _runtimeProfileResolver.ResolveAsync(runtime.RuntimeProfileId);

                if (!result.Profiles.Contains(runtimeProfile.ProfileId, StringComparer.OrdinalIgnoreCase))
                {
                    result.Profiles.Add(runtimeProfile.ProfileId);
                }
            }
            catch (InvalidOperationException ex)
            {
                result.Warnings.Add($"Model {model.DisplayName} has invalid local runtime configuration: {ex.Message}");
            }
        }

        return result;
    }

    #endregion

    #region Helper Methods

    private async Task<NormalizedModelParameters> NormalizeAndValidateModelParametersAsync(
        string? modelId,
        float? temperature,
        double? topP,
        string? reasoningEffort,
        string? samplingParametersJson = null)
    {
        Dictionary<string, double>? samplingOverrides = null;
        if (!string.IsNullOrWhiteSpace(samplingParametersJson))
        {
            try
            {
                samplingOverrides = JsonSerializer.Deserialize<Dictionary<string, double>>(
                    samplingParametersJson, JsonCaseInsensitiveOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "SamplingParametersJson must be a valid JSON object mapping parameter names to numeric values.", ex);
            }
        }

        var hasAnyParameter = temperature.HasValue || topP.HasValue
            || !string.IsNullOrWhiteSpace(reasoningEffort)
            || samplingOverrides is { Count: > 0 };
        if (!hasAnyParameter)
        {
            return NormalizedModelParameters.Empty;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                throw new InvalidOperationException("ModelId is required when reasoningEffort is specified.");
            }

            return NormalizedModelParameters.Empty;
        }

        var model = await _context.Models
            .AsNoTracking()
            .Where(m => m.ModelId == modelId)
            .Select(m => new
            {
                m.ModelId,
                m.Provider,
                m.ReasoningChoicesJson,
                m.RuntimeConfigJson
            })
            .FirstOrDefaultAsync();

        if (model == null)
        {
            throw new InvalidOperationException($"Model '{modelId}' was not found.");
        }

        if (string.Equals(model.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            var runtime = LocalRuntimeConfigurationParser.ParseRequired(model.ModelId, model.RuntimeConfigJson);
            var profile = await _runtimeProfileResolver.ResolveAsync(runtime.RuntimeProfileId);

            ValidateSamplingParameters(model.ModelId, profile, temperature, topP, samplingOverrides);

            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                ValidateReasoningEffortChoice(model.ModelId, model.ReasoningChoicesJson, profile, reasoningEffort);
            }

            return new NormalizedModelParameters(
                temperature,
                topP,
                NormalizeReasoningEffort(reasoningEffort),
                string.IsNullOrWhiteSpace(samplingParametersJson) ? null : samplingParametersJson);
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            ValidateCatalogReasoningEffortChoice(model.ModelId, model.ReasoningChoicesJson, reasoningEffort);
        }

        return new NormalizedModelParameters(
            Temperature: null,
            TopP: null,
            ReasoningEffort: NormalizeReasoningEffort(reasoningEffort),
            SamplingParametersJson: null);
    }

    private static string? NormalizeReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        var trimmed = reasoningEffort.Trim();
        return string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private sealed record NormalizedModelParameters(
        float? Temperature,
        double? TopP,
        string? ReasoningEffort,
        string? SamplingParametersJson)
    {
        public static NormalizedModelParameters Empty { get; } = new(null, null, null, null);
    }

    private static void ValidateSamplingParameters(
        string modelId,
        RuntimeProfileData profile,
        float? temperature,
        double? topP,
        Dictionary<string, double>? samplingOverrides = null)
    {
        if (temperature.HasValue)
        {
            ValidateParameterRange(modelId, profile, "temperature", temperature.Value);
        }

        if (topP.HasValue)
        {
            ValidateParameterRange(modelId, profile, "top_p", topP.Value);
        }

        if (samplingOverrides is { Count: > 0 })
        {
            foreach (var (key, value) in samplingOverrides)
            {
                ValidateParameterRange(modelId, profile, key, value);
            }
        }
    }

    private static void ValidateParameterRange(string modelId, RuntimeProfileData profile, string key, double value)
    {
        if (!profile.SamplingParameters.TryGetValue(key, out var definition))
        {
            throw new InvalidOperationException(
                $"Parameter '{key}' is not supported by model '{modelId}'.");
        }

        if (value < definition.Min || value > definition.Max)
        {
            throw new InvalidOperationException(
                $"Parameter '{key}' value {value} is out of range for model '{modelId}'. Valid range: {definition.Min} to {definition.Max}.");
        }
    }

    private static void ValidateReasoningEffortChoice(
        string modelId,
        string? reasoningChoicesJson,
        RuntimeProfileData profile,
        string reasoningEffort)
    {
        var profileChoices = profile.ThinkingControl.ChoiceActions?.Keys.ToList() ?? [];
        var modelChoices = ParseReasoningChoices(reasoningChoicesJson);

        var validChoices = modelChoices.Count > 0
            ? profileChoices.Where(c => modelChoices.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList()
            : profileChoices;

        if (validChoices.Count == 0)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' does not support reasoningEffort.");
        }

        if (!validChoices.Any(c => string.Equals(c, reasoningEffort, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Reasoning effort '{reasoningEffort}' is invalid for model '{modelId}'. Valid values: {string.Join(", ", validChoices)}");
        }
    }

    private static List<string> ParseReasoningChoices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonCaseInsensitiveOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void ValidateCatalogReasoningEffortChoice(string modelId, string? reasoningChoicesJson, string reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningChoicesJson))
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' does not define reasoning choices. Configure ReasoningChoicesJson in the model catalog.");
        }

        List<string>? choices;
        try
        {
            choices = JsonSerializer.Deserialize<List<string>>(reasoningChoicesJson, JsonCaseInsensitiveOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' has invalid ReasoningChoicesJson. Expected a JSON array of strings.", ex);
        }

        if (choices == null || choices.Count == 0)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' has no reasoning choices configured.");
        }

        if (!choices.Any(choice => string.Equals(choice, reasoningEffort, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Reasoning effort '{reasoningEffort}' is invalid for model '{modelId}'. Valid values: {string.Join(", ", choices)}");
        }
    }

    /// <summary>
    /// Core logic for creating an assistant or guide entity. Handles all shared logic.
    /// </summary>
    private Assistant CreateAssistantEntity(
        AssistantKind kind,
        string name,
        string description,
        string? instructions,
        string? homePageMarkdown, // Only used for guides
        string? modelId,
        float? temperature,
        double? topP,
        string? reasoningEffort,
        string? samplingParametersJson,
        byte[]? avatarImageBytes,
        string? avatarContentType,
        List<Guid>? toolIds,
        List<CustomToolDto>? customTools,
        List<ContextOptionDto>? contextOptions,
        List<FileUploadDto>? files,
        List<string>? conversationStarters,
        List<Guid>? crewMemberIds) // Only used for guides
    {
        var assistant = new Assistant
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = name,
            Description = description,
            Instructions = instructions,
            HomePageMarkdown = kind == AssistantKind.Guide ? homePageMarkdown : null,
            ModelId = modelId,
            Temperature = temperature,
            TopP = topP,
            ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim(),
            SamplingParametersJson = string.IsNullOrWhiteSpace(samplingParametersJson) ? null : samplingParametersJson,
            AvatarImageBytes = avatarImageBytes,
            AvatarContentType = avatarContentType,
            Created = DateTime.UtcNow
        };

        // Add tool assignments
        if (toolIds != null)
        {
            foreach (var toolId in toolIds)
            {
                assistant.Tools.Add(new AssistantTool
                {
                    AssistantId = assistant.Id,
                    ToolId = toolId
                });
            }
        }

        // Add custom tools (OpenAPI schemas with optional auth)
        if (customTools != null)
        {
            var duplicateSchemaNames = customTools
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicateSchemaNames.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Duplicate OpenAPI schema names are not allowed: {string.Join(", ", duplicateSchemaNames)}");
            }

            ValidateMcpAssistantConstraints(customTools);

            foreach (var customTool in customTools)
            {
                AssistantAuthProvider? authProvider = null;
                
                // Create auth provider if configured
                if (customTool.AuthConfig != null)
                {
                    if (string.IsNullOrEmpty(customTool.ApiHost))
                        throw new InvalidOperationException(
                            $"OpenAPI schema '{customTool.Name}' has an auth configuration but no ApiHost. ApiHost is required for auth.");

                    authProvider = new AssistantAuthProvider
                    {
                        Id = Guid.NewGuid(),
                        ProviderId = customTool.ApiHost,
                        AuthType = customTool.AuthConfig.AuthType,
                        ClientId = customTool.AuthConfig.ClientId,
                        Tenant = customTool.AuthConfig.Tenant,
                        HeaderName = customTool.AuthConfig.HeaderName,
                        ValueTemplate = customTool.AuthConfig.ValueTemplate,
                        UserConfigPolicy = customTool.AuthConfig.UserConfigPolicy ?? "none" // Default to "none"
                    };

                    if (customTool.AuthConfig.Scopes != null)
                    {
                        foreach (var scope in customTool.AuthConfig.Scopes)
                        {
                            authProvider.Scopes.Add(new AssistantAuthScope
                            {
                                AssistantAuthProviderId = authProvider.Id,
                                Scope = scope
                            });
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(customTool.Name))
                    throw new ArgumentException("Custom tool name is required.");
                if (string.IsNullOrWhiteSpace(customTool.ApiHost))
                    throw new ArgumentException($"API Host is required for custom tool '{customTool.Name}'.");
                if (string.IsNullOrWhiteSpace(customTool.OpenApiSpec))
                    throw new ArgumentException($"OpenAPI specification is required for custom tool '{customTool.Name}'.");

                var normalizedOpenApiSpec = ToolSourceValidator.NormalizeDescriptor(customTool.OpenApiSpec);

                ToolSourceValidator.EnsurePublishableOrThrow(normalizedOpenApiSpec, customTool.Name);

                var schema = new AssistantOpenApiSchema
                {
                    Id = Guid.NewGuid(),
                    AssistantId = assistant.Id,
                    Name = customTool.Name,
                    ApiHost = customTool.ApiHost,
                    SpecificationJson = normalizedOpenApiSpec,
                    AuthProvider = authProvider
                };

                // Parse operations from the OpenAPI spec
                List<AssistantOpenApiOperation> operations;
                try
                {
                    operations = ParseOpenApiOperations(schema.Id, normalizedOpenApiSpec, _logger);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to parse OpenAPI operations for assistant {AssistantId} schema {SchemaId} custom tool {ToolName}",
                        assistant.Id,
                        schema.Id,
                        LogValueSanitizer.Sanitize(customTool.Name));
                    throw;
                }
                foreach (var operation in operations)
                {
                    schema.Operations.Add(operation);
                }

                assistant.OpenApiSchemas.Add(schema);
            }
        }

        // Add context options
        if (contextOptions != null)
        {
            foreach (var option in contextOptions)
            {
                assistant.ContextOptions.Add(new AssistantContextOption
                {
                    AssistantId = assistant.Id,
                    Key = option.Key,
                    Value = option.Value
                });
            }
        }

        // Auto-enable "Set Context Options" tool if any context options have empty values
        Guid setContextOptionsToolId = new ("b0000000-0000-0000-0000-00000000000c");
        
        var hasEmptyContextOptions = contextOptions?.Any(opt => string.IsNullOrWhiteSpace(opt.Value)) ?? false;
        
        if (hasEmptyContextOptions && !(toolIds?.Contains(setContextOptionsToolId) ?? false))
        {
            // Add the tool if not already present
            assistant.Tools.Add(new AssistantTool
            {
                AssistantId = assistant.Id,
                ToolId = setContextOptionsToolId
            });
        }

        // Add files
        if (files != null && files.Count > 0)
        {
            foreach (var fileDto in files)
            {
                // Validate file data
                if (fileDto.ContentBytes == null || fileDto.ContentBytes.Length == 0)
                {
                    _logger.LogWarning("Skipping file upload with empty content: {RelativePath}", LogValueSanitizer.Sanitize(fileDto.RelativePath));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fileDto.RelativePath))
                {
                    _logger.LogWarning("Skipping file upload with empty relative path");
                    continue;
                }

                try
                {
                    assistant.Files.Add(new AssistantFile
                    {
                        Id = Guid.NewGuid(),
                        AssistantId = assistant.Id,
                        FolderKind = fileDto.FolderKind,
                        VectorStoreName = fileDto.VectorStoreName,
                        RelativePath = fileDto.RelativePath,
                        ContentBytes = fileDto.ContentBytes,
                        ContentType = fileDto.ContentType,
                        Created = DateTime.UtcNow
                    });
                    
                    _logger.LogInformation("Added file to create queue: {RelativePath} ({Size} bytes, FolderKind: {FolderKind})", 
                        LogValueSanitizer.Sanitize(fileDto.RelativePath), fileDto.ContentBytes.Length, LogValueSanitizer.Sanitize(fileDto.FolderKind));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to add file to create queue: {RelativePath}", LogValueSanitizer.Sanitize(fileDto.RelativePath));
                    throw new InvalidOperationException($"Failed to upload file '{fileDto.RelativePath}': {ex.Message}", ex);
                }
            }
        }

        GuideExecutablePayload.EnsureRunPythonToolForSkillPayload(assistant);
        GuideExecutablePayload.EnsureFilesContextOption(assistant);

        // Add conversation starters
        if (conversationStarters != null)
        {
            for (int i = 0; i < conversationStarters.Count; i++)
            {
                assistant.ConversationStarters.Add(new AssistantConversationStarter
                {
                    Id = Guid.NewGuid(),
                    AssistantId = assistant.Id,
                    Prompt = conversationStarters[i],
                    OrderIndex = i
                });
            }
        }

        // Add crew members (guides only)
        if (kind == AssistantKind.Guide && crewMemberIds is { Count: > 0 })
        {
            foreach (var crewAssistantId in crewMemberIds)
            {
                assistant.CrewMembers.Add(new GuideMember
                {
                    GuideId = assistant.Id,
                    AssistantId = crewAssistantId,
                    DisplayOrder = crewMemberIds.IndexOf(crewAssistantId)
                });
            }
        }

        return assistant;
    }

    /// <summary>
    /// Core logic for updating an assistant or guide entity. Handles all shared logic.
    /// Returns the updated entity for caller to build response DTOs.
    /// </summary>
    private async Task<Assistant> UpdateAssistantEntityAsync(
        Guid assistantId,
        AssistantKind kind,
        string name,
        string description,
        string? instructions,
        string? homePageMarkdown, // Only used for guides
        string? modelId,
        float? temperature,
        double? topP,
        string? reasoningEffort,
        string? samplingParametersJson,
        byte[]? avatarImageBytes,
        string? avatarContentType,
        List<Guid>? toolIds,
        List<CustomToolDto>? customTools,
        List<ContextOptionDto>? contextOptions,
        List<Guid>? fileIdsToKeep,
        List<FileUploadDto>? filesToAdd,
        List<AssistantSkillSaveDto>? skills,
        List<string>? conversationStarters,
        List<Guid>? crewMemberIds) // Only used for guides
    {
        // Load ONLY the entity, no child collections
        var assistant = await _context.Assistants
            .Where(a => a.Id == assistantId && a.Kind == kind)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"{kind} {assistantId} not found");

        // Update ONLY the main entity properties
        assistant.Name = name;
        assistant.Description = description;
        assistant.Instructions = instructions;
        assistant.HomePageMarkdown = kind == AssistantKind.Guide ? homePageMarkdown : null;
        assistant.ModelId = modelId;
        assistant.Temperature = temperature;
        assistant.TopP = topP;
        assistant.ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
        assistant.SamplingParametersJson = string.IsNullOrWhiteSpace(samplingParametersJson) ? null : samplingParametersJson;
        assistant.Updated = DateTime.UtcNow;

        // Update avatar: empty array clears it, non-empty sets it, null leaves unchanged
        if (avatarImageBytes != null)
        {
            if (avatarImageBytes.Length > 0)
            {
                assistant.AvatarImageBytes = avatarImageBytes;
                assistant.AvatarContentType = avatarContentType;
            }
            else
            {
                // Empty array means clear the avatar
                assistant.AvatarImageBytes = null;
                assistant.AvatarContentType = null;
            }
        }

        await _context.SaveChangesAsync();

        // Now handle child collections using ExecuteDeleteAsync to avoid change tracking issues
        
        // Delete and recreate Tools
        if (toolIds != null)
        {
            await _context.AssistantTools.Where(t => t.AssistantId == assistantId).ExecuteDeleteAsync();
            foreach (var toolId in toolIds)
            {
                await _context.AssistantTools.AddAsync(new AssistantTool
                {
                    AssistantId = assistantId,
                    ToolId = toolId
                });
            }
        }

        // Delete and recreate OpenAPI Schemas (custom tools with optional auth)
        if (customTools != null)
        {
            // --- Validate everything BEFORE touching the database ---

            var duplicateSchemaNames = customTools
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicateSchemaNames.Count > 0)
                throw new InvalidOperationException(
                    $"Duplicate OpenAPI schema names are not allowed: {string.Join(", ", duplicateSchemaNames)}");

            ValidateMcpAssistantConstraints(customTools);

            foreach (var tool in customTools)
            {
                if (string.IsNullOrEmpty(tool.ApiHost))
                    throw new InvalidOperationException(
                        $"OpenAPI schema '{tool.Name}' is missing a server URL / ApiHost.");
            }

            // Fetch existing schemas to build secret lookup dictionaries
            var existingSchemas = await _context.AssistantOpenApiSchemas
                .Include(s => s.AuthProvider)
                .Where(s => s.AssistantId == assistantId)
                .ToListAsync();

            // 1. By ProviderId — primary match (DomainAuth uses ProviderId at runtime)
            var existingSecretsByProviderId = existingSchemas
                .Where(s => s.AuthProvider != null && !string.IsNullOrEmpty(s.AuthProvider.ValueTemplate))
                .GroupBy(s => s.AuthProvider!.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().AuthProvider!.ValueTemplate!,
                    StringComparer.OrdinalIgnoreCase
                );

            // 2. By ApiHost — secondary match when ProviderId doesn't align
            var existingSecretsByApiHost = existingSchemas
                .Where(s => s.AuthProvider != null && !string.IsNullOrEmpty(s.ApiHost) && !string.IsNullOrEmpty(s.AuthProvider.ValueTemplate))
                .GroupBy(s => s.ApiHost, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().AuthProvider!.ValueTemplate!,
                    StringComparer.OrdinalIgnoreCase
                );

            // Stage deletions via EF change tracking so they are part of the same
            // SaveChangesAsync transaction as the inserts. ExecuteDeleteAsync commits
            // immediately outside that transaction — if SaveChangesAsync later fails the
            // schemas are gone permanently. RemoveRange keeps everything atomic.
            var existingAuthProviders = existingSchemas
                .Where(s => s.AuthProvider != null)
                .Select(s => s.AuthProvider!)
                .ToList();
            _context.AssistantOpenApiSchemas.RemoveRange(existingSchemas);
            _context.AssistantAuthProviders.RemoveRange(existingAuthProviders);
            
            for (int index = 0; index < customTools.Count; index++)
            {
                var customTool = customTools[index];
                AssistantAuthProvider? authProvider = null;
                
                // Create auth provider if configured
                if (customTool.AuthConfig != null)
                {
                    if (string.IsNullOrWhiteSpace(customTool.ApiHost))
                        throw new ArgumentException($"API Host is required for custom tool '{customTool.Name}' when auth is configured.");

                    var providerId = customTool.ApiHost;
                    var valueTemplate = customTool.AuthConfig.ValueTemplate;
                    
                    // If ValueTemplate is the masked sentinel, resolve it from existing secrets
                    const string maskedValue = "••••••••";
                    if (valueTemplate == maskedValue)
                    {
                        // Match by ProviderId (primary — this is the runtime matching key used by DomainAuth)
                        if (existingSecretsByProviderId.TryGetValue(providerId, out var secretByProviderId))
                        {
                            valueTemplate = secretByProviderId;
                        }
                        // Match by ApiHost (secondary — handles cases where the ProviderId record changed)
                        else if (existingSecretsByApiHost.TryGetValue(customTool.ApiHost, out var secretByApiHost))
                        {
                            valueTemplate = secretByApiHost;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Could not find existing secret for masked ValueTemplate. Schema: {SchemaName}, ApiHost: {ApiHost}. Auth will be cleared.",
                                LogValueSanitizer.Sanitize(customTool.Name),
                                LogValueSanitizer.Sanitize(customTool.ApiHost)
                            );
                            valueTemplate = null;
                        }
                    }
                    
                    // Never write the mask to the database
                    if (valueTemplate == maskedValue)
                    {
                        _logger.LogError(
                            "Attempted to save masked value to database. Schema: {SchemaName}, ApiHost: {ApiHost}. Setting to null.",
                            LogValueSanitizer.Sanitize(customTool.Name),
                            LogValueSanitizer.Sanitize(customTool.ApiHost)
                        );
                        valueTemplate = null;
                    }
                    
                    authProvider = new AssistantAuthProvider
                    {
                        ProviderId = providerId,
                        AuthType = customTool.AuthConfig.AuthType,
                        ClientId = customTool.AuthConfig.ClientId,
                        Tenant = customTool.AuthConfig.Tenant,
                        HeaderName = customTool.AuthConfig.HeaderName,
                        // Never save the mask as the actual value - explicit validation ensures this
                        ValueTemplate = valueTemplate,
                        UserConfigPolicy = customTool.AuthConfig.UserConfigPolicy ?? "none" // Default to "none"
                    };

                    if (customTool.AuthConfig.Scopes != null)
                    {
                        foreach (var scope in customTool.AuthConfig.Scopes)
                        {
                            authProvider.Scopes.Add(new AssistantAuthScope
                            {
                                AssistantAuthProviderId = authProvider.Id,
                                Scope = scope
                            });
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(customTool.Name))
                    throw new ArgumentException("Custom tool name is required.");
                if (string.IsNullOrWhiteSpace(customTool.ApiHost))
                    throw new ArgumentException($"API Host is required for custom tool '{customTool.Name}'.");
                if (string.IsNullOrWhiteSpace(customTool.OpenApiSpec))
                    throw new ArgumentException($"OpenAPI specification is required for custom tool '{customTool.Name}'.");

                var normalizedOpenApiSpec = ToolSourceValidator.NormalizeDescriptor(customTool.OpenApiSpec);

                ToolSourceValidator.EnsurePublishableOrThrow(normalizedOpenApiSpec, customTool.Name);

                var schema = new AssistantOpenApiSchema
                {
                    Id = Guid.NewGuid(),
                    AssistantId = assistantId,
                    Name = customTool.Name,
                    ApiHost = customTool.ApiHost,
                    SpecificationJson = normalizedOpenApiSpec,
                    AuthProvider = authProvider
                };

                // Parse operations from the OpenAPI spec
                // Note: Operations are parsed from the full SpecificationJson, not from individual operation rows
                // The AssistantOpenApiOperations table is primarily for UI editing/preview
                List<AssistantOpenApiOperation> operations;
                try
                {
                    operations = ParseOpenApiOperations(schema.Id, normalizedOpenApiSpec, _logger);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to parse OpenAPI operations for assistant {AssistantId} schema {SchemaId} custom tool {ToolName}",
                        assistantId,
                        schema.Id,
                        LogValueSanitizer.Sanitize(customTool.Name));
                    throw;
                }
                foreach (var operation in operations)
                {
                    schema.Operations.Add(operation);
                }

                await _context.AssistantOpenApiSchemas.AddAsync(schema);
            }
        }

        // Delete and recreate Context Options
        if (contextOptions != null)
        {
            await _context.AssistantContextOptions.Where(o => o.AssistantId == assistantId).ExecuteDeleteAsync();
            foreach (var option in contextOptions)
            {
                await _context.AssistantContextOptions.AddAsync(new AssistantContextOption
                {
                    AssistantId = assistantId,
                    Key = option.Key,
                    Value = option.Value
                });
            }

            // Auto-enable "Set Context Options" tool if any context options have empty values
            var setContextOptionsToolId = new Guid("b0000000-0000-0000-0000-00000000000c");
            var hasEmptyContextOptions = contextOptions.Any(opt => string.IsNullOrWhiteSpace(opt.Value));
            
            if (hasEmptyContextOptions && !(toolIds?.Contains(setContextOptionsToolId) ?? false))
            {
                // Add the tool if not already present
                await _context.AssistantTools.AddAsync(new AssistantTool
                {
                    AssistantId = assistantId,
                    ToolId = setContextOptionsToolId
                });
            }
        }

        // Update Files incrementally (non-skill files via fileIdsToKeep; skills via skills DTO)
        if (fileIdsToKeep != null || filesToAdd != null || skills != null)
        {
            SkillDtoBuilder.ValidateSkillSavePayload(skills);

            if (fileIdsToKeep != null || skills != null)
            {
                var skillFileIdsToKeep = SkillDtoBuilder.CollectSkillFileIdsToKeep(skills);
                var existingFiles = await _context.AssistantFiles
                    .Where(f => f.AssistantId == assistantId)
                    .Select(f => new { f.Id, f.FolderKind })
                    .ToListAsync();

                var fileIdsToDelete = existingFiles
                    .Where(f =>
                    {
                        var isSkill = string.Equals(
                            f.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase);
                        if (isSkill)
                        {
                            if (kind == AssistantKind.Assistant)
                            {
                                return fileIdsToKeep != null && !fileIdsToKeep.Contains(f.Id);
                            }

                            return skills != null && !skillFileIdsToKeep.Contains(f.Id);
                        }

                        return fileIdsToKeep != null && !fileIdsToKeep.Contains(f.Id);
                    })
                    .Select(f => f.Id)
                    .ToList();

                if (fileIdsToDelete.Count > 0)
                {
                    await _context.DocumentChunks
                        .Where(dc => dc.AssistantFileId.HasValue
                                     && fileIdsToDelete.Contains(dc.AssistantFileId.Value))
                        .ExecuteDeleteAsync();

                    await _context.AssistantFiles
                        .Where(f => fileIdsToDelete.Contains(f.Id))
                        .ExecuteDeleteAsync();
                }
            }
            
            // Add new files
            if (filesToAdd != null && filesToAdd.Count > 0)
            {
                var newlyAddedFiles = new List<AssistantFile>();
                foreach (var fileDto in filesToAdd)
                {
                    // Validate file data
                    if (fileDto.ContentBytes == null || fileDto.ContentBytes.Length == 0)
                    {
                        _logger.LogWarning("Skipping file upload with empty content: {RelativePath}", LogValueSanitizer.Sanitize(fileDto.RelativePath));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(fileDto.RelativePath))
                    {
                        _logger.LogWarning("Skipping file upload with empty relative path");
                        continue;
                    }

                    try
                    {
                        var assistantFile = new AssistantFile
                        {
                            AssistantId = assistantId,
                            FolderKind = fileDto.FolderKind,
                            VectorStoreName = fileDto.VectorStoreName,
                            RelativePath = fileDto.RelativePath,
                            ContentBytes = fileDto.ContentBytes,
                            ContentType = fileDto.ContentType
                        };
                        
                        await _context.AssistantFiles.AddAsync(assistantFile);
                        newlyAddedFiles.Add(assistantFile);
                        
                        _logger.LogInformation("Added file to upload queue: {RelativePath} ({Size} bytes, FolderKind: {FolderKind})", 
                            LogValueSanitizer.Sanitize(fileDto.RelativePath), fileDto.ContentBytes.Length, LogValueSanitizer.Sanitize(fileDto.FolderKind));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to add file to upload queue: {RelativePath}", LogValueSanitizer.Sanitize(fileDto.RelativePath));
                        throw new InvalidOperationException($"Failed to upload file '{fileDto.RelativePath}': {ex.Message}", ex);
                    }
                }
                
                if (newlyAddedFiles.Count > 0)
                {
                    // Save to get file IDs
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Successfully saved {Count} file(s) for assistant {AssistantId}", newlyAddedFiles.Count, assistantId);
                }
                
                // Trigger markdown extraction for VectorStore files
                foreach (var file in newlyAddedFiles.Where(f => f.FolderKind == "VectorStore"))
                {
                    try
                    {
                        await _markdownExtractionService.CreateAssistantFileMarkdownShadowAsync(file.Id);
                        _logger.LogInformation("Created markdown shadow for AssistantFile {AssistantFileId} ({RelativePath})", 
                            file.Id, LogValueSanitizer.Sanitize(file.RelativePath));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create markdown shadow for AssistantFile {AssistantFileId} ({RelativePath})", 
                            file.Id, LogValueSanitizer.Sanitize(file.RelativePath));
                        // Don't fail the update - extraction will be retried
                    }
                }
            }

            if (skills is { Count: > 0 })
            {
                await _assistantSkillMetaSync.SyncFromSkillSavesAsync(assistantId, skills);
            }
            else
            {
                await _assistantSkillMetaSync.SyncAssistantAsync(assistantId);
            }
        }

        // Delete and recreate Conversation Starters
        if (conversationStarters != null)
        {
            await _context.AssistantConversationStarters.Where(c => c.AssistantId == assistantId).ExecuteDeleteAsync();
            for (int i = 0; i < conversationStarters.Count; i++)
            {
                await _context.AssistantConversationStarters.AddAsync(new AssistantConversationStarter
                {
                    AssistantId = assistantId,
                    Prompt = conversationStarters[i],
                    OrderIndex = i
                });
            }
        }

        // Delete and recreate Crew Members (guides only)
        if (kind == AssistantKind.Guide && crewMemberIds != null)
        {
            await _context.GuideMembers.Where(m => m.GuideId == assistantId).ExecuteDeleteAsync();
            for (int i = 0; i < crewMemberIds.Count; i++)
            {
                await _context.GuideMembers.AddAsync(new GuideMember
                {
                    GuideId = assistantId,
                    AssistantId = crewMemberIds[i],
                    DisplayOrder = i
                });
            }
        }

        if (filesToAdd is { Count: > 0 }
            && GuideExecutablePayload.NewUploadsHaveNotebookPayload(filesToAdd))
        {
            await GuideExecutablePayload.EnsureFilesContextOptionAsync(_context, assistantId);
        }

        await GuideExecutablePayload.EnsureRunPythonToolForSkillPayloadAsync(_context, assistantId);

        await _context.SaveChangesAsync();

        // Invalidate caches to ensure updated assistant definition and OpenAPI schemas are reloaded
        AssistantUtility.ClearCache(assistant.Name);
        ThreadRun.ClearRequestBuilderCache(assistant.Name);

        return assistant;
    }

    /// <summary>
    /// Parses an OpenAPI specification and creates AssistantOpenApiOperation entities.
    /// Each operation (path+method) becomes a separate entity with its tool definition and schema fragment.
    /// </summary>
    private static List<AssistantOpenApiOperation> ParseOpenApiOperations(Guid schemaId, string openApiSpecJson, ILogger logger)
    {
        var operations = new List<AssistantOpenApiOperation>();

        try
        {
            // Validate and parse the OpenAPI spec
            var validationResult = OpenApiHelper.ValidateAndParseOpenApiSpec(openApiSpecJson);
            if (!validationResult.Status || validationResult.Spec == null)
            {
                throw new InvalidOperationException($"Invalid OpenAPI spec: {validationResult.Message}");
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

                    // Extract operation ID
                    string operationId;
                    if (operationObj.TryGetProperty("operationId", out var opId))
                    {
                        if (opId.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidOperationException(
                                $"OpenAPI operationId for {method} {path} must be a string, but was {opId.ValueKind}.");
                        }
                        operationId = opId.GetString() ?? string.Empty;
                    }
                    else
                    {
                        operationId = $"{method}_{path}";
                    }

                    if (string.IsNullOrEmpty(operationId))
                    {
                        continue;
                    }

                    // Extract summary
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
                    var toolDefJson = JsonSerializer.Serialize(toolDef);

                    // Create schema fragment containing this specific operation
                    var schemaFragment = new
                    {
                        path,
                        method = method.ToLower(),
                        operation = JsonDocument.Parse(operationObj.GetRawText()).RootElement
                    };

                    var schemaFragmentJson = JsonSerializer.Serialize(schemaFragment, JsonIndentedOptions);

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
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to parse OpenAPI operations for schema {SchemaId}. OpenApiSpec length {OpenApiSpecLength}",
                schemaId,
                openApiSpecJson?.Length ?? 0);
            throw new InvalidOperationException($"Failed to parse OpenAPI operations: {ex.Message}", ex);
        }

        return operations;
    }

    private static List<FileUploadDto>? MergeCreateFiles(
        List<FileUploadDto>? files,
        List<AssistantSkillSaveDto>? skills)
    {
        SkillDtoBuilder.ValidateSkillSavePayload(skills);
        var merged = new List<FileUploadDto>();
        if (files is { Count: > 0 })
        {
            merged.AddRange(files);
        }

        merged.AddRange(SkillDtoBuilder.FlattenSkillUploads(skills));
        return merged.Count > 0 ? merged : null;
    }

    private static List<FileUploadDto>? MergeUpdateFiles(
        List<FileUploadDto>? filesToAdd,
        List<AssistantSkillSaveDto>? skills)
    {
        SkillDtoBuilder.ValidateSkillSavePayload(skills);
        var merged = new List<FileUploadDto>();
        if (filesToAdd is { Count: > 0 })
        {
            merged.AddRange(filesToAdd);
        }

        merged.AddRange(SkillDtoBuilder.FlattenSkillUploads(skills));
        return merged.Count > 0 ? merged : null;
    }

    #endregion
}

