using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.SystemGuide;

public interface ISystemProjectAccessGuard
{
    Task<IResult?> EnsureReadAccessAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IResult?> EnsureDeleteAllowedAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class SystemProjectAccessGuard : ISystemProjectAccessGuard
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _currentUserService;

    public SystemProjectAccessGuard(ApplicationDbContext db, ICurrentUserService currentUserService)
    {
        _db = db;
        _currentUserService = currentUserService;
    }

    public async Task<IResult?> EnsureReadAccessAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var isSystemProject = await _db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId && !p.Deleted && p.IsSystemProject, cancellationToken);

        if (!isSystemProject)
        {
            return null;
        }

        var user = await _currentUserService.GetCurrentUserAsync(cancellationToken);
        if (user == null)
        {
            return Results.Unauthorized();
        }

        if (user.Role == Role.Admin)
        {
            return null;
        }

        return Results.NotFound();
    }

    public async Task<IResult?> EnsureDeleteAllowedAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var isSystemProject = await _db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId && !p.Deleted && p.IsSystemProject, cancellationToken);

        if (!isSystemProject)
        {
            return null;
        }

        return Results.Problem(
            title: "System project cannot be deleted",
            detail: "The GuideAnts System project is protected and cannot be removed.",
            statusCode: StatusCodes.Status400BadRequest);
    }
}
