using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Services;

public sealed class NotebookTemplateService(
    IServiceScopeFactory scopeFactory,
    ISystemGuideCatalogFilter systemGuideCatalogFilter,
    ILogger<NotebookTemplateService> logger) : INotebookTemplateService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ISystemGuideCatalogFilter _systemGuideCatalogFilter = systemGuideCatalogFilter;
    private readonly ILogger<NotebookTemplateService> _logger = logger;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Checks if the auth config JSON contains any providers with optional or required user configuration.
    /// </summary>
    private bool HasConfigurableAuthProviders(string? authConfigJson)
    {
        if (string.IsNullOrWhiteSpace(authConfigJson)) return false;
        
        try
        {
            var authConfig = JsonSerializer.Deserialize<TemplateAuthManifestInternal>(authConfigJson, _jsonOptions);
            if (authConfig?.Providers is not { Count: > 0 }) return false;
            
            return authConfig.Providers.Any(p => 
                p.UserConfigPolicy == NotebookUserConfigPolicy.Optional || 
                p.UserConfigPolicy == NotebookUserConfigPolicy.Required);
        }
        catch
        {
            return false;
        }
    }


    public async Task<IEnumerable<NotebookTemplateSummaryDto>> GetTemplateSummariesAsync(
        Guid? projectId = null,
        Guid? userId = null)
    {
        // Load lightweight template summaries for selection dialogs
        // Does NOT load: conversationStarters, defaultAssistant, homeContent, full authProviders
        var results = new List<NotebookTemplateSummaryDto>();

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _logger.LogInformation("Loading template summaries for userId: {UserId}", userId);

        var hiddenGuideIds = await _systemGuideCatalogFilter.GetHiddenGuideIdsForCatalogAsync(projectId);
        
        // Load only the fields we need from database guides
        // Include AuthConfigJson to compute HasConfigurableAuth flag
        var dbGuides = await context.Assistants
            .AsNoTracking()
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive && !hiddenGuideIds.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Description,
                HasAvatar = a.AvatarImageBytes != null,
                a.IsGlobal,
                a.AuthConfigJson
            })
            .ToListAsync();
        
        _logger.LogInformation("Found {Count} guides from database for userId {UserId}", dbGuides.Count, userId);

        // Group by name to handle duplicates (user-specific overrides global)
        var guidesByName = new Dictionary<string, (Guid Id, string Name, string? Description, bool HasAvatar, string? AuthConfigJson, bool IsGlobal)>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var guide in dbGuides)
        {
            var templateName = guide.Name;
            
            if (guidesByName.TryGetValue(templateName, out var existing))
            {
                if (IsHigherPriorityGuide(
                    guide.IsGlobal,
                    existing.IsGlobal))
                {
                    guidesByName[templateName] = (guide.Id, guide.Name, guide.Description, guide.HasAvatar, guide.AuthConfigJson, guide.IsGlobal);
                }
            }
            else
            {
                guidesByName[templateName] = (guide.Id, guide.Name, guide.Description, guide.HasAvatar, guide.AuthConfigJson, guide.IsGlobal);
            }
        }

        foreach (var (templateName, guide) in guidesByName)
        {
            var avatarUrl = guide.HasAvatar
                ? $"/api/notebook-templates/avatar/{Uri.EscapeDataString(templateName)}"
                : string.Empty;

            // Check if any auth providers require/allow user configuration
            var hasConfigurableAuth = HasConfigurableAuthProviders(guide.AuthConfigJson);

            results.Add(new NotebookTemplateSummaryDto(
                guide.Id,
                templateName,
                guide.Description ?? string.Empty,
                avatarUrl,
                hasConfigurableAuth
            ));
        }

        return results;
    }

    public async Task<IEnumerable<NotebookTemplateDto>> GetTemplatesAsync(
        Guid? projectId = null,
        Guid? userId = null)
    {
        // Load templates from database (user-specific + global guides).
        var results = new List<NotebookTemplateDto>();

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // RULE: Internally, we use string names for resolution always
        // PRIORITY: 1) User-specific guides, 2) Global guides
        _logger.LogInformation("Loading templates for userId: {UserId}", userId);

        var hiddenGuideIds = await _systemGuideCatalogFilter.GetHiddenGuideIdsForCatalogAsync(projectId);
        var dbGuides = await context.Assistants
            .AsNoTracking()
            .Include(a => a.Model)
            .Include(a => a.ConversationStarters)
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive && !hiddenGuideIds.Contains(a.Id))
            .ToListAsync();
        _logger.LogInformation("Found {Count} guides from database for userId {UserId}", dbGuides.Count, userId);

        // Guides are the source of truth - no registry needed

        // Group guides by template name to handle duplicates (owner-specific overrides global)
        var guidesByTemplateName = new Dictionary<string, Assistant>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var guide in dbGuides)
        {
            // Guide name IS the template name
            var templateName = guide.Name;

            // If duplicate, prefer owner-specific over global
            if (guidesByTemplateName.TryGetValue(templateName, out var existing))
            {
                if (IsHigherPriorityGuide(
                    guide.IsGlobal,
                    existing.IsGlobal))
                {
                    guidesByTemplateName[templateName] = guide;
                }
            }
            else
            {
                guidesByTemplateName[templateName] = guide;
            }
        }

        foreach (var (templateName, guide) in guidesByTemplateName)
        {
            // Use guide ID directly - Assistants table is the source of truth
            var templateId = guide.Id;

            var avatarUrl = guide.AvatarImageBytes != null
                ? $"/api/notebook-templates/avatar/{Uri.EscapeDataString(templateName)}"
                : string.Empty;

            var starters = guide.ConversationStarters
                .OrderBy(s => s.OrderIndex)
                .Select(s => s.Prompt)
                .ToList();

            // Use guide name as default assistant
            var computedDefaultAssistant = guide.Name;

            // Parse auth providers from AuthConfigJson
            List<NotebookAuthProviderDto>? authProviders = null;
            if (!string.IsNullOrWhiteSpace(guide.AuthConfigJson))
            {
                try
                {
                    var authConfig = JsonSerializer.Deserialize<TemplateAuthManifestInternal>(guide.AuthConfigJson, _jsonOptions);
                    if (authConfig?.Providers is { Count: > 0 })
                    {
                        authProviders = authConfig.Providers
                            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && p.AuthType.HasValue)
                            .Select(p => new NotebookAuthProviderDto(
                                p.Id,
                                p.AuthType!.Value,
                                p.AuthType == NotebookAuthType.OAuth ? p.ClientId : null,
                                p.AuthType == NotebookAuthType.OAuth ? (p.Scopes ?? new List<string>()) : null,
                                p.AuthType == NotebookAuthType.OAuth ? p.Tenant : null,
                                p.AuthType == NotebookAuthType.OAuth ? (string.IsNullOrWhiteSpace(p.ValueTemplate) ? "Bearer {access_token}" : p.ValueTemplate) : p.ValueTemplate,
                                p.AuthType == NotebookAuthType.ServiceHttp ? (p.HeaderName ?? "Authorization") : null,
                                p.UserConfigPolicy ?? NotebookUserConfigPolicy.None
                            ))
                            .ToList();
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse AuthConfigJson for guide {GuideName}", guide.Name);
                }
            }

            results.Add(new NotebookTemplateDto(
                templateId,
                templateName,
                guide.Description ?? string.Empty,
                avatarUrl,
                starters,
                computedDefaultAssistant,
                guide.HomePageMarkdown,
                authProviders
            ));
        }

        return results;
    }

    public async Task<NotebookTemplateDto?> GetTemplateByIdAsync(
        Guid templateId,
        Guid? projectId = null,
        Guid? userId = null)
    {
        List<NotebookAuthProviderDto>? authProviders = null;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var hiddenGuideIds = await _systemGuideCatalogFilter.GetHiddenGuideIdsForCatalogAsync(projectId);
        if (hiddenGuideIds.Contains(templateId))
        {
            return null;
        }

        // STEP 1: Try to look up guide by ID directly (new path)
        var dbGuides = await context.Assistants
            .AsNoTracking()
            .Include(a => a.Model)
            .Include(a => a.ConversationStarters)
            .Where(a => a.Id == templateId && a.Kind == AssistantKind.Guide && a.IsActive)
            .ToListAsync();

        var dbGuide = SelectPreferredGuide(dbGuides, userId);

        if (dbGuide != null)
        {
            // Found guide directly by ID
            var templateName = dbGuide.Name;
            var guideAvatarUrl = dbGuide.AvatarImageBytes != null
                ? $"/api/notebook-templates/avatar/{Uri.EscapeDataString(templateName)}"
                : string.Empty;

            var guideStarters = dbGuide.ConversationStarters
                .OrderBy(s => s.OrderIndex)
                .Select(s => s.Prompt)
                .ToList();

            if (!string.IsNullOrWhiteSpace(dbGuide.AuthConfigJson))
            {
                try
                {
                    var authConfig = JsonSerializer.Deserialize<TemplateAuthManifestInternal>(dbGuide.AuthConfigJson, _jsonOptions);
                    if (authConfig?.Providers is { Count: > 0 })
                    {
                        authProviders = authConfig.Providers
                            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && p.AuthType.HasValue)
                            .Select(p => new NotebookAuthProviderDto(
                                p.Id,
                                p.AuthType!.Value,
                                p.AuthType == NotebookAuthType.OAuth ? p.ClientId : null,
                                p.AuthType == NotebookAuthType.OAuth ? (p.Scopes ?? new List<string>()) : null,
                                p.AuthType == NotebookAuthType.OAuth ? p.Tenant : null,
                                p.AuthType == NotebookAuthType.OAuth ? (string.IsNullOrWhiteSpace(p.ValueTemplate) ? "Bearer {access_token}" : p.ValueTemplate) : p.ValueTemplate,
                                p.AuthType == NotebookAuthType.ServiceHttp ? (p.HeaderName ?? "Authorization") : null,
                                p.UserConfigPolicy ?? NotebookUserConfigPolicy.None
                            ))
                            .ToList();
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse AuthConfigJson for guide {GuideName}", dbGuide.Name);
                }
            }

            return new NotebookTemplateDto(
                dbGuide.Id,
                templateName,
                dbGuide.Description ?? string.Empty,
                guideAvatarUrl,
                guideStarters,
                dbGuide.Name,
                dbGuide.HomePageMarkdown,
                authProviders
            );
        }

        // STEP 2: Fall back to NotebookTemplates table (legacy path)
        // Look up template name, then find guide by name
        var dbTemplate = await context.NotebookTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId);
        
        if (dbTemplate == null)
        {
            return null;
        }

        string guideName = dbTemplate.TemplateName;

        // Find guide by name with user override
        var guidesByName = await context.Assistants
            .AsNoTracking()
            .Include(a => a.Model)
            .Include(a => a.ConversationStarters)
            .Where(a => a.Name == guideName && a.Kind == AssistantKind.Guide && a.IsActive && !hiddenGuideIds.Contains(a.Id))
            .ToListAsync();

        dbGuide = SelectPreferredGuide(guidesByName, userId);

        if (dbGuide == null)
        {
            return null;
        }

        var avatarUrl = dbGuide.AvatarImageBytes != null
            ? $"/api/notebook-templates/avatar/{Uri.EscapeDataString(guideName)}"
            : string.Empty;

        var starters = dbGuide.ConversationStarters
            .OrderBy(s => s.OrderIndex)
            .Select(s => s.Prompt)
            .ToList();

        if (!string.IsNullOrWhiteSpace(dbGuide.AuthConfigJson))
        {
            try
            {
                var authConfig = JsonSerializer.Deserialize<TemplateAuthManifestInternal>(dbGuide.AuthConfigJson, _jsonOptions);
                if (authConfig?.Providers is { Count: > 0 })
                {
                    authProviders = authConfig.Providers
                        .Where(p => !string.IsNullOrWhiteSpace(p.Id) && p.AuthType.HasValue)
                        .Select(p => new NotebookAuthProviderDto(
                            p.Id,
                            p.AuthType!.Value,
                            p.AuthType == NotebookAuthType.OAuth ? p.ClientId : null,
                            p.AuthType == NotebookAuthType.OAuth ? (p.Scopes ?? new List<string>()) : null,
                            p.AuthType == NotebookAuthType.OAuth ? p.Tenant : null,
                            p.AuthType == NotebookAuthType.OAuth ? (string.IsNullOrWhiteSpace(p.ValueTemplate) ? "Bearer {access_token}" : p.ValueTemplate) : p.ValueTemplate,
                            p.AuthType == NotebookAuthType.ServiceHttp ? (p.HeaderName ?? "Authorization") : null,
                            p.UserConfigPolicy ?? NotebookUserConfigPolicy.None
                        ))
                        .ToList();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse AuthConfigJson for guide {GuideName}", dbGuide.Name);
            }
        }

        return new NotebookTemplateDto(
            dbGuide.Id, // Return guide ID, not template ID
            guideName,
            dbGuide.Description ?? string.Empty,
            avatarUrl,
            starters,
            dbGuide.Name,
            dbGuide.HomePageMarkdown,
            authProviders
        );
    }

    public async Task<IReadOnlyList<AssistantDefinitionDto>> GetAssistantsAsync(
        Guid templateId,
        Guid? projectId = null,
        Guid? userId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var hiddenGuideIds = await _systemGuideCatalogFilter.GetHiddenGuideIdsForCatalogAsync(projectId);
        if (hiddenGuideIds.Contains(templateId))
        {
            return [];
        }

        // STEP 1: Try to find guide by ID directly (new path - GuideId)
        var guidesById = await context.Assistants
            .AsNoTracking()
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant)
            .Where(a => a.Id == templateId && a.Kind == AssistantKind.Guide && a.IsActive)
            .ToListAsync();

        var selectedGuide = SelectPreferredGuide(guidesById, userId);
        if (selectedGuide != null)
        {
            return BuildAssistantListFromGuide(selectedGuide);
        }

        // STEP 2: Fall back to NotebookTemplates table (legacy path - NotebookTemplateId)
        // Look up template name, then find guide by name
        var dbTemplate = await context.NotebookTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId);
        
        if (dbTemplate == null)
        {
            return [];
        }

        string guideName = dbTemplate.TemplateName;

        // Find guide by name with user override
        var guidesByName = await context.Assistants
            .AsNoTracking()
            .Include(a => a.CrewMembers).ThenInclude(cm => cm.Assistant)
            .Where(a => a.Name == guideName && a.Kind == AssistantKind.Guide && a.IsActive && !hiddenGuideIds.Contains(a.Id))
            .ToListAsync();

        var guide = SelectPreferredGuide(guidesByName, userId);

        if (guide == null)
        {
            return [];
        }

        return BuildAssistantListFromGuide(guide);
    }

    private static Assistant? SelectPreferredGuide(IEnumerable<Assistant> guides, Guid? userId)
    {
        Assistant? selected = null;
        var selectedRank = int.MinValue;
        foreach (var guide in guides)
        {
            var rank = GetGuidePreferenceRank(guide.IsGlobal);
            if (selected == null || rank > selectedRank)
            {
                selected = guide;
                selectedRank = rank;
            }
        }

        return selected;
    }

    private static bool IsHigherPriorityGuide(
        bool candidateIsGlobal,
        bool currentIsGlobal)
    {
        return GetGuidePreferenceRank(candidateIsGlobal)
            > GetGuidePreferenceRank(currentIsGlobal);
    }

    private static int GetGuidePreferenceRank(bool isGlobal)
    {
        return isGlobal ? 1 : 0;
    }

    private List<AssistantDefinitionDto> BuildAssistantListFromGuide(Assistant guide)
    {
        var list = new List<AssistantDefinitionDto>();

        // Add the guide itself as the first assistant
        // Use template name URL which handles owner overrides
        var guideAvatarUrl = guide.AvatarImageBytes != null
            ? $"/api/notebook-templates/avatar/{Uri.EscapeDataString(guide.Name)}"
            : string.Empty;
        
        list.Add(new AssistantDefinitionDto(
            guide.Id,
            guide.Name,
            guide.Description ?? string.Empty,
            guideAvatarUrl,
            guide.ModelId
        ));

        // Add crew members
        foreach (var crewMember in guide.CrewMembers.OrderBy(cm => cm.DisplayOrder))
        {
            var assistant = crewMember.Assistant;
            if (assistant == null) continue;

            // Use assistant name URL which handles owner overrides via projectId query param
            var avatarUrl = assistant.AvatarImageBytes != null
                ? $"/api/assistants/avatar/{Uri.EscapeDataString(assistant.Name)}"
                : string.Empty;

            list.Add(new AssistantDefinitionDto(
                assistant.Id,
                assistant.Name,
                assistant.Description ?? string.Empty,
                avatarUrl,
                assistant.ModelId
            ));
        }

        return list;
    }

    private sealed class TemplateAuthManifestInternal
    {
        public List<TemplateAuthProviderInternal> Providers { get; set; } = [];
    }

    private sealed class TemplateAuthProviderInternal
    {
        // Provider identifier, typically the host (e.g., graph.microsoft.com)
        public string Id { get; set; } = string.Empty;
        public NotebookAuthType? AuthType { get; set; }
        // OAuth fields (for AuthType == oauth)
        public string? ClientId { get; set; }
        public List<string>? Scopes { get; set; }
        public string? Tenant { get; set; }
        public string? ValueTemplate { get; set; }
        // Header-based service auth (for AuthType == service_http)
        public string? HeaderName { get; set; }
        // Per-provider policy indicating whether users can/should provide overrides
        public NotebookUserConfigPolicy? UserConfigPolicy { get; set; }
    }

}
