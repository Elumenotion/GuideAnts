using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.Endpoints;

public static class FileLineageEndpoints
{
    public static void MapFileLineageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lineage").WithTags("FileLineage");

        group.MapGet("/{eventId:guid}", async Task<Results<Ok<FileLineageEventDto>, NotFound, UnauthorizedHttpResult>> (
            Guid eventId,
            ApplicationDbContext db) =>
        {
            var ev = await db.FileLineageEvents.FindAsync(eventId);
            if (ev == null) return TypedResults.NotFound();


var dto = await ToDto(db, ev);
            return TypedResults.Ok(dto);
        });

        group.MapGet("/{eventId:guid}/download", async Task<IResult> (
            Guid eventId,
            ApplicationDbContext db,
            IConfiguration config) =>
        {
            var ev = await db.FileLineageEvents.FindAsync(eventId);
            if (ev == null) return Results.NotFound();


var storageRoot = config["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
            var resolved = await ResolveFileLocationAsync(db, ev, storageRoot);
            if (resolved == null) return Results.NotFound();

            var (path, fileName, contentType) = resolved.Value;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Results.File(stream, contentType, fileDownloadName: fileName);
        });
    }

    private static async Task<(string Path, string FileName, string ContentType)?> ResolveFileLocationAsync(ApplicationDbContext db, FileLineageEvent ev, string storageRoot)
    {
        if (!string.IsNullOrWhiteSpace(ev.StoragePath) && File.Exists(ev.StoragePath))
        {
            var fileName = System.IO.Path.GetFileName(ev.StoragePath);
            var ct = "application/octet-stream";
            return (ev.StoragePath, fileName, ct);
        }

        if (ev.FileKind == FileKind.Project)
        {
            var version = await db.ContentFileVersions.FirstOrDefaultAsync(v => v.ContentFileId == ev.FileId && v.VersionNumber == ev.VersionNumber);
            if (version != null)
            {
                var path = version.StoragePath ?? version.Path;
                if (File.Exists(path))
                {
                    var fileName = version.FileName;
                    var ct = version.ContentType;
                    return (path, fileName, ct);
                }
            }
        }
        else // Notebook
        {
            var nf = await db.NotebookFiles.FindAsync(ev.FileId);
            if (nf != null && ev.NotebookId.HasValue)
            {
                var rel = nf.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
                var projectSlug = await db.Projects
                    .Where(p => p.Id == ev.ProjectId)
                    .Select(p => p.Slug)
                    .FirstOrDefaultAsync() ?? ev.ProjectId.ToString();
                var notebookSlug = await db.Notebooks
                    .Where(n => n.Id == ev.NotebookId.Value)
                    .Select(n => n.Slug)
                    .FirstOrDefaultAsync() ?? ev.NotebookId.Value.ToString();
                var path = Path.Combine(storageRoot, projectSlug, notebookSlug, rel);
                if (!File.Exists(path))
                {
                    path = Path.Combine(storageRoot, ev.ProjectId.ToString(), "notebooks", ev.NotebookId.Value.ToString(), rel);
                }
                if (File.Exists(path))
                {
                    var fileName = Path.GetFileName(rel);
                    var ct = "application/octet-stream";
                    return (path, fileName, ct);
                }
            }
        }
        return null;
    }

    // Helper to translate entity to DTO with additional metadata
    static async Task<FileLineageEventDto> ToDto(ApplicationDbContext db, FileLineageEvent ev)
    {
        string fileName = ev.FileKind == FileKind.Project ?
            (await db.ContentFiles.Where(cf => cf.Id == ev.FileId).Select(cf => cf.FileName).FirstOrDefaultAsync()) ?? string.Empty :
            (await db.NotebookFiles.Where(nf => nf.Id == ev.FileId).Select(nf => nf.RelativePath).FirstOrDefaultAsync()) ?? string.Empty;

        // Resolve user display name (fallback to userId)
        var userDisplayName = await db.Users
            .Where(u => u.Email == ev.UserId || u.Id.ToString() == ev.UserId)
            .Select(u => u.Name)
            .FirstOrDefaultAsync() ?? ev.UserId;

        // Resolve notebook name when NotebookId is present
        string? notebookName = null;
        if (ev.NotebookId.HasValue)
        {
            notebookName = await db.Notebooks
                .Where(n => n.Id == ev.NotebookId.Value)
                .Select(n => n.Title)
                .FirstOrDefaultAsync();
        }

        var versionLabel = ev.VersionNumber.HasValue ? $"v{ev.VersionNumber}" : "–";

        return new FileLineageEventDto(
            ev.Id,
            ev.Action,
            ev.FileKind,
            ev.FileId,
            ev.VersionNumber,
            ev.ProjectId,
            ev.NotebookId,
            ev.StoragePath,
            ev.Timestamp,
            ev.UserId,
            userDisplayName,
            fileName,
            versionLabel,
            notebookName);
    }
} 
