using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.SystemGuide;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.SystemGuide;

public interface ISystemGuideSessionService
{
    Task<SystemGuideSessionDto?> GetSessionAsync(
        CurrentUserContext user,
        CancellationToken cancellationToken = default);

    Task<SystemGuideWorkspaceDto?> GetWorkspaceAsync(
        CurrentUserContext user,
        CancellationToken cancellationToken = default);
}

public sealed class SystemGuideSessionService : ISystemGuideSessionService
{
    private readonly ApplicationDbContext _db;
    private readonly IGuideAntsSystemSettingsStore _settingsStore;

    public SystemGuideSessionService(ApplicationDbContext db, IGuideAntsSystemSettingsStore settingsStore)
    {
        _db = db;
        _settingsStore = settingsStore;
    }

    public async Task<SystemGuideSessionDto?> GetSessionAsync(
        CurrentUserContext user,
        CancellationToken cancellationToken = default)
    {
        if (user.Role is Role.Pending)
        {
            return null;
        }

        var settings = await _settingsStore.GetAsync(cancellationToken);
        if (settings == null || !settings.IsFullySeeded)
        {
            return null;
        }

        var isAdminGuide = user.Role == Role.Admin;
        var publishedGuideId = isAdminGuide
            ? settings.AdminPublishedGuideId!.Value
            : settings.UserPublishedGuideId!.Value;
        var guideId = isAdminGuide
            ? settings.AdminGuideId!.Value
            : settings.UserGuideId!.Value;
        var notebookId = isAdminGuide
            ? settings.AdminNotebookId!.Value
            : settings.UserNotebookId!.Value;

        var guideName = await _db.Assistants
            .AsNoTracking()
            .Where(a => a.Id == guideId)
            .Select(a => a.Name)
            .SingleOrDefaultAsync(cancellationToken);

        if (guideName == null)
        {
            return null;
        }

        var commandMode = await _db.PublishedGuides
            .AsNoTracking()
            .Where(pg => pg.Id == publishedGuideId)
            .Select(pg => pg.CommandMode)
            .SingleOrDefaultAsync(cancellationToken);

        return new SystemGuideSessionDto(
            publishedGuideId,
            settings.ProjectId!.Value,
            notebookId,
            guideId,
            guideName,
            settings.ClientBridgeId,
            isAdminGuide,
            commandMode);
    }

    public async Task<SystemGuideWorkspaceDto?> GetWorkspaceAsync(
        CurrentUserContext user,
        CancellationToken cancellationToken = default)
    {
        if (user.Role != Role.Admin)
        {
            return null;
        }

        var settings = await _settingsStore.GetAsync(cancellationToken);
        if (settings?.ProjectId == null)
        {
            return null;
        }

        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == settings.ProjectId.Value && !p.Deleted && p.IsSystemProject)
            .Select(p => new { p.Id, p.Slug })
            .SingleOrDefaultAsync(cancellationToken);

        if (project == null)
        {
            return null;
        }

        return new SystemGuideWorkspaceDto(project.Id, project.Slug);
    }
}
