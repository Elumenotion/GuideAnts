using System.IO.Compression;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

public sealed class RequiredGuidesAssistantsSeeder : IRequiredGuidesAssistantsSeeder
{
    private const string BootstrapRootRelativePath = "Resources/bootstrap";
    private const string GuidesFolderName = "guides";
    private const string AssistantsFolderName = "assistants";

    private readonly IWebHostEnvironment _environment;
    private readonly IGuideExportImportService _guideExportImportService;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<RequiredGuidesAssistantsSeeder> _logger;

    public RequiredGuidesAssistantsSeeder(
        IWebHostEnvironment environment,
        IGuideExportImportService guideExportImportService,
        ApplicationDbContext dbContext,
        ILogger<RequiredGuidesAssistantsSeeder> logger)
    {
        _environment = environment;
        _guideExportImportService = guideExportImportService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var bootstrapRoot = Path.Combine(_environment.ContentRootPath, BootstrapRootRelativePath);
        if (!Directory.Exists(bootstrapRoot))
        {
            _logger.LogInformation(
                "Required bootstrap resources folder not found at {Path}. Skipping required guide/assistant seed.",
                bootstrapRoot);
            return;
        }

        await SeedGuidesAsync(Path.Combine(bootstrapRoot, GuidesFolderName), cancellationToken);
        await SeedAssistantsAsync(Path.Combine(bootstrapRoot, AssistantsFolderName), cancellationToken);
    }

    private async Task SeedGuidesAsync(string guidesFolder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(guidesFolder))
        {
            _logger.LogInformation("Bootstrap guides folder not found at {Path}.", guidesFolder);
            return;
        }

        foreach (var seedSource in EnumerateSeedSources(guidesFolder))
        {
            await using var stream = await OpenSeedArchiveStreamAsync(seedSource, cancellationToken);
            var guideName = await TryReadManifestNameAsync(stream, cancellationToken);
            stream.Position = 0;

            if (string.IsNullOrWhiteSpace(guideName))
            {
                throw new InvalidOperationException(
                    $"Bootstrap guide seed '{seedSource.DisplayName}' is missing manifest name.");
            }

            var exists = await _dbContext.Assistants
                .AnyAsync(a => a.Kind == AssistantKind.Guide && a.Name == guideName, cancellationToken);
            if (exists)
            {
                _logger.LogInformation(
                    "Required guide '{GuideName}' already exists. Skipping seed {SeedName}.",
                    guideName,
                    seedSource.DisplayName);
                continue;
            }

            _logger.LogInformation(
                "Importing required bootstrap guide '{GuideName}' from seed {SeedName}.",
                guideName,
                seedSource.DisplayName);

            await _guideExportImportService.ImportGuideAsync(stream);
        }
    }

    private async Task SeedAssistantsAsync(string assistantsFolder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(assistantsFolder))
        {
            _logger.LogInformation("Bootstrap assistants folder not found at {Path}.", assistantsFolder);
            return;
        }

        foreach (var seedSource in EnumerateSeedSources(assistantsFolder))
        {
            await using var stream = await OpenSeedArchiveStreamAsync(seedSource, cancellationToken);
            var assistantName = await TryReadManifestNameAsync(stream, cancellationToken);
            stream.Position = 0;

            if (string.IsNullOrWhiteSpace(assistantName))
            {
                throw new InvalidOperationException(
                    $"Bootstrap assistant seed '{seedSource.DisplayName}' is missing manifest name.");
            }

            var exists = await _dbContext.Assistants
                .AnyAsync(a => a.Kind == AssistantKind.Assistant && a.Name == assistantName, cancellationToken);
            if (exists)
            {
                _logger.LogInformation(
                    "Required assistant '{AssistantName}' already exists. Skipping seed {SeedName}.",
                    assistantName,
                    seedSource.DisplayName);
                continue;
            }

            _logger.LogInformation(
                "Importing required bootstrap assistant '{AssistantName}' from seed {SeedName}.",
                assistantName,
                seedSource.DisplayName);

            await _guideExportImportService.ImportAssistantAsync(stream);
        }
    }

    private sealed record SeedSource(string Path, string DisplayName, bool IsDirectory);

    private static IEnumerable<SeedSource> EnumerateSeedSources(string folderPath)
    {
        var archives = Directory.EnumerateFiles(folderPath, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(path => new SeedSource(path, Path.GetFileName(path), IsDirectory: false));

        var folders = Directory.EnumerateDirectories(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, "manifest.json")))
            .Select(path => new SeedSource(path, Path.GetFileName(path), IsDirectory: true));

        return archives.Concat(folders)
            .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<MemoryStream> OpenSeedArchiveStreamAsync(SeedSource seedSource, CancellationToken cancellationToken)
    {
        if (!seedSource.IsDirectory)
        {
            var stream = new MemoryStream();
            await using var fileStream = File.OpenRead(seedSource.Path);
            await fileStream.CopyToAsync(stream, cancellationToken);
            stream.Position = 0;
            return stream;
        }

        var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var filePath in Directory.EnumerateFiles(seedSource.Path, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(seedSource.Path, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var sourceStream = File.OpenRead(filePath);
                await sourceStream.CopyToAsync(entryStream, cancellationToken);
            }
        }

        archiveStream.Position = 0;
        return archiveStream;
    }

    private static async Task<string?> TryReadManifestNameAsync(Stream zipStream, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry == null)
        {
            return null;
        }

        await using var manifestStream = manifestEntry.Open();
        using var reader = new StreamReader(manifestStream);
        var manifestJson = await reader.ReadToEndAsync(cancellationToken);

        using var doc = JsonDocument.Parse(manifestJson);
        if (!doc.RootElement.TryGetProperty("name", out var nameElement))
        {
            return null;
        }

        var name = nameElement.GetString();
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }
}
