using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Services.Guides;

public class CatalogService : ICatalogService
{
    private readonly ApplicationDbContext _context;

    private static readonly JsonSerializerOptions JsonCaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CatalogService(ApplicationDbContext context)
    {
        _context = context;
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
                m.Provider,
                m.ReasoningChoicesJson,
                m.IsActive,
                m.DisplayOrder,
                m.RuntimeConfigJson,
                m.CombineSystemAndDeveloperMessages,
                m.ThoughtBlockPattern,
                m.SamplingParametersJson,
                m.ThinkingControlJson,
                m.RequestFieldsWhenToolsPresentJson
            })
            .ToListAsync();

        var results = new List<ModelDto>();
        foreach (var m in models)
        {
            ModelRuntimeConfigDto? runtimeConfig = null;
            IReadOnlyList<SamplingParameterPolicyDto>? samplingPolicy = null;
            IReadOnlyList<string>? reasoningChoices = null;
            string? defaultReasoningChoice = null;

            if (!string.IsNullOrEmpty(m.RuntimeConfigJson)
                && string.Equals(m.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
            {
                runtimeConfig = JsonSerializer.Deserialize<ModelRuntimeConfigDto>(m.RuntimeConfigJson, JsonCaseInsensitive);
            }

            if (string.Equals(m.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(m.ThinkingControlJson)
                && !string.Equals(m.ThinkingControlJson.Trim(), "{}", StringComparison.Ordinal))
            {
                var profile = RuntimeProfileDataJson.FromJsonStrings(
                    m.ModelId,
                    m.CombineSystemAndDeveloperMessages,
                    m.ThoughtBlockPattern,
                    m.SamplingParametersJson,
                    m.ThinkingControlJson,
                    m.RequestFieldsWhenToolsPresentJson,
                    m.DisplayName,
                    m.Description);

                if (runtimeConfig is null && !string.IsNullOrWhiteSpace(m.RuntimeConfigJson))
                {
                    try
                    {
                        var localRuntime = LocalRuntimeConfigurationParser.ParseRequired(m.ModelId, m.RuntimeConfigJson);
                        runtimeConfig = new ModelRuntimeConfigDto(localRuntime.RouterModelId);
                    }
                    catch
                    {
                        // Leave runtime config null when invalid.
                    }
                }

                samplingPolicy = BuildSamplingPolicy(profile);
                var profileChoices = profile.ThinkingControl.ChoiceActions?.Keys.ToList() ?? [];
                var modelChoices = ParseReasoningChoices(m.ReasoningChoicesJson);
                reasoningChoices = modelChoices.Count > 0
                    ? profileChoices.Where(c => modelChoices.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList()
                    : profileChoices;
                defaultReasoningChoice = profile.ThinkingControl.DefaultChoice;
            }
            else if (!string.Equals(m.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(m.SamplingParametersJson)
                    && !string.Equals(m.SamplingParametersJson.Trim(), "{}", StringComparison.Ordinal))
                {
                    try
                    {
                        var profile = RuntimeProfileDataJson.FromJsonStrings(
                            m.ModelId,
                            m.CombineSystemAndDeveloperMessages,
                            m.ThoughtBlockPattern,
                            m.SamplingParametersJson,
                            "{}",
                            "{}",
                            m.DisplayName,
                            m.Description);
                        samplingPolicy = BuildSamplingPolicy(profile);
                    }
                    catch
                    {
                        // Leave policy null when invalid.
                    }
                }

                reasoningChoices = ParseReasoningChoices(m.ReasoningChoicesJson);
                defaultReasoningChoice = reasoningChoices.Count > 0 ? reasoningChoices[0] : null;
            }

            results.Add(new ModelDto(
                m.ModelId, m.DisplayName, m.Description, m.ReasoningChoicesJson,
                m.IsActive, m.DisplayOrder, runtimeConfig,
                samplingPolicy, reasoningChoices, defaultReasoningChoice));
        }

        return results;
    }

    private static IReadOnlyList<SamplingParameterPolicyDto>? BuildSamplingPolicy(RuntimeProfileData profile)
    {
        var policy = profile.SamplingParameters
            .Values
            .Where(sp => sp.ExposedInGuideBuilder)
            .OrderBy(sp => sp.DisplayOrder)
            .Select(sp => new SamplingParameterPolicyDto(
                sp.Key, sp.Label, sp.Description,
                sp.Min, sp.Max, sp.Step, sp.Default, sp.DisplayOrder))
            .ToList();

        return policy.Count == 0 ? null : policy;
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
                ga.AvatarImageBytes != null ? $"/api/catalogs/global-assistants/{ga.Id}/avatar" : null,
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
