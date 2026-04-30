using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Settings;

public interface IApplicationSettingsService
{
    Task BootstrapAsync(IConfiguration bootstrapConfiguration, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SettingsSectionSummaryDto>> GetSectionSummariesAsync(CancellationToken cancellationToken = default);
    Task<SettingsSectionDto?> GetSectionAsync(string sectionName, CancellationToken cancellationToken = default);
    Task<(SettingsSectionDto? Section, IReadOnlyList<string> ValidationErrors, bool ConcurrencyConflict)> UpdateSectionAsync(
        string sectionName,
        UpdateSettingsSectionRequest request,
        CancellationToken cancellationToken = default);
    Task<SettingsSchemaDto> GetSchemaAsync(CancellationToken cancellationToken = default);
    Task<SettingsReadinessDto> GetReadinessAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SettingsModelDto>> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<SettingsModelDto> CreateModelAsync(CreateSettingsModelRequest request, CancellationToken cancellationToken = default);
    Task<SettingsModelDto?> UpdateModelAsync(string modelId, UpdateSettingsModelRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteModelAsync(string modelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SettingsRuntimeProfileDto>> GetRuntimeProfilesAsync(CancellationToken cancellationToken = default);
    Task<SettingsRuntimeProfileDto?> GetRuntimeProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task<SettingsRuntimeProfileDto> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken = default);
    Task<SettingsRuntimeProfileDto?> UpdateRuntimeProfileAsync(string profileId, UpdateRuntimeProfileRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteRuntimeProfileAsync(string profileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceModeDto>> GetServiceModesAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<ServiceEditorStateDto> GetServiceEditorStateAsync(string serviceId, CancellationToken cancellationToken = default);
    Task<ServiceEditorStateDto> SetServiceActiveProviderAsync(string serviceId, string providerId, CancellationToken cancellationToken = default);
    Task<ServiceEditorStateDto> UpdateServiceProviderFieldsAsync(
        string serviceId,
        string providerId,
        ProviderFieldsUpdateRequest request,
        CancellationToken cancellationToken = default);
    Task<ServiceEditorReadinessDto> GetServiceEditorReadinessAsync(string serviceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatTargetDto>> GetChatTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase D helper (R-5.5 / R-8.1 consumer visibility). Returns the modes and
    /// chat targets that currently reference the given provider section so the
    /// Connections tab can render "Used by services" chips driven by a single
    /// authoritative source rather than the client guessing from other endpoints.
    /// Empty lists are returned when nothing references the section.
    /// </summary>
    Task<ConnectionUsageDto> GetConnectionUsageAsync(string sectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase B helper (R-5.2 overview / R-7.1 preflight). Given a provider section
    /// name (e.g. <c>AzureOpenAiEmbedding</c>, <c>LlamaCpp</c>, or a runtime-owned
    /// <c>LocalServiceHosts:EmbeddingsBaseUrl</c> key), evaluates whether the
    /// minimum required fields are populated in the current configuration and
    /// returns a flat list of missing field names. The readiness composition in
    /// <c>IRoutingReadinessService</c> consumes this to build per-mode blockers.
    /// </summary>
    Task<ProviderSectionReadinessDto> GetProviderSectionReadinessAsync(string sectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase E (R-5.7): returns the Infrastructure tab dependency list with
    /// source, value, hasValue, isSecret, and kind already resolved. This is
    /// the single authoritative catalog consumed by the Infrastructure UI; no
    /// caller should re-enumerate <c>IConfiguration</c> itself.
    /// </summary>
    Task<IReadOnlyList<SettingsRuntimeDependencyDto>> GetRuntimeDependenciesAsync(CancellationToken cancellationToken = default);

    void ReloadConfiguration();
}

public sealed partial class ApplicationSettingsService(
    ApplicationDbContext db,
    ISettingsSectionRegistry registry,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    IOptionsMonitor<SettingsSecretsOptions> settingsSecretsOptionsMonitor,
    IRuntimeProfileResolver runtimeProfileResolver,
    IServiceEditorMetadataProvider? metadataProvider = null,
    ILlamaRouterIniSyncService? llamaRouterIniSync = null) : IApplicationSettingsService
{
    private static readonly SemaphoreSlim BootstrapEnsureGate = new(1, 1);

    private readonly ApplicationDbContext _db = db;
    private readonly ISettingsSectionRegistry _registry = registry;
    private readonly IWebHostEnvironment _environment = environment;
    private readonly IConfiguration _configuration = configuration;
    private readonly IOptionsMonitor<SettingsSecretsOptions> _settingsSecretsOptionsMonitor = settingsSecretsOptionsMonitor;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver = runtimeProfileResolver;
    private readonly IServiceEditorMetadataProvider? _injectedMetadataProvider = metadataProvider;
    private readonly ILlamaRouterIniSyncService? _llamaRouterIniSync = llamaRouterIniSync;
    private IServiceEditorMetadataProvider? _metadataProvider;
    private ILegacyProtectorAccessor? _protectorAccessor;

    public async Task BootstrapAsync(IConfiguration bootstrapConfiguration, CancellationToken cancellationToken = default)
    {
        if (!await ApplicationSettingsTableExistsAsync(cancellationToken))
        {
            return;
        }

        var protector = GetProtector();
        var existing = await _db.ApplicationSettings
            .ToDictionaryAsync(x => x.SectionName, x => x, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var changed = false;
        if (existing.Remove("LlamaCpp", out var legacyLlamaCppRow))
        {
            _db.ApplicationSettings.Remove(legacyLlamaCppRow);
            changed = true;
        }

        foreach (var section in _registry.All)
        {
            var bootstrapPayload = section.BuildBootstrapPayload(bootstrapConfiguration);

            if (!existing.TryGetValue(section.SectionName, out var row))
            {
                var encryptedPayload = ApplicationSettingsJson.EncryptSecrets(
                    section,
                    bootstrapPayload,
                    _settingsSecretsOptionsMonitor.CurrentValue);
                _db.ApplicationSettings.Add(new ApplicationSetting
                {
                    SectionName = section.SectionName,
                    SchemaVersion = section.SchemaVersion,
                    JsonValue = ApplicationSettingsJson.Serialize(encryptedPayload),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                changed = true;
                continue;
            }

            var currentPayload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);
            var decryptedCurrent = ApplicationSettingsJson.DecryptSecrets(
                section,
                currentPayload,
                _settingsSecretsOptionsMonitor.CurrentValue,
                protector.Protect);
            var merged = ApplicationSettingsJson.MergeMissingProperties(section, decryptedCurrent, bootstrapPayload);

            var needsPayloadUpdate = !JsonObjectsEquivalent(decryptedCurrent, merged);
            var needsSchemaVersionUpdate = row.SchemaVersion != section.SchemaVersion;

            if (!needsPayloadUpdate && !needsSchemaVersionUpdate)
            {
                continue;
            }

            if (needsPayloadUpdate)
            {
                var encryptedPayload = ApplicationSettingsJson.EncryptSecrets(
                    section,
                    merged,
                    _settingsSecretsOptionsMonitor.CurrentValue);
                row.JsonValue = ApplicationSettingsJson.Serialize(encryptedPayload);
            }

            if (needsSchemaVersionUpdate)
            {
                row.SchemaVersion = section.SchemaVersion;
            }

            row.UpdatedUtc = DateTime.UtcNow;
            changed = true;
        }

        if (changed)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Normalize single-mode legacy rows into explicit local/cloud mode ids
        // with canonical provider-section values. This is idempotent and only
        // mutates narrow transitional shapes.
        await NormalizeLegacySingleModeRowsAsync(cancellationToken).ConfigureAwait(false);
    }

    public void ReloadConfiguration()
    {
        if (_configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
    }

    private async Task EnsureRowsExistFromCurrentConfigAsync(CancellationToken cancellationToken)
    {
        await BootstrapEnsureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BootstrapAsync(_configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another concurrent request (or instance) likely won the bootstrap write race.
            // Clear tracked state and run one more pass to verify rows now exist.
            _db.ChangeTracker.Clear();
            await BootstrapAsync(_configuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            BootstrapEnsureGate.Release();
        }
    }

    private async Task<bool> ApplicationSettingsTableExistsAsync(CancellationToken cancellationToken)
    {
        if (!_db.Database.IsSqlServer())
        {
            // Non-SQL Server providers are used in tests and don't support OBJECT_ID checks.
            return true;
        }

        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        try
        {
            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CASE WHEN OBJECT_ID('dbo.ApplicationSettings', 'U') IS NULL THEN 0 ELSE 1 END";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is int i && i == 1;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private ILegacyProtectorAccessor GetProtector()
    {
        _protectorAccessor ??= new ILegacyProtectorAccessor(ApplicationSettingsJson.CreateLegacyProtector(_environment.ContentRootPath));
        return _protectorAccessor;
    }

    private sealed class ILegacyProtectorAccessor(Microsoft.AspNetCore.DataProtection.IDataProtector protect)
    {
        public Microsoft.AspNetCore.DataProtection.IDataProtector Protect { get; } = protect;
    }
}
