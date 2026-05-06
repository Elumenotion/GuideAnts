using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Services.Guides;

public class CatalogService : ICatalogService
{
    private readonly ApplicationDbContext _context;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;

    private static readonly JsonSerializerOptions JsonCaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CatalogService(ApplicationDbContext context, IRuntimeProfileResolver runtimeProfileResolver)
    {
        _context = context;
        _runtimeProfileResolver = runtimeProfileResolver;
    }

    public async Task<IEnumerable<ModelDto>> GetModelsAsync()
    {
        var models = await _context.Models
            .Where(m => m.IsActive)
            .OrderBy(m => m.DisplayOrder)
            .Select(m => new
            {
                m.ModelId,
                m.DisplayName,
                m.Description,
                m.ReasoningChoicesJson,
                m.IsActive,
                m.DisplayOrder,
                m.RuntimeConfigJson
            })
            .ToListAsync();

        var results = new List<ModelDto>();
        foreach (var m in models)
        {
            ModelRuntimeConfigDto? runtimeConfig = null;
            IReadOnlyList<SamplingParameterPolicyDto>? samplingPolicy = null;
            IReadOnlyList<string>? reasoningChoices = null;
            string? defaultReasoningChoice = null;

            if (!string.IsNullOrEmpty(m.RuntimeConfigJson))
            {
                runtimeConfig = JsonSerializer.Deserialize<ModelRuntimeConfigDto>(m.RuntimeConfigJson, JsonCaseInsensitive);

                if (runtimeConfig != null && !string.IsNullOrWhiteSpace(runtimeConfig.RuntimeProfileId))
                {
                    try
                    {
                        var profile = await _runtimeProfileResolver.ResolveAsync(runtimeConfig.RuntimeProfileId);

                        samplingPolicy = profile.SamplingParameters
                            .Values
                            .Where(sp => sp.ExposedInGuideBuilder)
                            .OrderBy(sp => sp.DisplayOrder)
                            .Select(sp => new SamplingParameterPolicyDto(
                                sp.Key, sp.Label, sp.Description,
                                sp.Min, sp.Max, sp.Step, sp.Default, sp.DisplayOrder))
                            .ToList();

                        var profileChoices = profile.ThinkingControl.ChoiceActions?.Keys.ToList() ?? [];
                        var modelChoices = ParseReasoningChoices(m.ReasoningChoicesJson);
                        reasoningChoices = modelChoices.Count > 0
                            ? profileChoices.Where(c => modelChoices.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList()
                            : profileChoices;
                        defaultReasoningChoice = profile.ThinkingControl.DefaultChoice;
                    }
                    catch
                    {
                        // Profile not found; leave policy/choices null
                    }
                }
            }

            results.Add(new ModelDto(
                m.ModelId, m.DisplayName, m.Description, m.ReasoningChoicesJson,
                m.IsActive, m.DisplayOrder, runtimeConfig,
                samplingPolicy, reasoningChoices, defaultReasoningChoice));
        }

        return results;
    }

    private static List<string> ParseReasoningChoices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonCaseInsensitive) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<IEnumerable<ToolDto>> GetToolsAsync()
    {
        return await _context.Tools
            .Where(t => t.IsActive)
            .OrderBy(t => t.Category)
            .ThenBy(t => t.DisplayOrder)
            .Select(t => new ToolDto(
                t.Id,
                t.ToolType,
                t.DisplayName,
                t.Description,
                t.Category,
                t.IsActive,
                t.DisplayOrder
            ))
            .ToListAsync();
    }

    public async Task<IEnumerable<GlobalAssistantDto>> GetGlobalAssistantsAsync()
    {
        return await _context.Assistants
            .Where(a => a.IsGlobal)
            .Where(ga => true)
            .OrderBy(ga => ga.DisplayOrder)
            .Select(ga => new GlobalAssistantDto(
                ga.Id,
                ga.Name,
                ga.Description ?? string.Empty,
                ga.Instructions,
                ga.ModelId,
                ga.AvatarImageBytes != null ? $"/api/catalogs/global-assistants/{ga.Id}/avatar" : null, // AvatarUrl
                true,
                ga.DisplayOrder,
                ga.Tools.Select(t => t.ToolId).ToList()
            ))
            .ToListAsync();
    }

    public async Task<GlobalAssistantDetailsDto?> GetGlobalAssistantAsync(Guid id)
    {
        var assistant = await _context.Assistants
            .Where(a => a.IsGlobal)
            .Include(ga => ga.Tools).ThenInclude(gat => gat.Tool)
            .Where(ga => ga.Id == id && true)
            .FirstOrDefaultAsync();

        if (assistant == null)
            return null;

        var assistantDto = new GlobalAssistantDto(
            assistant.Id,
            assistant.Name,
            assistant.Description ?? string.Empty,
            assistant.Instructions,
            assistant.ModelId,
            assistant.AvatarImageBytes != null ? $"/api/catalogs/global-assistants/{assistant.Id}/avatar" : null,
            assistant.IsActive,
            assistant.DisplayOrder,
            assistant.Tools.Select(t => t.ToolId).ToList()
        );

        var tools = assistant.Tools.Select(t => new ToolDto(
            t.Tool.Id,
            t.Tool.ToolType,
            t.Tool.DisplayName,
            t.Tool.Description,
            t.Tool.Category,
            t.Tool.IsActive,
            t.Tool.DisplayOrder
        )).ToList();

        return new GlobalAssistantDetailsDto(assistantDto, tools);
    }

    public async Task<byte[]?> GetGlobalAssistantAvatarBytesAsync(Guid id)
    {
        var assistant = await _context.Assistants
            .Where(a => a.IsGlobal && a.Id == id)
            .FirstOrDefaultAsync();

        return assistant?.AvatarImageBytes;
    }
}
