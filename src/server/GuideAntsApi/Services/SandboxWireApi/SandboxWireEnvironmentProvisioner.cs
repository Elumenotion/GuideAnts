using System.Text.Json;
using AntRunner.ToolCalling;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.SandboxWireApi;

public sealed record SandboxWireProvisionRequest(
    Guid ExecutionId,
    Guid ProjectId,
    Guid NotebookId,
    Guid OwnerAssistantId,
    Guid? AttributionConversationId,
    TimeSpan Lifetime,
    Guid? OverrideTargetAssistantId = null,
    decimal? JobDailyLimitUsd = null,
    decimal? JobMonthlyLimitUsd = null,
    bool ForceEnabled = false);

public interface ISandboxWireEnvironmentProvisioner
{
    Task<IReadOnlyDictionary<string, string>?> BuildEnvironmentAsync(
        SandboxWireProvisionRequest request,
        CancellationToken ct = default);
}

public sealed class SandboxWireEnvironmentProvisioner : ISandboxWireEnvironmentProvisioner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] AllEndpoints =
    [
        "models",
        "chat.completions",
        "messages",
        "responses",
        "embeddings",
        "images.generations",
        "audio.transcriptions",
        "audio.speech",
    ];

    private readonly ApplicationDbContext _db;
    private readonly ISandboxWireJwtService _jwtService;
    private readonly ISandboxWireCycleDetector _cycleDetector;
    private readonly SandboxWireApiOptions _options;
    private readonly ILogger<SandboxWireEnvironmentProvisioner> _logger;

    public SandboxWireEnvironmentProvisioner(
        ApplicationDbContext db,
        ISandboxWireJwtService jwtService,
        ISandboxWireCycleDetector cycleDetector,
        IOptions<SandboxWireApiOptions> options,
        ILogger<SandboxWireEnvironmentProvisioner> logger)
    {
        _db = db;
        _jwtService = jwtService;
        _cycleDetector = cycleDetector;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string>?> BuildEnvironmentAsync(
        SandboxWireProvisionRequest request,
        CancellationToken ct = default)
    {
        var ownerGuide = await _db.Assistants
            .AsNoTracking()
            .Where(a => a.Id == request.OwnerAssistantId && a.Kind == AssistantKind.Guide)
            .Select(a => new { a.Id, a.SandboxWireApiConfigJson })
            .FirstOrDefaultAsync(ct);

        if (ownerGuide == null)
        {
            return null;
        }

        var config = DeserializeConfig(ownerGuide.SandboxWireApiConfigJson);
        var enabled = config.Enabled || request.ForceEnabled;
        var targetAssistantId = request.OverrideTargetAssistantId ?? config.TargetAssistantId;
        if (!enabled || !targetAssistantId.HasValue)
        {
            return null;
        }

        if (targetAssistantId.Value == request.OwnerAssistantId)
        {
            _logger.LogWarning(
                "Sandbox wire disabled: owner guide {OwnerGuideId} cannot target itself.",
                request.OwnerAssistantId);
            return null;
        }

        if (await _cycleDetector.WouldCreateCycleAsync(request.OwnerAssistantId, targetAssistantId.Value, ct))
        {
            _logger.LogWarning(
                "Sandbox wire disabled: cycle detected for owner guide {OwnerGuideId} -> target {TargetAssistantId}.",
                request.OwnerAssistantId,
                targetAssistantId.Value);
            return null;
        }

        var targetAssistant = await _db.Assistants
            .AsNoTracking()
            .Where(a => a.Id == targetAssistantId.Value && a.IsActive)
            .Select(a => new { a.Id, a.Name })
            .FirstOrDefaultAsync(ct);

        if (targetAssistant == null)
        {
            _logger.LogWarning(
                "Sandbox wire disabled: target assistant {TargetAssistantId} not found or inactive.",
                targetAssistantId.Value);
            return null;
        }

        var ancestors = await _cycleDetector.BuildAncestorChainAsync(request.OwnerAssistantId, ct);
        var allowedEndpoints = ResolveAllowedEndpoints(config);
        var lifetime = request.Lifetime > TimeSpan.Zero
            ? request.Lifetime
            : TimeSpan.FromMinutes(_options.DefaultLifetimeMinutes);
        var dailyLimitUsd = request.JobDailyLimitUsd ?? config.DailyLimitUsd;
        var monthlyLimitUsd = request.JobMonthlyLimitUsd ?? config.MonthlyLimitUsd;

        var grant = new SandboxWireExecutionGrant(
            ExecutionId: request.ExecutionId,
            ProjectId: request.ProjectId,
            NotebookId: request.NotebookId,
            OwnerAssistantId: request.OwnerAssistantId,
            TargetAssistantId: targetAssistant.Id,
            TargetAssistantName: targetAssistant.Name,
            AllowedEndpoints: allowedEndpoints,
            AttributionConversationId: request.AttributionConversationId,
            AncestorAssistantIds: ancestors,
            Lifetime: lifetime,
            DailyLimitUsd: dailyLimitUsd,
            MonthlyLimitUsd: monthlyLimitUsd);

        var issued = _jwtService.Mint(grant);
        _logger.LogDebug(
            "Minted sandbox wire JWT for execution {ExecutionId}, expires {ExpiresUtc}.",
            request.ExecutionId,
            issued.ExpiresAtUtc);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENAI_BASE_URL"] = _options.InternalBaseUrl.TrimEnd('/'),
            ["OPENAI_API_KEY"] = issued.Token,
        };
    }

    internal static IReadOnlyList<string> ResolveAllowedEndpoints(SandboxWireApiConfigDto config)
    {
        var flags = config.EndpointFlags;
        if (flags == null)
        {
            return AllEndpoints;
        }

        var enabled = new List<string>();
        if (flags.Models != false)
        {
            enabled.Add("models");
        }

        if (flags.ChatCompletions != false)
        {
            enabled.Add("chat.completions");
        }

        if (flags.Messages != false)
        {
            enabled.Add("messages");
        }

        if (flags.Responses != false)
        {
            enabled.Add("responses");
        }

        if (flags.Embeddings != false)
        {
            enabled.Add("embeddings");
        }

        if (flags.ImageGenerations != false)
        {
            enabled.Add("images.generations");
        }

        if (flags.AudioTranscriptions != false)
        {
            enabled.Add("audio.transcriptions");
        }

        if (flags.AudioSpeech != false)
        {
            enabled.Add("audio.speech");
        }

        return enabled;
    }

    private static SandboxWireApiConfigDto DeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SandboxWireApiConfigDto();
        }

        return JsonSerializer.Deserialize<SandboxWireApiConfigDto>(json, JsonOptions)
            ?? new SandboxWireApiConfigDto();
    }
}

public static class SandboxWireEnvironmentMergeExtensions
{
    public static IReadOnlyDictionary<string, string>? MergeSandboxWireEnvironment(
        IReadOnlyDictionary<string, string>? baseEnvironment,
        IReadOnlyDictionary<string, string>? sandboxWireEnvironment)
    {
        if (sandboxWireEnvironment == null || sandboxWireEnvironment.Count == 0)
        {
            return baseEnvironment;
        }

        if (baseEnvironment == null || baseEnvironment.Count == 0)
        {
            return new Dictionary<string, string>(sandboxWireEnvironment, StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, string>(baseEnvironment, StringComparer.Ordinal);
        foreach (var (key, value) in sandboxWireEnvironment)
        {
            merged[key] = value;
        }

        return merged;
    }
}
