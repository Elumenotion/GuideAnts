using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Reachability;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Single writer surface for Plane B (API-owned install memory of aux downloads). Every
/// read and write of <see cref="ServiceLocalModelInstallation"/> goes through this store
/// so the Plane A/B/C ownership rules live in one file, not folklore.
///
/// <para>Plane A (selections in ServiceModes) is owned by IApplicationSettingsService and is
/// NEVER written here.<see cref="MarkObservedAsync"/> stamps only the observation columns —
/// it cannot touch a selection, an instruction, or a desired state (D3/D4; T-A2).</para>
/// </summary>
public interface IServiceLocalModelInstallationStore
{
    /// <summary>Insert-or-update a row keyed (serviceId, modelRef). API-originated writes only.</summary>
    Task<ServiceLocalModelInstallation> UpsertAsync(
        string serviceId,
        string modelRef,
        string contentKind,
        string? catalogId,
        string? catalogVersion,
        string? requestedRevision,
        string state,
        string? operationId = null,
        CancellationToken cancellationToken = default);

    Task<ServiceLocalModelInstallation?> TryGetAsync(string serviceId, string modelRef, CancellationToken cancellationToken = default);

    Task RemoveAsync(string serviceId, string modelRef, CancellationToken cancellationToken = default);

    /// <summary>Finds the row whose last download operation is this engine operation id.</summary>
    Task<ServiceLocalModelInstallation?> FindByOperationAsync(string serviceId, string operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceLocalModelInstallation>> ListForServiceAsync(string serviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plane C writes observation truth only. Never ServiceModes; never State.
    /// present=true → ObservedState "present"; false → "absent"; null (unreachable/unknown)
    /// leaves ObservedState untouched but stamps ObservedUtc/Error.
    /// </summary>
    Task MarkObservedAsync(string serviceId, string modelRef, bool? present, string? error, DateTime observedUtc, CancellationToken cancellationToken = default);

    /// <summary>Terminal-success enrichment: artifacts path/size from the admin listing.</summary>
    Task SetInstalledAsync(string serviceId, string modelRef, string? targetDirectory, string artifactsJson, CancellationToken cancellationToken = default);

    /// <summary>Terminal failure at download/operation time.</summary>
    Task MarkFailedAsync(string serviceId, string modelRef, string? error, CancellationToken cancellationToken = default);
}

public sealed class ServiceLocalModelInstallationStore : IServiceLocalModelInstallationStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServiceLocalModelInstallationStore> _logger;

    public ServiceLocalModelInstallationStore(IServiceScopeFactory scopeFactory, ILogger<ServiceLocalModelInstallationStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ServiceLocalModelInstallation> UpsertAsync(
        string serviceId, string modelRef, string contentKind, string? catalogId,
        string? catalogVersion, string? requestedRevision, string state,
        string? operationId = null, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ServiceLocalModelInstallations
            .FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.ModelRef == modelRef, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new ServiceLocalModelInstallation
            {
                ServiceId = serviceId,
                ModelRef = modelRef,
                ContentKind = contentKind,
                CatalogId = catalogId,
                CatalogVersion = catalogVersion,
                RequestedRevision = requestedRevision,
                State = state,
                LastOperationId = operationId,
            };
            db.ServiceLocalModelInstallations.Add(row);
        }
        else
        {
            row.ContentKind = contentKind;
            if (!string.IsNullOrWhiteSpace(catalogId)) row.CatalogId = catalogId;
            if (!string.IsNullOrWhiteSpace(catalogVersion)) row.CatalogVersion = catalogVersion;
            if (!string.IsNullOrWhiteSpace(requestedRevision)) row.RequestedRevision = requestedRevision;
            row.State = state;
            if (!string.IsNullOrWhiteSpace(operationId)) row.LastOperationId = operationId;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task<ServiceLocalModelInstallation?> TryGetAsync(string serviceId, string modelRef, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ServiceLocalModelInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.ModelRef == modelRef, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RemoveAsync(string serviceId, string modelRef, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ServiceLocalModelInstallations
            .FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.ModelRef == modelRef, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            db.ServiceLocalModelInstallations.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ServiceLocalModelInstallation?> FindByOperationAsync(string serviceId, string operationId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ServiceLocalModelInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.LastOperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ServiceLocalModelInstallation>> ListForServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ServiceLocalModelInstallations
            .AsNoTracking()
            .Where(x => x.ServiceId == serviceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkObservedAsync(string serviceId, string modelRef, bool? present, string? error, DateTime observedUtc, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ServiceLocalModelInstallations
            .FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.ModelRef == modelRef, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return; // no install row: nothing to observe (never invent one from observation)
        }

        row.ObservedState = present is true ? "present" : present is false ? "absent" : row.ObservedState;
        row.ObservedUtc = observedUtc;
        row.ObservedError = error;
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetInstalledAsync(string serviceId, string modelRef, string? targetDirectory, string artifactsJson, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ServiceLocalModelInstallations
            .FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.ModelRef == modelRef, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.State = "Installed";
        row.TargetDirectory = targetDirectory;
        row.ArtifactsJson = artifactsJson;
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(string serviceId, string modelRef, string? error, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ServiceLocalModelInstallations
            .FirstOrDefaultAsync(x => x.ServiceId == serviceId && x.ModelRef == modelRef, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.State = "Failed";
        row.ObservedError = error;
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
