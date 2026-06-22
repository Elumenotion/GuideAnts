using System.Text.Json.Nodes;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Settings;

public sealed class GuideAntsSystemSettingsStore : IGuideAntsSystemSettingsStore
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsSectionRegistry _registry;
    private readonly IApplicationSettingsService _applicationSettingsService;

    public GuideAntsSystemSettingsStore(
        ApplicationDbContext db,
        ISettingsSectionRegistry registry,
        IApplicationSettingsService applicationSettingsService)
    {
        _db = db;
        _registry = registry;
        _applicationSettingsService = applicationSettingsService;
    }

    public async Task<GuideAntsSystemSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(GuideAntsSystemSettings.SectionName, out var definition))
        {
            return null;
        }

        var row = await _db.ApplicationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.SectionName == definition.SectionName,
                cancellationToken);

        if (row == null)
        {
            return null;
        }

        var payload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);
        return FromPayload(payload);
    }

    public async Task SaveAsync(GuideAntsSystemSettings settings, CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(GuideAntsSystemSettings.SectionName, out var definition))
        {
            throw new InvalidOperationException(
                $"Settings section '{GuideAntsSystemSettings.SectionName}' is not registered.");
        }

        var payload = ToPayload(settings);
        var validationErrors = definition.Validate(payload);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid {GuideAntsSystemSettings.SectionName} settings: {string.Join("; ", validationErrors)}");
        }

        var row = await _db.ApplicationSettings
            .SingleOrDefaultAsync(
                x => x.SectionName == definition.SectionName,
                cancellationToken);

        var serialized = ApplicationSettingsJson.Serialize(payload);

        if (row == null)
        {
            _db.ApplicationSettings.Add(new ApplicationSetting
            {
                SectionName = definition.SectionName,
                SchemaVersion = definition.SchemaVersion,
                JsonValue = serialized,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            row.JsonValue = serialized;
            row.SchemaVersion = definition.SchemaVersion;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _applicationSettingsService.ReloadConfiguration();
    }

    internal static GuideAntsSystemSettings FromPayload(JsonObject payload)
    {
        return new GuideAntsSystemSettings
        {
            ProjectId = ParseGuid(payload, "projectId"),
            UserGuideId = ParseGuid(payload, "userGuideId"),
            AdminGuideId = ParseGuid(payload, "adminGuideId"),
            UserNotebookId = ParseGuid(payload, "userNotebookId"),
            AdminNotebookId = ParseGuid(payload, "adminNotebookId"),
            UserPublishedGuideId = ParseGuid(payload, "userPublishedGuideId"),
            AdminPublishedGuideId = ParseGuid(payload, "adminPublishedGuideId"),
            ClientBridgeId = ParseString(payload, "clientBridgeId") ?? GuideAntsSystemSettings.DefaultClientBridgeId
        };
    }

    internal static JsonObject ToPayload(GuideAntsSystemSettings settings)
    {
        var payload = new JsonObject
        {
            ["clientBridgeId"] = settings.ClientBridgeId
        };

        WriteGuid(payload, "projectId", settings.ProjectId);
        WriteGuid(payload, "userGuideId", settings.UserGuideId);
        WriteGuid(payload, "adminGuideId", settings.AdminGuideId);
        WriteGuid(payload, "userNotebookId", settings.UserNotebookId);
        WriteGuid(payload, "adminNotebookId", settings.AdminNotebookId);
        WriteGuid(payload, "userPublishedGuideId", settings.UserPublishedGuideId);
        WriteGuid(payload, "adminPublishedGuideId", settings.AdminPublishedGuideId);

        return payload;
    }

    private static Guid? ParseGuid(JsonObject payload, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        var text = ApplicationSettingsJson.NodeToString(node)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Guid.TryParse(text, out var parsed) ? parsed : null;
    }

    private static string? ParseString(JsonObject payload, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        var text = ApplicationSettingsJson.NodeToString(node)?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void WriteGuid(JsonObject payload, string propertyName, Guid? value)
    {
        if (value.HasValue)
        {
            payload[propertyName] = value.Value.ToString("D");
        }
    }
}
