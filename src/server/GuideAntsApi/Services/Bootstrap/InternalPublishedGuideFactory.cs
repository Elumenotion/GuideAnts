using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Seeder-only factory for published guides with <see cref="PublishedGuideAuthMode.AppIdentity"/>.
/// This is the only code path allowed to set AppIdentity (D-GG-A).
/// </summary>
internal sealed class InternalPublishedGuideFactory
{
    public const int DefaultMaxTurns = 50;

    private readonly ApplicationDbContext _dbContext;

    public InternalPublishedGuideFactory(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    internal async Task<PublishedGuide> EnsureAppIdentityPublishedGuideAsync(
        Guid projectId,
        Guid guideId,
        Guid? preferredNotebookId,
        string guideName,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.PublishedGuides
            .Include(pg => pg.Notebook)
            .Where(pg => pg.GuideId == guideId && pg.Notebook.ProjectId == projectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != null)
        {
            RepairPublishedGuideRow(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return existing;
        }

        if (preferredNotebookId.HasValue)
        {
            var preferredNotebook = await _dbContext.Notebooks
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    n => n.Id == preferredNotebookId.Value && n.ProjectId == projectId,
                    cancellationToken);

            if (preferredNotebook != null)
            {
                var publishedFromPreferred = CreatePublishedGuide(guideId, preferredNotebook.Id);
                _dbContext.PublishedGuides.Add(publishedFromPreferred);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return publishedFromPreferred;
            }
        }

        var notebook = await _dbContext.Notebooks
            .FirstOrDefaultAsync(
                n => n.ProjectId == projectId && n.GuideId == guideId,
                cancellationToken);

        if (notebook == null)
        {
            notebook = new Notebook
            {
                ProjectId = projectId,
                Title = $"Published {guideName}",
                Slug = await GenerateUniqueNotebookSlugAsync(projectId, guideName, cancellationToken),
                Description = $"Published guide notebook for {guideName}",
                GuideId = guideId,
                Created = DateTime.UtcNow
            };
            _dbContext.Notebooks.Add(notebook);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var publishedGuide = CreatePublishedGuide(guideId, notebook.Id);
        _dbContext.PublishedGuides.Add(publishedGuide);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return publishedGuide;
    }

    private static PublishedGuide CreatePublishedGuide(Guid guideId, Guid notebookId)
    {
        return new PublishedGuide
        {
            GuideId = guideId,
            NotebookId = notebookId,
            Created = DateTime.UtcNow,
            Active = true,
            FriendlyName = null,
            DisplayMode = "full",
            CommandMode = true,
            ShowTurnNavigation = true,
            Collapsible = false,
            AuthMode = PublishedGuideAuthMode.AppIdentity,
            MaxTurns = DefaultMaxTurns
        };
    }

    private static void RepairPublishedGuideRow(PublishedGuide publishedGuide)
    {
        publishedGuide.Active = true;
        publishedGuide.FriendlyName = null;
        publishedGuide.DisplayMode = "full";
        publishedGuide.CommandMode = true;
        publishedGuide.ShowTurnNavigation = true;
        publishedGuide.Collapsible = false;
        publishedGuide.AuthMode = PublishedGuideAuthMode.AppIdentity;

        if (!publishedGuide.MaxTurns.HasValue)
        {
            publishedGuide.MaxTurns = DefaultMaxTurns;
        }
    }

    private async Task<string> GenerateUniqueNotebookSlugAsync(
        Guid projectId,
        string title,
        CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(title);
        var slug = baseSlug;
        var suffix = 1;

        while (await _dbContext.Notebooks.AnyAsync(
                   n => n.ProjectId == projectId && n.Slug == slug,
                   cancellationToken))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }

    private static string Slugify(string title)
    {
        var chars = title
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }
}
